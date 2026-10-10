using System;
using System.IO;
using CodeBrix.Audio.Wave;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Audio.FlacWriter.Tests;

public class FlacAudioFileWriterFactoryTests
{
    [Fact]
    public void Extensions_is_flac()
        => new FlacAudioFileWriterFactory().Extensions.Should().ContainSingle().Which.Should().Be(".flac");

    [Fact]
    public void RequiresSeekableStream_is_false()
        => new FlacAudioFileWriterFactory().RequiresSeekableStream.Should().BeFalse();

    [Fact]
    public void DefaultFormat_is_16_bit_pcm()
    {
        //Act
        WaveFormat format = new FlacAudioFileWriterFactory().DefaultFormat(48000, 2);

        //Assert
        format.Encoding.Should().Be(WaveFormatEncoding.Pcm);
        format.BitsPerSample.Should().Be(16);
        format.SampleRate.Should().Be(48000);
        format.Channels.Should().Be(2);
    }

    [Fact]
    public void DefaultFormat_follows_the_factory_bit_depth()
        => new FlacAudioFileWriterFactory(new FlacEncoderOptions { BitsPerSample = 24 }).DefaultFormat(44100, 1).BitsPerSample.Should().Be(24);

    [Fact]
    public void DefaultFormat_rejects_bad_arguments()
    {
        //Arrange
        FlacAudioFileWriterFactory factory = new FlacAudioFileWriterFactory();
        Action badRate = () => factory.DefaultFormat(0, 2);
        Action badChannels = () => factory.DefaultFormat(44100, 9);

        //Assert
        badRate.Should().Throw<ArgumentOutOfRangeException>();
        badChannels.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void constructor_validates_options()
    {
        //Arrange
        Action create = () => new FlacAudioFileWriterFactory(new FlacEncoderOptions { CompressionLevel = 12 });

        //Assert
        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Options_defaults_when_null_is_passed()
        => new FlacAudioFileWriterFactory(null).Options.BitsPerSample.Should().Be(16);

    [Fact]
    public void Options_is_a_copy_the_caller_cannot_change()
    {
        //Arrange
        FlacEncoderOptions mine = new FlacEncoderOptions { CompressionLevel = 2 };
        FlacAudioFileWriterFactory factory = new FlacAudioFileWriterFactory(mine);

        //Act
        mine.CompressionLevel = 7;
        factory.Options.CompressionLevel = 8;

        //Assert
        factory.Options.CompressionLevel.Should().Be(2);
    }

    [Fact]
    public void Create_rejects_bad_arguments()
    {
        //Arrange
        FlacAudioFileWriterFactory factory = new FlacAudioFileWriterFactory();
        WaveFormat format = new WaveFormat(44100, 16, 2);
        using MemoryStream readOnly = new MemoryStream(new byte[4], writable: false);
        Action nullStream = () => factory.Create(null, format);
        Action nullFormat = () => factory.Create(new MemoryStream(), null);
        Action notWritable = () => factory.Create(readOnly, format);
        Action tooManyChannels = () => factory.Create(new MemoryStream(), new WaveFormat(44100, 16, 9));

        //Assert
        nullStream.Should().Throw<ArgumentNullException>();
        nullFormat.Should().Throw<ArgumentNullException>();
        notWritable.Should().Throw<ArgumentException>();
        tooManyChannels.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Create_takes_the_bit_depth_of_a_pcm_format()
    {
        //Arrange
        FlacAudioFileWriterFactory factory = new FlacAudioFileWriterFactory();
        using MemoryStream output = new MemoryStream();
        float[] tone = TestAudio.Sine(440, 0.5f, 48000, 2, 12000);

        //Act
        using (IAudioFileWriter writer = factory.Create(output, new WaveFormat(48000, 24, 2)))
        {
            writer.WaveFormat.BitsPerSample.Should().Be(24);
            writer.Write(tone);
        }

        //Assert
        TestAudio.Decoded decoded = TestAudio.Decode(output.ToArray());
        decoded.BitsPerSample.Should().Be(24);
        decoded.Samples.Should().Equal(TestAudio.Quantize(tone, 24));
    }

    [Fact]
    public void Create_uses_the_factory_bit_depth_for_a_float_format()
    {
        //Arrange
        FlacAudioFileWriterFactory factory = new FlacAudioFileWriterFactory();
        using MemoryStream output = new MemoryStream();

        //Act
        using (IAudioFileWriter writer = factory.Create(output, WaveFormat.CreateIeeeFloatWaveFormat(44100, 1)))
        {
            writer.WaveFormat.Encoding.Should().Be(WaveFormatEncoding.Pcm);
            writer.Write(TestAudio.Sine(440, 0.5f, 44100, 1, 4410));
        }

        //Assert
        TestAudio.Decode(output.ToArray()).BitsPerSample.Should().Be(16);
    }

    [Fact]
    public void Create_writer_carries_the_factory_comments()
    {
        //Arrange
        FlacEncoderOptions options = new FlacEncoderOptions();
        options.Comments["ALBUM"] = "Factory settings";
        FlacAudioFileWriterFactory factory = new FlacAudioFileWriterFactory(options);
        using MemoryStream output = new MemoryStream();

        //Act
        using (IAudioFileWriter writer = factory.Create(output, factory.DefaultFormat(44100, 1)))
        {
            writer.Write(TestAudio.Sine(440, 0.5f, 44100, 1, 4410));
        }

        //Assert
        TestAudio.Decode(output.ToArray()).Tags["ALBUM"].Should().ContainSingle().Which.Should().Be("Factory settings");
    }

    [Fact]
    public void Create_on_a_forward_only_stream_writes_a_decodable_file()
    {
        //Arrange
        FlacAudioFileWriterFactory factory = new FlacAudioFileWriterFactory();
        ForwardOnlyStream output = new ForwardOnlyStream();
        float[] tone = TestAudio.Sine(440, 0.5f, 44100, 2, 10000);

        //Act
        using (IAudioFileWriter writer = factory.Create(output, factory.DefaultFormat(44100, 2)))
        {
            writer.Write(tone);
        }

        //Assert
        TestAudio.Decode(output.ToArray()).Samples.Should().Equal(TestAudio.Quantize(tone, 16));
    }
}
