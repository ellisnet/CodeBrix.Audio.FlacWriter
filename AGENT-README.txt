================================================================================
AGENT-README: CodeBrix.Audio.FlacWriter
A Guide for AI Coding Agents - CONSUMING the
CodeBrix.Audio.FlacWriter.BsdLicenseForever NuGet package
================================================================================

OVERVIEW
========
CodeBrix.Audio.FlacWriter is a lossless FLAC ENCODER for .NET 10 or later. It
has two modes:

  STREAM MODE - FlacEncoder: interleaved samples in, a complete .flac stream
  out to any writable System.IO.Stream. Plugs a ".flac" writer into
  CodeBrix.Audio's AudioFileWriterRegistry so .flac files can be written by
  file name.

  PACKET MODE - FlacPacketEncoder: interleaved samples in, one FLAC frame per
  packet out (with its first-sample position and sample count), plus the FLAC
  stream header as codec-private data - for authoring a container that carries
  FLAC as packets.

It is encode-only and always lossless. Reading, decoding and playing .flac
files is already part of CodeBrix.Audio.Core (FlacFileReader,
AudioFileReader, the playback engine) and needs nothing from this package.

The encoder itself is native code: the package carries one native library per
platform under runtimes/<rid>/native/, built from the Xiph.Org reference
libFLAC source. The application needs nothing beyond the package and chooses
nothing - natives for every supported runtime identifier are inside it, and
NuGet / the .NET runtime pick the right one (on Android it is packed into the
APK).


INSTALLATION
============
PackageId:  CodeBrix.Audio.FlacWriter.BsdLicenseForever

    dotnet add package CodeBrix.Audio.FlacWriter.BsdLicenseForever

IMPORTANT: the NuGet package id is CodeBrix.Audio.FlacWriter.BsdLicenseForever
(NOT "CodeBrix.Audio.FlacWriter"). The assembly and primary namespace are
CodeBrix.Audio.FlacWriter.

NuGet dependencies (pulled in automatically, no version pinning needed in the
consuming project):
  - CodeBrix.Audio.Core.MitLicenseForever  (WaveFormat, IAudioFileWriter,
                                            AudioFileWriterRegistry, and the
                                            managed FLAC DECODER
                                            FlacFileReader)

License: BSD 3-Clause (SPDX: BSD-3-Clause). The native libraries are built
from the upstream library named in OVERVIEW, also BSD 3-Clause; its licence
text ships beside every native binary as LICENSE-Flac.txt.

Requirements: .NET 10 or later, on one of the runtime identifiers the package
has a native for:
    win-x64, win-arm64, osx-x64, osx-arm64,
    linux-x64, linux-arm64, linux-riscv64,
    android-x64, android-arm64 (Android 13 / API 33 and later)
Anything else (32-bit processes, linux-musl, iOS, WebAssembly) throws a
FlacEncoderException on first use, naming the platform.


KEY NAMESPACES / USINGS
=======================
    using CodeBrix.Audio.FlacWriter;   // FlacEncoder, FlacPacketEncoder,
                                       // FlacPacket, FlacEncoderOptions,
                                       // FlacEncoderException,
                                       // FlacAudioFileWriterFactory,
                                       // CodeBrixAudioFlac
    using CodeBrix.Audio.Wave;         // (from Core) AudioFileWriterRegistry,
                                       // IAudioFileWriter, WaveFormat,
                                       // FlacFileReader (to read back)

Note: CodeBrix.Audio.Core has its own CodeBrix.Audio.Flac namespace (its
decoder internals). That is a different namespace; this package's API is
entirely in CodeBrix.Audio.FlacWriter.


CORE API REFERENCE
==================

SAMPLE INPUT (both encoders)
----------------------------
Every Encode method takes FRAME-INTERLEAVED samples (L0 R0 L1 R1 ...), returns
the number of WHOLE FRAMES consumed (sample count / Channels), and leaves a
trailing partial frame unconsumed - pass it again at the start of the next
call. Any length is fine (large inputs are split internally).

    int Encode(ReadOnlySpan<float> interleavedSamples)
        Floats in [-1, 1], quantised to BitsPerSample bits as
        round(sample * 2^(BitsPerSample-1)) (ties to even), clipped to the
        integer range (so 1.0 -> the largest positive value); NaN -> 0. No
        dither. Lossless from that point on.
    int EncodeInt32(ReadOnlySpan<int> interleavedSamples)
        Integers that ALREADY hold BitsPerSample-bit values (e.g. -32768..32767
        at 16 bits, -8388608..8388607 at 24). Encoded exactly. A value outside
        the range throws ArgumentOutOfRangeException and NOTHING from that call
        is encoded.
    int EncodeInt16(ReadOnlySpan<short> interleavedSamples)
        16-bit integers, encoded exactly. At BitsPerSample 16 they are stored
        as-is; at a wider depth they are shifted up (24-bit output = input *
        256). BitsPerSample below 16 throws InvalidOperationException.

