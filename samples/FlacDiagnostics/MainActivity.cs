using System;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CodeBrix.Audio.Android;
using CodeBrix.Audio.Playback;
using CodeBrix.Audio.Wave;
using global::Android.App;
using global::Android.OS;
using global::Android.Widget;
using AndroidBuild = global::Android.OS.Build;
using AndroidLog = global::Android.Util.Log;

namespace CodeBrix.Audio.FlacWriter.Diagnostics;

/// <summary>
/// One-screen device proof for CodeBrix.Audio.FlacWriter: loads the native encoder, encodes generated tones to
/// .flac files (two in stream mode, one in packet mode), decodes them back with CodeBrix.Audio.Core and compares
/// them sample-exact with the quantised input, plays the first one through CodeBrix.Audio.Android, and ends with a
/// "FLAC-DIAG RESULT:" line. Every line also goes to logcat under the tag FlacDiag.
/// </summary>
[Activity(Label = "CodeBrix FLAC Diagnostics", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    private const string LogTag = "FlacDiag";

    private TextView _log = null;
    private ScrollView _scroll = null;
    private Button _run = null;
    private int _running;
    private AndroidAudioFocus _focus = null;
    private AudioFilePlayer _player = null;

    /// <summary>A test tone, how it is encoded, and what is expected of it after the round trip.</summary>
    private sealed class ToneCase
    {
        public string Name { get; init; }
        public string Title { get; init; }
        public double Frequency { get; init; }
        public int SampleRate { get; init; }
        public int Channels { get; init; }
        public double Seconds { get; init; }
        public bool PacketMode { get; init; }
        public FlacEncoderOptions Options { get; init; }
        public string Path { get; set; }
        public int[] Expected { get; set; }
    }

    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        CodeBrixAndroidAudio.Initialize(this);
        _focus = new AndroidAudioFocus(this);

        var panel = new LinearLayout(this) { Orientation = global::Android.Widget.Orientation.Vertical };
        _run = new Button(this) { Text = "Run FLAC diagnostics" };
        _run.Click += (_, _) => StartRun();
        _log = new TextView(this) { Text = "" };
        _log.SetTextIsSelectable(true);
        _scroll = new ScrollView(this);
        _scroll.AddView(_log);
        panel.AddView(_run);
        panel.AddView(_scroll, new LinearLayout.LayoutParams(LinearLayout.LayoutParams.MatchParent, 0, 1f));
        SetContentView(panel);

        // Auto-run one second after launch so adb can drive the app without taps.
        new Handler(Looper.MainLooper).PostDelayed(StartRun, 1000);
    }

    private void StartRun()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _run.Enabled = false;
        Task.Run(() =>
        {
            try { RunDiagnostics(); }
            catch (Exception ex) { Log("FLAC-DIAG RESULT: FAIL: unexpected " + ex.GetType().Name + ": " + ex.Message); }
            finally
            {
                Volatile.Write(ref _running, 0);
                RunOnUiThread(() => _run.Enabled = true);
            }
        });
    }

    private void Log(string line)
    {
        AndroidLog.Info(LogTag, line);
        RunOnUiThread(() =>
        {
            _log.Append(line + "\n");
            _scroll.Post(() => _scroll.FullScroll(global::Android.Views.FocusSearchDirection.Down));
        });
    }

    /// <summary>The float-to-integer quantisation FlacEncoder documents: round half to even, clip, no dither.</summary>
    private static int[] Quantize(float[] samples, int bitsPerSample)
    {
        int[] result = new int[samples.Length];
        double scale = 1L << (bitsPerSample - 1);
        for (int i = 0; i < samples.Length; i++)
        {
            double value = Math.Round(samples[i] * scale);
            result[i] = (int)Math.Clamp(value, -scale, scale - 1);
        }

        return result;
    }

    /// <summary>Reads a little-endian signed integer of 1 to 4 bytes.</summary>
    private static int ReadSigned(byte[] buffer, int offset, int bytes)
    {
        if (bytes == 1) return buffer[offset] - 128;

        int value = 0;
        for (int b = 0; b < bytes; b++) value |= buffer[offset + b] << (8 * b);
        int unused = 32 - (8 * bytes);
        return (value << unused) >> unused;
    }

    private void RunDiagnostics()
    {
        string failure = null;
        void Fail(string reason) { failure ??= reason; Log("CHECK FAILED: " + reason); }

        Log("=== CodeBrix.Audio.FlacWriter device diagnostics ===");
        Log($"Device {AndroidBuild.Manufacturer} {AndroidBuild.Model}, ABI {AndroidBuild.SupportedAbis?[0]}, "
            + $"API {(int)AndroidBuild.VERSION.SdkInt}, process {RuntimeInformation.ProcessArchitecture}, "
            + $"runtime {RuntimeInformation.FrameworkDescription}");

        // (1) Load and register.
        try
        {
            CodeBrixAudioFlac.Register();
            Log("Step 1 Register: OK, IsRegistered=" + CodeBrixAudioFlac.IsRegistered);
            Log("Step 1 NativeLibraryVersion: " + CodeBrixAudioFlac.NativeLibraryVersion);
            Log("Step 1 NativeLibraryPath: " + (CodeBrixAudioFlac.NativeLibraryPath ?? "(null)"));
        }
        catch (Exception ex)
        {
            Log("Step 1 Register FAILED: " + ex);
            Log("FLAC-DIAG RESULT: FAIL: native load: " + ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
            return;
        }

        var cases = new[]
        {
            new ToneCase
            {
                Name = "tone-440hz-stereo-48k-16bit", Frequency = 440, SampleRate = 48000, Channels = 2, Seconds = 3,
                Options = new FlacEncoderOptions { BitsPerSample = 16, Verify = true },
            },
            new ToneCase
            {
                Name = "tone-660hz-mono-44k1-24bit-l8", Frequency = 660, SampleRate = 44100, Channels = 1, Seconds = 2,
                Options = new FlacEncoderOptions { BitsPerSample = 24, CompressionLevel = 8, Verify = true },
            },
            new ToneCase
            {
                Name = "tone-440hz-stereo-48k-16bit-packets", Frequency = 440, SampleRate = 48000, Channels = 2, Seconds = 3,
                Title = "tone-440hz-stereo-48k-16bit", // same comments as the stream case, so the bytes can match
                PacketMode = true,
                Options = new FlacEncoderOptions { BitsPerSample = 16, Verify = true },
            },
        };

        const float amplitude = 0.5011872f; // -6 dBFS peak
        double expectedRms = amplitude / Math.Sqrt(2);
        string externalDir = GetExternalFilesDir(null)?.AbsolutePath;

        foreach (ToneCase tone in cases)
        {
            // (2) Generate.
            int frames = (int)Math.Round(tone.SampleRate * tone.Seconds);
            float[] pcm = new float[frames * tone.Channels];
            for (int i = 0; i < frames; i++)
            {
                float v = (float)(amplitude * Math.Sin(2 * Math.PI * tone.Frequency * i / tone.SampleRate));
                for (int c = 0; c < tone.Channels; c++) pcm[(i * tone.Channels) + c] = v;
            }

            tone.Expected = Quantize(pcm, tone.Options.BitsPerSample);
            Log($"Step 2 {tone.Name}: generated {frames} frames, {tone.Channels} ch @ {tone.SampleRate} Hz, "
                + $"{tone.Frequency} Hz sine, -6 dBFS");

            // (3) Encode.
            tone.Path = Path.Combine(FilesDir.AbsolutePath, tone.Name + ".flac");
            try
            {
                tone.Options.Comments["TITLE"] = tone.Title ?? tone.Name;
                tone.Options.Comments["ENCODER_TEST"] = "CodeBrix FlacDiagnostics";
                int block = 4096 * tone.Channels; // feed in blocks, as a real producer would
                var watch = Stopwatch.StartNew();
                long framesWritten;
                TimeSpan duration;
                string modeText;
                if (!tone.PacketMode)
                {
                    bool headerFinal;
                    using (FileStream file = File.Create(tone.Path))
                    {
                        using (var encoder = new FlacEncoder(file, tone.Channels, tone.SampleRate, tone.Options))
                        {
                            for (int offset = 0; offset < pcm.Length; offset += block)
                            {
                                encoder.Encode(pcm.AsSpan(offset, Math.Min(block, pcm.Length - offset)));
                            }

                            encoder.Flush();
                            framesWritten = encoder.FramesWritten;
                            duration = encoder.Duration;
                            headerFinal = encoder.HeaderIsFinal;
                        }
                    }

                    modeText = $"stream, {tone.Options.BitsPerSample}-bit, level {tone.Options.CompressionLevel}, "
                        + $"verify {tone.Options.Verify}, HeaderIsFinal {headerFinal}";
                    if (!headerFinal) Fail($"{tone.Name}: HeaderIsFinal false on a seekable FileStream");
                }
                else
                {
                    var packets = new List<FlacPacket>();
                    byte[] header;
                    using (var encoder = new FlacPacketEncoder(tone.Channels, tone.SampleRate, tone.Options))
                    {
                        for (int offset = 0; offset < pcm.Length; offset += block)
                        {
                            encoder.Encode(pcm.AsSpan(offset, Math.Min(block, pcm.Length - offset)));
                            while (encoder.TryReadPacket(out FlacPacket packet)) packets.Add(packet);
                        }

                        encoder.Finish();
                        while (encoder.TryReadPacket(out FlacPacket packet)) packets.Add(packet);
                        framesWritten = encoder.FramesWritten;
                        duration = encoder.Duration;
                        header = encoder.StreamHeader.ToArray();
                    }

                    using (FileStream file = File.Create(tone.Path))
                    {
                        file.Write(header);
                        foreach (FlacPacket packet in packets) file.Write(packet.Data.Span);
                    }

                    long sampleSum = packets.Sum(p => (long)p.SampleCount);
                    bool monotonic = true;
                    long next = 0;
                    foreach (FlacPacket packet in packets)
                    {
                        if (packet.FirstSample != next) monotonic = false;
                        next = packet.FirstSample + packet.SampleCount;
                    }

                    modeText = $"PACKET, {tone.Options.BitsPerSample}-bit, level {tone.Options.CompressionLevel}, "
                        + $"verify {tone.Options.Verify}, header {header.Length} bytes, {packets.Count} packets, "
                        + $"sum SampleCount {sampleSum}, FirstSample contiguous {monotonic}";
                    if (sampleSum != frames) Fail($"{tone.Name}: packets' SampleCount sum {sampleSum} != {frames}");
                    if (!monotonic) Fail($"{tone.Name}: packet FirstSample values are not contiguous");
                    if (header.Length < 42 || header[0] != (byte)'f' || header[1] != (byte)'L' || header[2] != (byte)'a'
                        || header[3] != (byte)'C' || (header[4] & 0x7F) != 0 || header[7] != 34)
                    {
                        Fail($"{tone.Name}: stream header does not start with fLaC + a 34-byte STREAMINFO");
                    }
                }

                watch.Stop();
                long bytes = new FileInfo(tone.Path).Length;
                Log($"Step 3 {tone.Name}: {modeText}; {bytes} bytes, FramesWritten {framesWritten}, "
                    + $"Duration {duration.TotalSeconds:F3} s, {watch.Elapsed.TotalMilliseconds:F1} ms, path {tone.Path}");
                if (framesWritten != frames) Fail($"{tone.Name}: FramesWritten {framesWritten} != {frames}");
                if (bytes < 1000) Fail($"{tone.Name}: only {bytes} bytes written");

                if (tone.PacketMode && cases[0].Path != null && File.Exists(cases[0].Path))
                {
                    bool same = File.ReadAllBytes(tone.Path).AsSpan().SequenceEqual(File.ReadAllBytes(cases[0].Path));
                    Log($"Step 3 {tone.Name}: header + packets byte-identical to {cases[0].Name}.flac: {same}");
                    if (!same) Fail($"{tone.Name}: header + packets differ from the stream-mode file for the same input");
                }

                if (externalDir != null)
                {
                    string copy = Path.Combine(externalDir, tone.Name + ".flac");
                    File.Copy(tone.Path, copy, true);
                    Log($"Step 3 {tone.Name}: copied for adb pull to {copy}");
                }
            }
            catch (Exception ex)
            {
                Log($"Step 3 {tone.Name} encode FAILED: {ex}");
                Fail($"{tone.Name}: encode threw {ex.GetType().Name}: {ex.Message}");
                tone.Path = null;
                continue;
            }

            // (4) Decode back with CodeBrix.Audio.Core and compare sample-exact.
            try
            {
                var watch = Stopwatch.StartNew();
                using FileStream input = File.OpenRead(tone.Path);
                using var reader = new FlacFileReader(input);
                WaveFormat format = reader.WaveFormat;
                int channels = format.Channels;
                int rate = format.SampleRate;
                int sourceBits = reader.SourceBitsPerSample;
                int container = format.BitsPerSample / 8;
                int shift = format.BitsPerSample - sourceBits;
                long reportedFrames = reader.Length / format.BlockAlign;
                byte[] buffer = new byte[4096 * format.BlockAlign];
                var decoded = new List<int>(tone.Expected.Length);
                int read;
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i + container <= read; i += container) decoded.Add(ReadSigned(buffer, i, container) >> shift);
                }

                watch.Stop();
                int mismatches = 0;
                int firstMismatch = -1;
                int compared = Math.Min(decoded.Count, tone.Expected.Length);
                double sum = 0;
                double peak = 0;
                double scale = 1L << (sourceBits - 1);
                for (int i = 0; i < compared; i++)
                {
                    if (decoded[i] != tone.Expected[i])
                    {
                        mismatches++;
                        if (firstMismatch < 0) firstMismatch = i;
                    }

                    double s = decoded[i] / scale;
                    sum += s * s;
                    peak = Math.Max(peak, Math.Abs(s));
                }

                double rms = compared == 0 ? 0 : Math.Sqrt(sum / compared);
                long decodedFrames = decoded.Count / Math.Max(1, channels);
                bool exact = mismatches == 0 && decoded.Count == tone.Expected.Length;
                Log($"Step 4 {tone.Name}: decoded channels {channels}, rate {rate}, bits {sourceBits}, reported frames "
                    + $"{reportedFrames}, decoded frames {decodedFrames}, SAMPLE-EXACT {exact} ({mismatches} mismatches"
                    + (firstMismatch >= 0 ? $", first at sample {firstMismatch}" : "") + $"), RMS {rms:F4} "
                    + $"(expected {expectedRms:F4}), peak {peak:F4}, {watch.Elapsed.TotalMilliseconds:F1} ms");
                if (channels != tone.Channels) Fail($"{tone.Name}: decoded {channels} channels, expected {tone.Channels}");
                if (rate != tone.SampleRate) Fail($"{tone.Name}: decoded rate {rate}, expected {tone.SampleRate}");
                if (sourceBits != tone.Options.BitsPerSample) Fail($"{tone.Name}: decoded {sourceBits}-bit, expected {tone.Options.BitsPerSample}");
                if (reportedFrames != frames) Fail($"{tone.Name}: reader reports {reportedFrames} frames, expected {frames}");
                if (!exact) Fail($"{tone.Name}: not sample-exact ({decoded.Count} samples decoded, {tone.Expected.Length} expected, {mismatches} mismatches)");
                if (Math.Abs(rms - expectedRms) > expectedRms * 0.01) Fail($"{tone.Name}: RMS {rms:F4} not within 1% of {expectedRms:F4}");
            }
            catch (Exception ex)
            {
                Log($"Step 4 {tone.Name} decode FAILED: {ex}");
                Fail($"{tone.Name}: decode threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        // (5) Play the first file audibly through CodeBrix.Audio.Android (the shared output).
        try
        {
            if (cases[0].Path == null || !File.Exists(cases[0].Path)) throw new FileNotFoundException("first file was not written");
            bool focus = _focus.Request();
            Log("Step 5 audio focus granted: " + focus);
            using var ended = new ManualResetEventSlim(false);
            _player = new AudioFilePlayer();
            _player.PlaybackEnded += (_, _) => ended.Set();
            _player.Load(cases[0].Path);
            _player.Volume = 0.5f;
            Log($"Step 5 playback START: {cases[0].Name}, player Duration {_player.Duration.TotalSeconds:F3} s");
            var watch = Stopwatch.StartNew();
            _player.Play();
            bool natural = ended.Wait(_player.Duration + TimeSpan.FromSeconds(5));
            watch.Stop();
            Log($"Step 5 playback END: natural end {natural}, wall {watch.Elapsed.TotalSeconds:F3} s, "
                + $"state {_player.PlaybackState}, position {_player.Position.TotalSeconds:F3} s");
            if (!natural) Fail("playback did not reach its natural end within duration + 5 s");
            else if (watch.Elapsed < _player.Duration - TimeSpan.FromSeconds(0.5)) Fail($"playback ended after only {watch.Elapsed.TotalSeconds:F3} s");
            _player.Dispose();
            _player = null;
            _focus.Abandon();
        }
        catch (Exception ex)
        {
            Log("Step 5 playback FAILED: " + ex);
            Fail("playback threw " + ex.GetType().Name + ": " + ex.Message);
        }

        // (6) Verdict.
        Log(failure == null ? "FLAC-DIAG RESULT: PASS" : "FLAC-DIAG RESULT: FAIL: " + failure);
    }

    protected override void OnDestroy()
    {
        _player?.Dispose();
        _focus?.Dispose();
        SharedAudioOutput.Shutdown();
        base.OnDestroy();
    }
}
