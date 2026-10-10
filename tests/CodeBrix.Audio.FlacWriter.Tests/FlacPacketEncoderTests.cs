using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Audio.FlacWriter.Tests;

public class FlacPacketEncoderTests
{
    [Fact]
    public void packets_sample_counts_sum_to_the_frame_count()
    {
        //Arrange
        const int frames = 44100 + 999;
        int[] input = TestAudio.SineInt(440, 44100, 2, frames, 16);

        //Act
        TestAudio.EncodePackets(input, 2, 44100, null, out List<FlacPacket> packets);

        //Assert
        packets.Sum(p => (long)p.SampleCount).Should().Be(frames);
        packets.Should().OnlyContain(p => p.SampleCount > 0 && p.Data.Length > 0);
    }

    [Fact]
    public void packets_FirstSample_is_monotonic_and_contiguous()
    {
        //Arrange
        int[] input = TestAudio.SineInt(440, 48000, 1, 48000 * 2, 24);

        //Act
        TestAudio.EncodePackets(input, 1, 48000, new FlacEncoderOptions { BitsPerSample = 24 }, out List<FlacPacket> packets);

        //Assert
        long expected = 0;
        foreach (FlacPacket packet in packets)
        {
            packet.FirstSample.Should().Be(expected);
            expected += packet.SampleCount;
        }
    }

    [Fact]
    public void every_packet_is_one_whole_flac_frame()
    {
        //Arrange
        int[] input = TestAudio.SineInt(440, 44100, 2, 30000, 16);

        //Act
        TestAudio.EncodePackets(input, 2, 44100, null, out List<FlacPacket> packets);

        //Assert
        foreach (FlacPacket packet in packets)
        {
            ReadOnlySpan<byte> data = packet.Data.Span;
            data[0].Should().Be(0xFF);
            (data[1] & 0xFE).Should().Be(0xF8);
        }
    }

    [Theory]
    [InlineData(1, 44100, 16, 5, true)]
    [InlineData(2, 44100, 16, 8, false)]
    [InlineData(2, 48000, 24, 0, true)]
    public void header_plus_packets_is_byte_identical_to_the_stream_mode_file(int channels, int rate, int bits, int level, bool verify)
    {
        //Arrange
        int[] input = TestAudio.SineInt(440, rate, channels, rate + 1234, bits);
        FlacEncoderOptions options = new FlacEncoderOptions { BitsPerSample = bits, CompressionLevel = level, Verify = verify };
        options.Comments["TITLE"] = "Packet mode";

        //Act
        byte[] header = TestAudio.EncodePackets(input, channels, rate, options, out List<FlacPacket> packets);
        byte[] streamFile = TestAudio.Encode(input, channels, rate, options);
        using MemoryStream joined = new MemoryStream();
        joined.Write(header);
        foreach (FlacPacket packet in packets) joined.Write(packet.Data.Span);

        //Assert
        joined.ToArray().Should().Equal(streamFile);
    }

    [Fact]
    public void StreamHeader_has_the_shape_a_container_expects()
    {
        //Arrange
        int[] input = TestAudio.SineInt(440, 44100, 2, 20000, 16);

        //Act
        byte[] header = TestAudio.EncodePackets(input, 2, 44100, null, out List<FlacPacket> _);
        FlacStreamHeader parsed = FlacStreamHeader.Parse(header);

        //Assert
        header.AsSpan(0, 4).SequenceEqual("fLaC"u8).Should().BeTrue();
        parsed.Blocks[0].Type.Should().Be(0);
        parsed.Blocks[0].Length.Should().Be(34);
        parsed.Blocks[^1].IsLast.Should().BeTrue();
        parsed.Length.Should().Be(header.Length);
        parsed.TotalSamples.Should().Be(20000);
        parsed.Md5.Should().Equal(TestAudio.FlacMd5(input, 16));
    }

    [Fact]
    public void StreamHeader_before_Finish_has_placeholders_and_the_same_length()
    {
        //Arrange
        using FlacPacketEncoder encoder = new FlacPacketEncoder(2, 44100);
        int before = encoder.StreamHeader.Length;
        FlacStreamHeader placeholder = FlacStreamHeader.Parse(encoder.StreamHeader.Span);

        //Act
        encoder.EncodeInt32(TestAudio.SineInt(440, 44100, 2, 10000, 16));
        encoder.Finish();

        //Assert
        placeholder.TotalSamples.Should().Be(0);
        encoder.StreamHeader.Length.Should().Be(before);
        FlacStreamHeader.Parse(encoder.StreamHeader.Span).TotalSamples.Should().Be(10000);
    }