FlacEncoder : IDisposable   (sealed) - STREAM MODE
--------------------------------------------------
    FlacEncoder(Stream output, int channels, int sampleRate,
                FlacEncoderOptions options = null)
        Creates the native encoder and IMMEDIATELY writes the stream header
        ("fLaC" + STREAMINFO + VORBIS_COMMENT [+ SEEKTABLE] [+ PADDING]) to
        output, with placeholder totals.
        output      writable stream; NOT owned (the caller closes it, after
                    disposing the encoder). Seekable is better (see Flush) but
                    not required. Writing may start mid-stream: patch offsets
                    are relative to output.Position at construction.
        channels    1..8.
        sampleRate  1..655350, e.g. 44100 or 48000.
        options     null = defaults (16-bit, level 5, verify on, subset).
        Throws ArgumentNullException (output null), ArgumentException (stream
        not writable, bad comment, seek table without an estimate),
        ArgumentOutOfRangeException (channels, sampleRate or an option out of
        range), FlacEncoderException (native library missing, or the encoder
        refuses the combination - NativeCode -4, e.g. a block size or bit
        depth outside the streamable subset).

    Encode / EncodeInt32 / EncodeInt16   (see SAMPLE INPUT)
        Write every FLAC frame that completes. Throw ObjectDisposedException
        after Dispose, InvalidOperationException after Flush,
        FlacEncoderException on a native failure or a verify mismatch.

    void Flush()
        ENDS THE STREAM: encodes the last (short) frame and writes it. Then,
        IF output.CanSeek, seeks back, writes the final STREAMINFO (total
        samples, MD5 of the audio, min/max frame size) and SEEKTABLE over the
        placeholders, returns to the end, and sets HeaderIsFinal = true. If the
        stream cannot seek, the placeholders stay (total samples 0, MD5 all
        zero): still a valid FLAC stream that decoders play, it just does not
        state its own length. Flushes output. Idempotent.

    void Dispose()
        Calls Flush() if it has not run, then releases the native encoder. The
        output stream stays open. Idempotent.

    ReadOnlyMemory<byte> StreamHeader { get; }
        The header bytes ("fLaC" up to the first audio frame). Before Flush:
        placeholder totals. After Flush: the FINAL header, whether or not it
        could be written into output - its LENGTH NEVER CHANGES, so a caller
        that wrote to a non-seekable stream can overwrite the first
        StreamHeader.Length bytes of the saved file with it later. Readable
        after Dispose once the stream was finished.
    bool HeaderIsFinal { get; }      true once Flush patched the output.
    int Channels { get; }
    int SampleRate { get; }
    int BitsPerSample { get; }
    long FramesWritten { get; }      frames accepted so far.
    TimeSpan Duration { get; }       FramesWritten / SampleRate.
    bool IsFinished { get; }         true after Flush or Dispose.
    bool IsDisposed { get; }

FlacPacketEncoder : IDisposable   (sealed) - PACKET MODE
--------------------------------------------------------
    FlacPacketEncoder(int channels, int sampleRate,
                      FlacEncoderOptions options = null)
        Same ranges and exceptions as FlacEncoder, no stream. Nothing is
        written anywhere: the packets and the header are the whole output.

    Encode / EncodeInt32 / EncodeInt16   (see SAMPLE INPUT)
        Queue every FLAC frame that completes as a packet.

    bool TryReadPacket(out FlacPacket packet)
        Takes the OLDEST queued packet; false (packet = default) when none is
        queued. Drain after each Encode call and again after Finish.
    int PendingPackets { get; }      packets waiting.

    void Finish()
        Encodes the last (short) frame into the queue and finalises
        StreamHeader. Queued packets can still be taken afterwards; Encode
        afterwards throws InvalidOperationException. Idempotent.

    ReadOnlyMemory<byte> StreamHeader { get; }
        "fLaC", then STREAMINFO (always the FIRST block, 34 bytes), then the
        other metadata blocks - the container's codec-private data.
        Placeholder totals before Finish, final after; same length either way.
        Readable after Dispose once finished.

    void Dispose()
        Releases the native encoder and DISCARDS untaken packets. It does NOT
        finish the stream - call Finish() and drain first. Idempotent.

    Channels, SampleRate, BitsPerSample, FramesWritten, Duration, IsFinished,
    IsDisposed - as FlacEncoder.

    GUARANTEE: StreamHeader followed by every packet in order is byte for byte
    the file FlacEncoder writes to a seekable stream for the same input and
    options.

