using System;
using System.IO;
using CodeBrix.Audio.FlacWriter.Internal;
using CodeBrix.Audio.Wave;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Audio.FlacWriter.Tests;

public class FlacStreamAudioFileWriterTests
{
    private static FlacStreamAudioFileWriter NewWriter(Stream output, int channels) =>
        new FlacStreamAudioFileWriter(output, new WaveFormat(44100, 16, channels), new FlacEncoderOptions());

    [Fact]
    public void Write_accepts_writes_that_split_frames()
    {
        //Arrange
        float[] tone = TestAudio.Sine(440, 0.5f, 44100, 2, 44100);
        using MemoryStream output = new MemoryStream();

        //Act
        using (FlacStreamAudioFileWriter writer = NewWriter(output, 2))
        {
            for (int offset = 0; offset < tone.Length; offset += 333)
            {
                writer.Write(tone, offset, Math.Min(333, tone.Length - offset));
            }

            writer.SamplesWritten.Should().Be(tone.Length);
        }

        //Assert
        TestAudio.Decode(output.ToArray()).Samples.Should().Equal(TestAudio.Quantize(tone, 16));
    }

    [Fact]
    public void WaveFormat_is_the_format_passed_in()
    {
        //Arrange
        WaveFormat format = new WaveFormat(48000, 16, 1);
        using MemoryStream output = new MemoryStream();

        //Act
        using FlacStreamAudioFileWriter writer = new FlacStreamAudioFileWriter(output, format, new FlacEncoderOptions());

        //Assert
        writer.WaveFormat.Should().BeSameAs(format);
    }

    [Fact]
    public void Write_after_Finish_throws()
    {
        //Arrange
        using MemoryStream output = new MemoryStream();
        FlacStreamAudioFileWriter writer = NewWriter(output, 1);
        writer.Finish();

        //Act
        Action write = () => writer.Write(new float[4]);

        //Assert
        write.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Write_rejects_a_bad_buffer_range()
    {
        //Arrange
        using MemoryStream output = new MemoryStream();
        using FlacStreamAudioFileWriter writer = NewWriter(output, 1);
        Action nullBuffer = () => writer.Write(null, 0, 0);
        Action badOffset = () => writer.Write(new float[4], 5, 0);
        Action badCount = () => writer.Write(new float[4], 2, 3);

        //Assert
        nullBuffer.Should().Throw<ArgumentNullException>();
        badOffset.Should().Throw<ArgumentOutOfRangeException>();
        badCount.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Finish_twice_is_harmless()
    {
        //Arrange
        using MemoryStream output = new MemoryStream();
        FlacStreamAudioFileWriter writer = NewWriter(output, 1);
        writer.Finish();
        long length = output.Length;

        //Act
        writer.Dispose();

        //Assert
        output.Length.Should().Be(length);
    }
}