    [Fact]
    public void decoding_header_plus_packets_gives_back_the_input()
    {
        //Arrange
        float[] tone = TestAudio.Sine(440, 0.7f, 48000, 2, 30000);
        using FlacPacketEncoder encoder = new FlacPacketEncoder(2, 48000);
        List<FlacPacket> packets = new List<FlacPacket>();

        //Act
        encoder.Encode(tone);
        encoder.Finish();
        while (encoder.TryReadPacket(out FlacPacket packet)) packets.Add(packet);
        using MemoryStream joined = new MemoryStream();
        joined.Write(encoder.StreamHeader.Span);
        foreach (FlacPacket packet in packets) joined.Write(packet.Data.Span);

        //Assert
        TestAudio.Decode(joined.ToArray()).Samples.Should().Equal(TestAudio.Quantize(tone, 16));
    }

    [Fact]
    public void TryReadPacket_returns_false_when_nothing_is_queued()
    {
        //Arrange
        using FlacPacketEncoder encoder = new FlacPacketEncoder(1, 44100);

        //Act
        bool got = encoder.TryReadPacket(out FlacPacket packet);

        //Assert
        got.Should().BeFalse();
        packet.Data.Length.Should().Be(0);
        encoder.PendingPackets.Should().Be(0);
    }

    [Fact]
    public void PendingPackets_counts_the_queue()
    {
        //Arrange
        using FlacPacketEncoder encoder = new FlacPacketEncoder(1, 44100, new FlacEncoderOptions { BlockSize = 1024 });

        //Act
        encoder.EncodeInt32(new int[4096]);

        //Assert
        encoder.PendingPackets.Should().BeGreaterThanOrEqualTo(3);
        encoder.FramesWritten.Should().Be(4096);
    }

    [Fact]
    public void Encode_after_Finish_throws_but_packets_still_drain()
    {
        //Arrange
        using FlacPacketEncoder encoder = new FlacPacketEncoder(1, 44100);
        encoder.EncodeInt32(new int[100]);
        encoder.Finish();
        Action encode = () => encoder.EncodeInt16(new short[4]);

        //Act
        bool got = encoder.TryReadPacket(out FlacPacket packet);

        //Assert
        encoder.IsFinished.Should().BeTrue();
        encode.Should().Throw<InvalidOperationException>();
        got.Should().BeTrue();
        packet.SampleCount.Should().Be(100);
    }

    [Fact]
    public void Dispose_twice_is_harmless_and_later_calls_throw()
    {
        //Arrange
        FlacPacketEncoder encoder = new FlacPacketEncoder(2, 44100);
        encoder.Dispose();

        //Act
        encoder.Dispose();
        Action read = () => encoder.TryReadPacket(out FlacPacket _);
        Action encode = () => encoder.Encode(new float[2]);
        Action header = () => _ = encoder.StreamHeader;

        //Assert
        encoder.IsDisposed.Should().BeTrue();
        read.Should().Throw<ObjectDisposedException>();
        encode.Should().Throw<ObjectDisposedException>();
        header.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void StreamHeader_survives_Dispose_after_Finish()
    {
        //Arrange
        FlacPacketEncoder encoder = new FlacPacketEncoder(2, 44100);
        encoder.Finish();

        //Act
        encoder.Dispose();

        //Assert
        FlacStreamHeader.Parse(encoder.StreamHeader.Span).Channels.Should().Be(2);
    }

    [Fact]
    public void constructor_rejects_bad_arguments()
    {
        //Arrange
        Action noChannels = () => new FlacPacketEncoder(0, 44100);
        Action noRate = () => new FlacPacketEncoder(1, -1);
        Action badBits = () => new FlacPacketEncoder(1, 44100, new FlacEncoderOptions { BitsPerSample = 33 });

        //Assert
        noChannels.Should().Throw<ArgumentOutOfRangeException>();
        noRate.Should().Throw<ArgumentOutOfRangeException>();
        badBits.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Duration_follows_FramesWritten()
    {
        //Arrange
        using FlacPacketEncoder encoder = new FlacPacketEncoder(2, 48000);

        //Act
        encoder.Encode(new float[48000 * 2]);

        //Assert
        encoder.Duration.Should().Be(TimeSpan.FromSeconds(1));
        encoder.Channels.Should().Be(2);
        encoder.SampleRate.Should().Be(48000);
        encoder.BitsPerSample.Should().Be(16);
    }
}