FlacPacket   (readonly struct)
------------------------------
    FlacPacket(ReadOnlyMemory<byte> data, long firstSample, int sampleCount)
    ReadOnlyMemory<byte> Data   exactly one FLAC frame (sync code to CRC-16).
    long FirstSample            index of its first sample, in frames - the
                                container timestamp in samples (divide by
                                SampleRate for seconds). Contiguous: each
                                packet's FirstSample = previous FirstSample +
                                previous SampleCount, starting at 0.
    int SampleCount             frames it decodes to (the block size; the last
                                packet is usually shorter).

FlacEncoderOptions   (sealed, settable properties)
---------------------------------------------------
    int BitsPerSample                4..32, default 16. The streamable subset
                                     allows 8, 12, 16, 20, 24, 32; set
                                     StreamableSubset = false for others.
    int CompressionLevel             0 (fastest) .. 8 (smallest), default 5.
                                     Always lossless.
    int BlockSize                    0 (default) = the level's own size (4096
                                     at the usual levels), else 16..65535. The
                                     subset allows <= 4608 at <= 48 kHz and
                                     <= 16384 above.
    bool Verify                      default TRUE: every frame is decoded
                                     while encoding and compared with the
                                     input. Set false for speed.
    bool StreamableSubset            default true (what every player must
                                     accept).
    long TotalSamplesEstimate        frames expected, or 0 (default) =
                                     unknown. Lets STREAMINFO carry a total
                                     before finishing (useful for a non-seekable
                                     output) and sizes the seek table.
    int PaddingBytes                 PADDING block size, 0 (default) = none,
                                     max 16777215. A few KB leave room for tag
                                     editors.
    long SeekTableSamplesBetweenPoints
                                     SEEKTABLE spacing in frames, 0 (default) =
                                     no table. REQUIRES TotalSamplesEstimate > 0
                                     (ArgumentException otherwise). Filled in
                                     at Flush when the output can seek.
    IDictionary<string, string> Comments
                                     Vorbis comments, e.g. ["TITLE"] = "...".
                                     Names: ASCII 0x20..0x7D, no '='. Values:
                                     any text without NUL, written as UTF-8.
                                     Default: empty (ordinal) dictionary. The
                                     vendor string is written automatically.
    Constants: MinimumBitsPerSample (4), MaximumBitsPerSample (32),
               DefaultBitsPerSample (16), MinimumCompressionLevel (0),
               MaximumCompressionLevel (8), DefaultCompressionLevel (5),
               MinimumBlockSize (16), MaximumBlockSize (65535),
               MaximumPaddingBytes (16777215), MaximumTotalSamples (2^36-1).
    Options are validated when the encoder (or factory) is created and read
    once; later changes do not affect an existing encoder.

FlacEncoderException : Exception
--------------------------------
    int NativeCode    the native result code, or 0 when the failure was not a
                      native call (e.g. the native library could not be
                      loaded):
                      -1 argument out of range
                      -2 out of memory
                      -3 call not valid in the current state
                      -4 the encoder refused the combination of settings
                      -5 VERIFY MISMATCH - a frame did not decode back to its
                         input; the output must be discarded
                      -6 any other native encoder failure
    string NativeState  the encoder's own state name for the failure, e.g.
                      "FLAC__STREAM_ENCODER_INIT_STATUS_NOT_STREAMABLE", or ""
                      (never null). Also appended to Message.
    bool IsVerifyMismatch   NativeCode == -5.

CodeBrixAudioFlac   (static)
----------------------------
    void Register()
        Loads and checks the native encoder, then registers a
        FlacAudioFileWriterFactory for ".flac" with AudioFileWriterRegistry.
        Idempotent and thread-safe. Throws FlacEncoderException if the native
        library cannot be loaded (message names the RID and every path tried).
    void EnsureLoaded()
        Just the native load + contract-version check, without registering.
    bool IsRegistered { get; }
    string NativeLibraryVersion { get; }   native encoder version string, e.g.
                                           a dotted version number (loads the
                                           native).
    string NativeLibraryPath { get; }      where the native was loaded from,
                                           or null before it has been.

