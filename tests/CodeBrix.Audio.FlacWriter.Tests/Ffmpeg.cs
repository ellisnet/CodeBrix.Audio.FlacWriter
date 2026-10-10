using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace CodeBrix.Audio.FlacWriter.Tests;

/// <summary>
/// The second, independent decoder: the ffmpeg and ffprobe programs on the PATH. Tests that use them skip, with
/// the reason, on a machine that has neither.
/// </summary>
internal static class Ffmpeg
{
    private static readonly Lazy<bool> Present = new Lazy<bool>(() => CanRun("ffmpeg") && CanRun("ffprobe"));

    /// <summary>Skips the calling test when ffmpeg / ffprobe are not installed.</summary>
    public static void SkipUnlessPresent()
    {
        if (!Present.Value) Assert.Skip("ffmpeg and ffprobe are not on the PATH; the second-decoder checks need them.");
    }

    /// <summary>Decodes a FLAC file to raw little-endian PCM with ffmpeg, at the file's own bit depth.</summary>
    /// <param name="flac">The FLAC bytes.</param>
    /// <param name="bitsPerSample">16 (s16le) or 24 (s24le).</param>
    /// <param name="errors">Receives whatever ffmpeg wrote at log level "error" (empty when the decode was clean).</param>
    /// <returns>The interleaved decoded samples.</returns>
    public static int[] Decode(byte[] flac, int bitsPerSample, out string errors)
    {
        string path = WriteTemp(flac);
        try
        {
            string format = bitsPerSample == 16 ? "s16le" : "s24le";
            string codec = bitsPerSample == 16 ? "pcm_s16le" : "pcm_s24le";
            byte[] raw = Run("ffmpeg", ["-v", "error", "-nostdin", "-i", path, "-c:a", codec, "-f", format, "-"], out errors);

            int bytes = bitsPerSample / 8;
            int[] samples = new int[raw.Length / bytes];
            for (int i = 0; i < samples.Length; i++) samples[i] = TestAudio.ReadSigned(raw, i * bytes, bytes);
            return samples;
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Asks ffprobe what it sees in the first audio stream of a FLAC file.</summary>
    /// <param name="flac">The FLAC bytes.</param>
    /// <param name="errors">Receives whatever ffprobe wrote at log level "error".</param>
    /// <returns>The <c>key=value</c> pairs ffprobe printed.</returns>
    public static Dictionary<string, string> Probe(byte[] flac, out string errors)
    {
        string path = WriteTemp(flac);
        try
        {
            byte[] text = Run(
                "ffprobe",
                ["-v", "error", "-select_streams", "a:0", "-show_entries",
                 "stream=codec_name,channels,sample_rate,bits_per_raw_sample,duration_ts,duration", "-of", "default=nw=1", path],
                out errors);

            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in System.Text.Encoding.UTF8.GetString(text).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = line.IndexOf('=');
                if (equals > 0) values[line.Substring(0, equals).Trim()] = line.Substring(equals + 1).Trim();
            }

            return values;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTemp(byte[] flac)
    {
        string path = Path.Combine(Path.GetTempPath(), "codebrix-flacwriter-test-" + Guid.NewGuid().ToString("N") + ".flac");
        File.WriteAllBytes(path, flac);
        return path;
    }

    private static byte[] Run(string program, string[] arguments, out string errors)
    {
        ProcessStartInfo start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        using Process process = Process.Start(start);
        System.Threading.Tasks.Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using MemoryStream output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        errors = stderr.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0) errors = $"exit code {process.ExitCode}: {errors}";
        return output.ToArray();
    }

    private static bool CanRun(string program)
    {
        try
        {
            ProcessStartInfo start = new ProcessStartInfo(program)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-version");
            using Process process = Process.Start(start);
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
