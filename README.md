# CodeBrix.Audio.FlacWriter

Lossless FLAC encoding for [CodeBrix.Audio](https://github.com/ellisnet/CodeBrix.Audio): float or integer samples in, a standard `.flac` file out - or one FLAC frame per packet for a container - over a native FLAC encoder that ships inside the package for every supported platform.
CodeBrix.Audio.FlacWriter is provided as a .NET 10 library and associated `CodeBrix.Audio.FlacWriter.BsdLicenseForever` NuGet package.

CodeBrix.Audio.FlacWriter supports applications and assemblies that target Microsoft .NET version 10.0 and later.
Microsoft .NET version 10.0 is a Long-Term Supported (LTS) version of .NET, and was released on Nov 11, 2025; and will be actively supported by Microsoft until Nov 14, 2028.
Please update your C#/.NET code and projects to the latest LTS version of Microsoft .NET.

## Installation

```
dotnet add package CodeBrix.Audio.FlacWriter.BsdLicenseForever
```

Note that the NuGet package ID and the namespace are different - there is no package named plain `CodeBrix.Audio.FlacWriter`:

* NuGet package ID: `CodeBrix.Audio.FlacWriter.BsdLicenseForever`
* Assembly and primary namespace: `CodeBrix.Audio.FlacWriter` - i.e. `using CodeBrix.Audio.FlacWriter;`

XML documentation (IntelliSense) ships alongside the assembly.

The package pulls in the following automatically; no version pinning is needed in the consuming project:

* `CodeBrix.Audio.Core.MitLicenseForever` - the shared audio library whose file-writer registry this package adds `.flac` to. Reading and playing `.flac` files is already part of Core; this package adds writing.

## CodeBrix.Audio.FlacWriter supports:

* Lossless encoding of interleaved samples - 32-bit float (scaled to the chosen bit depth), 32-bit integer or 16-bit integer - to FLAC, written to any writable `Stream`
* 4- to 32-bit samples (16 and 24 are the everyday cases), 1 to 8 channels, any sample rate up to 655350 Hz
* Compression levels 0 to 8, a fixed block size if wanted, and verification while encoding - on by default - that decodes every frame and fails loudly on any mismatch
* The finished header written into place when the stream can seek - total sample count, MD5 signature of the audio, frame sizes, seek table - and a still-valid file when it cannot, with the final header available to put in place later
* An optional seek table, an optional padding block for later tag editing, and Vorbis comments (`TITLE`, `ARTIST`, `ALBUM`, ...) written as UTF-8
* Packet mode: one FLAC frame per packet with its sample position and length, plus the stream header as codec-private data, for container authoring
* Writing `.flac` by file name wherever CodeBrix.Audio writes audio by extension, after one `CodeBrixAudioFlac.Register()` call
* Native encoders in the package for Windows x64 and ARM64, macOS Intel and Apple Silicon, Linux x64, ARM64 and RISC-V 64, and Android 13+ ARM64 and x64 - the application chooses nothing

## Native assets

The package carries one native library per platform in the standard NuGet `runtimes/<rid>/native/` layout, each beside the licence text of the code it was built from. NuGet and the .NET runtime pick the right one; on Android it is packed into the APK. If the native cannot be found at run time, the exception names the runtime identifier and every path that was tried.

## Sample Code

### Encode a tone to a .flac file

```csharp
using System;
using System.IO;
using CodeBrix.Audio.FlacWriter;

const int sampleRate = 48000;
const int channels = 2;

float[] samples = new float[sampleRate * channels * 2];   // two seconds
for (int i = 0; i < sampleRate * 2; i++)
{
    float value = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / sampleRate);
    samples[i * 2] = value;
    samples[i * 2 + 1] = value;
}

var options = new FlacEncoderOptions { BitsPerSample = 24, CompressionLevel = 8 };
options.Comments["TITLE"] = "A440";

using FileStream file = File.Create("tone.flac");
using (var encoder = new FlacEncoder(file, channels, sampleRate, options))
{
    encoder.Encode(samples);
}   // Dispose ends the stream and writes the final header into place
```

### Encode FLAC frames as packets for a container

```csharp
using System;
using CodeBrix.Audio.FlacWriter;

int[] pcm = GetSixteenBitSamples();   // interleaved stereo, 44.1 kHz, values in the 16-bit range

using var encoder = new FlacPacketEncoder(2, 44100);
encoder.EncodeInt32(pcm);
encoder.Finish();

while (encoder.TryReadPacket(out FlacPacket packet))
{
    WritePacket(packet.Data, packet.FirstSample, packet.SampleCount);   // your container
}

ReadOnlyMemory<byte> codecPrivate = encoder.StreamHeader;   // "fLaC" + STREAMINFO + the other metadata
```

### Write .flac by file name

```csharp
using System.IO;
using CodeBrix.Audio.FlacWriter;
using CodeBrix.Audio.Wave;

CodeBrixAudioFlac.Register();   // once, at application start-up

float[] samples = RenderSomething();   // interleaved stereo float samples at 44.1 kHz

using var stream = File.Create("render.flac");
using (IAudioFileWriter writer = AudioFileWriterRegistry.Create("render.flac", stream, 44100, 2))
{
    writer.Write(samples);
}
```

## Documentation

The NuGet package includes `AGENT-README.txt`, a complete API reference and usage guide written for AI coding agents - point your agent at that file when it is writing code against this library.

Additional sample code and usage examples are available in the `CodeBrix.Audio.FlacWriter.Tests` project:
https://github.com/ellisnet/CodeBrix.Audio.FlacWriter/tree/main/tests/CodeBrix.Audio.FlacWriter.Tests

## License

CodeBrix.Audio.FlacWriter is licensed under the BSD 3-Clause License - see the
[LICENSE](https://github.com/ellisnet/CodeBrix.Audio.FlacWriter/blob/main/LICENSE) file.

For licensing and provenance information about the open source code included in
this package, see [THIRD-PARTY-NOTICES.txt](https://github.com/ellisnet/CodeBrix.Audio.FlacWriter/blob/main/THIRD-PARTY-NOTICES.txt).