FlacAudioFileWriterFactory : IAudioFileWriterFactory   (sealed)
---------------------------------------------------------------
    FlacAudioFileWriterFactory()                        default options
    FlacAudioFileWriterFactory(FlacEncoderOptions options)   copied
    FlacEncoderOptions Options { get; }   a COPY of the factory's settings.
    IReadOnlyList<string> Extensions      [".flac"]
    bool RequiresSeekableStream           false (seekable gets the totals and
                                          MD5; non-seekable gets placeholders)
    WaveFormat DefaultFormat(int sampleRate, int channels)
        PCM at Options.BitsPerSample (16 by default).
    IAudioFileWriter Create(Stream stream, WaveFormat format)
        Rate and channels from format. Bit depth: format.BitsPerSample when
        format is PCM (e.g. new WaveFormat(48000, 24, 2) writes 24-bit),
        otherwise (IEEE float) Options.BitsPerSample. The writer's WaveFormat
        is the stored PCM format. The stream is not closed by the writer. The
        writer accepts any sample count per Write (it carries a split frame to
        the next call); IAudioFileWriter.Finish / Dispose ends the file.


COMPLETE EXAMPLES
=================

1. Encode a generated tone to a 24-bit file
-------------------------------------------
    using System;
    using System.IO;
    using CodeBrix.Audio.FlacWriter;

    const int rate = 48000, channels = 2;
    float[] samples = new float[rate * channels];          // one second
    for (int i = 0; i < rate; i++)
    {
        float v = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / rate);
        samples[i * 2] = v;
        samples[i * 2 + 1] = v;
    }

    var options = new FlacEncoderOptions { BitsPerSample = 24, CompressionLevel = 8 };
    options.Comments["TITLE"] = "A440";

    using FileStream file = File.Create("tone.flac");
    using (var encoder = new FlacEncoder(file, channels, rate, options))
    {
        encoder.Encode(samples);
    }                // Dispose ends the stream and patches the header in place

2. Exact 16-bit PCM, streamed in blocks, seek table and padding
---------------------------------------------------------------
    var options = new FlacEncoderOptions
    {
        TotalSamplesEstimate = totalFrames,          // needed for a seek table
        SeekTableSamplesBetweenPoints = 44100 * 10,  // a point every 10 s
        PaddingBytes = 8192,
    };
    options.Comments["ARTIST"] = "Studio B";

    using FileStream file = File.Create("session.flac");
    using (var encoder = new FlacEncoder(file, 2, 44100, options))
    {
        short[] block = new short[4096 * 2];
        while (FillNextBlock(block, out int samplesInBlock))   // your source
        {
            encoder.EncodeInt16(block.AsSpan(0, samplesInBlock));
        }
    }

3. A non-seekable destination, header fixed up afterwards
---------------------------------------------------------
    byte[] header;
    using (var encoder = new FlacEncoder(pipe, 2, 44100))
    {
        encoder.Encode(samples);
        encoder.Flush();
        // encoder.HeaderIsFinal == false: the pipe got placeholder totals
        header = encoder.StreamHeader.ToArray();      // the final header
    }
    // later, on the saved copy (same length, so it overwrites in place):
    using (var saved = File.Open("saved.flac", FileMode.Open))
    {
        saved.Write(header, 0, header.Length);
    }

4. Packet mode for a container
------------------------------
    using var encoder = new FlacPacketEncoder(2, 48000,
        new FlacEncoderOptions { BitsPerSample = 24 });

    foreach (int[] chunk in Source())                 // 24-bit values
    {
        encoder.EncodeInt32(chunk);
        while (encoder.TryReadPacket(out FlacPacket p))
            container.WritePacket(p.Data, p.FirstSample, p.SampleCount);
    }

    encoder.Finish();
    while (encoder.TryReadPacket(out FlacPacket p))
        container.WritePacket(p.Data, p.FirstSample, p.SampleCount);

    container.SetCodecPrivate(encoder.StreamHeader);  // final header

