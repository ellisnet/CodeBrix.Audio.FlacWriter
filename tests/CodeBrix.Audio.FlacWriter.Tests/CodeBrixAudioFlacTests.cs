using System.IO;
using CodeBrix.Audio.FlacWriter.Interop;
using CodeBrix.Audio.Wave;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Audio.FlacWriter.Tests;

// Registration is process-wide and permanent, so these assert what is true after registering themselves.
public class CodeBrixAudioFlacTests
{
    [Fact]
    public void Register_makes_flac_writable_by_file_name()
    {
        //Act
        CodeBrixAudioFlac.Register();

        //Assert
        CodeBrixAudioFlac.IsRegistered.Should().BeTrue();
        AudioFileWriterRegistry.Supports("tune.flac").Should().BeTrue();
        AudioFileWriterRegistry.Resolve(".flac").Should().BeOfType<FlacAudioFileWriterFactory>();
    }

    [Fact]
    public void Register_is_idempotent()
    {
        //Arrange
        CodeBrixAudioFlac.Register();
        IAudioFileWriterFactory first = AudioFileWriterRegistry.Resolve(".flac");

        //Act
        CodeBrixAudioFlac.Register();
        CodeBrixAudioFlac.Register();

        //Assert
        AudioFileWriterRegistry.Resolve(".flac").Should().BeSameAs(first);
    }

    [Fact]
    public void Register_leaves_the_built_in_writers_in_place()
    {
        //Act
        CodeBrixAudioFlac.Register();

        //Assert
        AudioFileWriterRegistry.Supports(".wav").Should().BeTrue();
        AudioFileWriterRegistry.Supports(".aiff").Should().BeTrue();
    }

    [Fact]
    public void registry_writes_a_decodable_flac_by_file_name()
    {
        //Arrange
        CodeBrixAudioFlac.Register();
        using MemoryStream output = new MemoryStream();
        float[] tone = TestAudio.Sine(440, 0.5f, 48000, 2, 48000);

        //Act
        using (IAudioFileWriter writer = AudioFileWriterRegistry.Create("render.flac", output, 48000, 2))
        {
            writer.Write(tone);
        }

        //Assert
        TestAudio.Decoded decoded = TestAudio.Decode(output.ToArray());
        decoded.SampleRate.Should().Be(48000);
        decoded.BitsPerSample.Should().Be(16);
        decoded.ReportedFrames.Should().Be(48000);
        decoded.Samples.Should().Equal(TestAudio.Quantize(tone, 16));
    }

    [Fact]
    public void EnsureLoaded_accepts_the_shipped_contract_version()
    {
        //Act
        CodeBrixAudioFlac.EnsureLoaded();

        //Assert
        FlacNative.ApiVersion().Should().Be(FlacLibrary.ExpectedApiVersion);
        FlacLibrary.IsLoaded.Should().BeTrue();
    }

    [Fact]
    public void NativeLibraryVersion_is_a_libflac_version_number()
        => CodeBrixAudioFlac.NativeLibraryVersion.Should().MatchRegex(@"^\d+\.\d+(\.\d+)?");

    [Fact]
    public void NativeLibraryPath_is_known_once_loaded()
    {
        //Act
        CodeBrixAudioFlac.EnsureLoaded();

        //Assert
        CodeBrixAudioFlac.NativeLibraryPath.Should().Contain("codebrix_flac");
    }
}