5. Write .flac by file name through CodeBrix.Audio
--------------------------------------------------
    using System.IO;
    using CodeBrix.Audio.FlacWriter;
    using CodeBrix.Audio.Wave;

    CodeBrixAudioFlac.Register();       // once, at start-up

    using var stream = File.Create("mix.flac");
    using (IAudioFileWriter writer =
           AudioFileWriterRegistry.Create("mix.flac", stream, 44100, 2))
    {
        writer.Write(samples);          // float[] or ReadOnlySpan<float>
    }

    // 24-bit by file name: pass a PCM format.
    using (IAudioFileWriter writer =
           AudioFileWriterRegistry.Create("mix24.flac", stream24, new WaveFormat(44100, 24, 2)))
    {
        writer.Write(samples);
    }

    // Different settings for every .flac written by name: register your own
    // factory AFTER Register().
    AudioFileWriterRegistry.Register(
        new FlacAudioFileWriterFactory(new FlacEncoderOptions { CompressionLevel = 8 }));

6. Read it back (Core, no extra package)
----------------------------------------
    using CodeBrix.Audio.Wave;

    using var reader = new FlacFileReader("tone.flac");
    // reader.WaveFormat: PCM, file's rate/channels; reader.SourceBitsPerSample
    // reader.Tags["TITLE"]

7. Diagnose a missing native
----------------------------
    try
    {
        CodeBrixAudioFlac.EnsureLoaded();
        Console.WriteLine(CodeBrixAudioFlac.NativeLibraryVersion);
        Console.WriteLine(CodeBrixAudioFlac.NativeLibraryPath);
    }
    catch (FlacEncoderException ex)
    {
        Console.WriteLine(ex.Message);  // names the RID and every path tried
    }


MINIMUM VIABLE PROJECT TEMPLATE
===============================
    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup>
        <OutputType>Exe</OutputType>
        <TargetFramework>net10.0</TargetFramework>
      </PropertyGroup>
      <ItemGroup>
        <PackageReference Include="CodeBrix.Audio.FlacWriter.BsdLicenseForever" Version="*" />
      </ItemGroup>
    </Project>

    // Program.cs
    using System;
    using System.IO;
    using CodeBrix.Audio.FlacWriter;

    float[] tone = new float[44100];
    for (int i = 0; i < tone.Length; i++)
        tone[i] = 0.4f * MathF.Sin(2 * MathF.PI * 440 * i / 44100f);

    using FileStream file = File.Create("a440.flac");
    using var encoder = new FlacEncoder(file, 1, 44100);
    encoder.Encode(tone);

(Pin a real version instead of "*" in a real project.)


PERFORMANCE TIPS
================
- Verify is ON by default and adds a full decode of every frame to the
  encoding work. Leave it on for archival output; turn it off
  (Verify = false) for throw-away renders.
- Compression levels 0-2 are much faster than 6-8 for a few percent more size;
  5 (the default) is the usual balance. Every level decodes equally fast.
- Feed blocks of a few thousand frames or more. Very large single calls are
  fine - they are split internally.
- EncodeInt32 / EncodeInt16 skip the float quantisation; use them when you
  already have integer PCM.
- Encoding is CPU-bound and single-threaded per encoder. Encode several files
  in parallel with one encoder per thread; never share one encoder between
  threads.
- Pass a buffered stream (FileStream is buffered by default).


COMMON PITFALLS TO AVOID
========================
- Forgetting to Dispose (or Flush). Without it the last frame is never written
  and STREAMINFO keeps its placeholders. Use a using block; dispose the encoder
  BEFORE the stream (nest the usings that way).
- Treating Flush() as a mid-stream flush. It ENDS the stream; Encode afterwards
  throws InvalidOperationException.
- Writing to a non-seekable stream and expecting the total length / MD5 in the
  file. They cannot be written without seeking: HeaderIsFinal stays false. Set
  TotalSamplesEstimate if you know the length, or overwrite the start of the
  saved file with StreamHeader afterwards (example 3).
- Disposing a FlacPacketEncoder without Finish(): the last packet and the final
  header are lost. Finish, drain TryReadPacket, THEN read StreamHeader.
- Passing integer samples at the wrong width to EncodeInt32 - e.g. 16-bit
  values while BitsPerSample = 24 (quiet audio), or 24-bit values while
  BitsPerSample = 16 (ArgumentOutOfRangeException). Use EncodeInt16 for 16-bit
  input at any depth >= 16.
- Asking for a seek table without TotalSamplesEstimate -> ArgumentException.
- A non-subset BlockSize (e.g. 8192 at 44.1 kHz) or a bit depth such as 17 with
  StreamableSubset left on -> FlacEncoderException, NativeCode -4,
  NativeState "...NOT_STREAMABLE". Set StreamableSubset = false deliberately.
- Catching FlacEncoderException and keeping the output when IsVerifyMismatch
  is true. A verify mismatch means the encoded file is NOT the input; discard
  it.
- Expecting dither. Float input is rounded, not dithered; dither yourself before
  encoding if you are reducing a higher-resolution signal to 16 bits.
- Calling CodeBrixAudioFlac.Register() and expecting playback changes. Reading
  and playing .flac is Core's job and works without this package; Register
  only adds WRITING by file name.
- Trimmed or single-file publishes that drop runtimes/: the native must still
  reach the output. If the loader cannot find it, the exception lists every
  path it tried.


WHAT THIS PACKAGE DOES NOT DO
=============================
- Decode or play FLAC (CodeBrix.Audio.Core does that).
- Lossy encoding (FLAC is lossless; for lossy see the Vorbis and Opus
  packages of the CodeBrix.Audio family).
- Ogg FLAC (FLAC inside Ogg pages), or any other container: packet mode hands
  you the frames, the container is yours.
- Multithreaded encoding of one stream.
- Resample, remix or dither: the stream is written at the rate, channel count
  and bit depth given.
- Write PICTURE, CUESHEET or APPLICATION metadata blocks, or edit the metadata
  of an existing file.


WORKING EXAMPLES ON GITHUB
==========================
Test project:
https://github.com/ellisnet/CodeBrix.Audio.FlacWriter/tree/main/tests/CodeBrix.Audio.FlacWriter.Tests

  FlacEncoderTests.cs
      Sample-exact round trips (1-6 channels, 44.1/48 kHz, 16/24-bit, levels
      0/5/8, verify on/off) through Core's reader and through ffmpeg/ffprobe;
      float quantisation; EncodeInt16 widening; STREAMINFO totals + MD5 patched
      on seekable streams and placeholders on forward-only ones; StreamHeader;
      comments (non-ASCII), padding, seek table, block size, subset refusal;
      argument validation and dispose semantics.
  FlacPacketEncoderTests.cs
      Packet counts and positions, one frame per packet, header + packets ==
      the stream-mode file byte for byte, header shape, Finish/Dispose.
  FlacEncoderOptionsTests.cs
      Every option's range and comment-name rules.
  FlacAudioFileWriterFactoryTests.cs
      The ".flac" factory: default format, PCM bit depth, factory comments,
      forward-only streams.
  CodeBrixAudioFlacTests.cs
      Register, writing .flac by file name through AudioFileWriterRegistry,
      native load diagnostics.
  TestAudio.cs / FlacStreamHeader.cs / Ffmpeg.cs
      Generating test tones, decoding with Core's FlacFileReader, parsing the
      FLAC header, and the ffmpeg cross-check.


QUICK REFERENCE CARD
====================
--- Stream mode ---
Create:     var enc = new FlacEncoder(stream, channels, sampleRate, options);
Feed:       enc.Encode(floats) / enc.EncodeInt32(ints) / enc.EncodeInt16(shorts)
End:        enc.Flush();   // or Dispose(); patches header if stream.CanSeek
Header:     enc.StreamHeader, enc.HeaderIsFinal
Progress:   enc.FramesWritten, enc.Duration

--- Packet mode ---
Create:     var enc = new FlacPacketEncoder(channels, sampleRate, options);
Feed:       enc.EncodeInt32(ints); while (enc.TryReadPacket(out FlacPacket p)) ...
End:        enc.Finish(); drain TryReadPacket; codecPrivate = enc.StreamHeader
Packet:     p.Data, p.FirstSample, p.SampleCount

--- Options ---
Depth:      new FlacEncoderOptions { BitsPerSample = 24 }        // 4..32
Size/speed: CompressionLevel = 0..8 (5), BlockSize = 0 | 16..65535
Safety:     Verify = true (default)
Extras:     PaddingBytes, TotalSamplesEstimate + SeekTableSamplesBetweenPoints
Tags:       options.Comments["TITLE"] = "...";

--- Write by file name ---
Register:   CodeBrixAudioFlac.Register();
Write:      AudioFileWriterRegistry.Create("x.flac", stream, rate, channels)
Custom:     AudioFileWriterRegistry.Register(new FlacAudioFileWriterFactory(options));

--- Diagnostics ---
Load check: CodeBrixAudioFlac.EnsureLoaded();
Versions:   CodeBrixAudioFlac.NativeLibraryVersion / NativeLibraryPath
Errors:     FlacEncoderException.NativeCode (-1..-6, 0 = not native),
            .NativeState, .IsVerifyMismatch

Target: .NET 10 or later
License: BSD 3-Clause


================================================================================
END OF AGENT-README
================================================================================
