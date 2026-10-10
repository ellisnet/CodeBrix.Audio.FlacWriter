using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Audio.FlacWriter.Tests;

public class FlacEncoderTests
{
    [Theory]
    [InlineData(1, 44100, 16, 5)]
    [InlineData(2, 44100, 16, 0)]
    [InlineData(2, 44100, 16, 8)]
    [InlineData(1, 48000, 24, 5)]
    [InlineData(2, 48000, 24, 8)]
    [InlineData(2, 48000, 16, 5)]
    [InlineData(6, 48000, 24, 0)]
    public void EncodeInt32_round_trips_sample_exact_through_the_core_reader(int channels, int sampleRate, int bits, int level)
    {
        //Arrange
        int frames = sampleRate + 777;
        int[] input = TestAudio.SineInt(440, sampleRate, channels, frames, bits);
        FlacEncoderOptions options = new FlacEncoderOptions { BitsPerSample = bits, CompressionLevel = level };

        //Act
        byte[] flac = TestAudio.Encode(input, channels, sampleRate, options);
        TestAudio.Decoded decoded = TestAudio.Decode(flac);

        //Assert
        decoded.Channels.Should().Be(channels);
        decoded.SampleRate.Should().Be(sampleRate);
        decoded.BitsPerSample.Should().Be(bits);
        decoded.ReportedFrames.Should().Be(frames);
        decoded.Samples.Should().Equal(input);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EncodeInt32_round_trips_with_verify_on_and_off(bool verify)
    {
        //Arrange
        int[] input = TestAudio.SineInt(1000, 44100, 2, 30000, 16);

        //Act
        byte[] flac = TestAudio.Encode(input, 2, 44100, new FlacEncoderOptions { Verify = verify });

        //Assert
        TestAudio.Decode(flac).Samples.Should().Equal(input);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    public void Encode_float_input_is_exact_after_quantisation(int bits)
    {
        //Arrange
        float[] tone = TestAudio.Sine(440, 0.8f, 44100, 2, 44100);
        tone[10] = 1.5f;
        tone[11] = -1.5f;
        tone[12] = 1.0f;
        tone[13] = -1.0f;
        tone[14] = float.NaN;
        using MemoryStream output = new MemoryStream();

        //Act
        using (FlacEncoder encoder = new FlacEncoder(output, 2, 44100, new FlacEncoderOptions { BitsPerSample = bits }))
        {
            encoder.Encode(tone).Should().Be(44100);
        }

        //Assert
        int[] expected = TestAudio.Quantize(tone, bits);
        expected[14] = 0;
        TestAudio.Decode(output.ToArray()).Samples.Should().Equal(expected);
    }

    [Fact]
    public void EncodeInt16_round_trips_exactly_at_16_bits()
    {
        //Arrange
        int[] wide = TestAudio.SineInt(440, 44100, 2, 20000, 16);
        short[] input = wide.Select(v => (short)v).ToArray();
        using MemoryStream output = new MemoryStream();

        //Act
        using (FlacEncoder encoder = new FlacEncoder(output, 2, 44100))
        {
            encoder.EncodeInt16(input).Should().Be(20000);
        }

        //Assert
        TestAudio.Decode(output.ToArray()).Samples.Should().Equal(wide);
    }

    [Fact]
    public void EncodeInt16_widens_losslessly_at_24_bits()
    {
        //Arrange
        short[] input = TestAudio.SineInt(440, 48000, 1, 9000, 16).Select(v => (short)v).ToArray();
        using MemoryStream output = new MemoryStream();

        //Act
        using (FlacEncoder encoder = new FlacEncoder(output, 1, 48000, new FlacEncoderOptions { BitsPerSample = 24 }))
        {
            encoder.EncodeInt16(input);
        }

        //Assert
        TestAudio.Decode(output.ToArray()).Samples.Should().Equal(input.Select(v => v * 256));
    }

    [Fact]
    public void EncodeInt16_below_16_bits_throws()
    {
        //Arrange
        using MemoryStream output = new MemoryStream();
        using FlacEncoder encoder = new FlacEncoder(output, 1, 44100, new FlacEncoderOptions { BitsPerSample = 12 });
        Action encode = () => encoder.EncodeInt16(new short[4]);

        //Assert
        encode.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EncodeInt32_rejects_a_sample_wider_than_the_bit_depth()
    {
        //Arrange
        using MemoryStream output = new MemoryStream();
        using FlacEncoder encoder = new FlacEncoder(output, 1, 44100);
        Action tooBig = () => encoder.EncodeInt32(new[] { 0, 32768 });
        Action tooSmall = () => encoder.EncodeInt32(new[] { -32769 });

        //Assert
        tooBig.Should().Throw<ArgumentOutOfRangeException>();
        tooSmall.Should().Throw<ArgumentOutOfRangeException>();
        encoder.FramesWritten.Should().Be(0);
    }

    [Fact]
    public void EncodeInt32_accepts_the_extremes_of_the_bit_depth()
    {
        //Arrange
        int[] input = Enumerable.Range(0, 5000).Select(i => i % 2 == 0 ? 32767 : -32768).ToArray();

        //Act
        byte[] flac = TestAudio.Encode(input, 1, 44100, null);

        //Assert
        TestAudio.Decode(flac).Samples.Should().Equal(input);
    }

    [Fact]
    public void Flush_on_a_seekable_stream_patches_streaminfo_totals_and_md5()
    {
        //Arrange
        const int frames = 44100 + 123;
        int[] input = TestAudio.SineInt(440, 44100, 2, frames, 16);
        using MemoryStream output = new MemoryStream();
        FlacEncoder encoder = new FlacEncoder(output, 2, 44100);
        FlacStreamHeader before = FlacStreamHeader.Parse(output.ToArray());

        //Act
        encoder.EncodeInt32(input);
        encoder.Flush();
        FlacStreamHeader after = FlacStreamHeader.Parse(output.ToArray());

        //Assert
        before.TotalSamples.Should().Be(0);
        before.Md5.Should().Equal(new byte[16]);
        encoder.HeaderIsFinal.Should().BeTrue();
        after.TotalSamples.Should().Be(frames);
        after.Md5.Should().Equal(TestAudio.FlacMd5(input, 16));
        after.SampleRate.Should().Be(44100);
        after.Channels.Should().Be(2);
        after.BitsPerSample.Should().Be(16);
        after.MinimumFrameSize.Should().BeGreaterThan(0);
        after.MaximumFrameSize.Should().BeGreaterThanOrEqualTo(after.MinimumFrameSize);
        output.Position.Should().Be(output.Length);
        encoder.Dispose();
    }

    [Fact]
    public void Flush_on_a_forward_only_stream_leaves_placeholders_and_still_decodes()
    {
        //Arrange
        int[] input = TestAudio.SineInt(440, 44100, 2, 30000, 16);
        ForwardOnlyStream output = new ForwardOnlyStream();

        //Act
        FlacEncoder encoder = new FlacEncoder(output, 2, 44100);
        encoder.EncodeInt32(input);
        encoder.Dispose();
        byte[] flac = output.ToArray();
        FlacStreamHeader header = FlacStreamHeader.Parse(flac);

        //Assert
        encoder.HeaderIsFinal.Should().BeFalse();
        header.TotalSamples.Should().Be(0);
        header.Md5.Should().Equal(new byte[16]);
        TestAudio.Decode(flac).Samples.Should().Equal(input);
    }

    [Fact]
    public void StreamHeader_after_Flush_is_the_final_header_on_either_kind_of_stream()
    {
        //Arrange
        int[] input = TestAudio.SineInt(440, 44100, 1, 20000, 16);
        using MemoryStream seekable = new MemoryStream();
        ForwardOnlyStream forward = new ForwardOnlyStream();
        FlacEncoder a = new FlacEncoder(seekable, 1, 44100);
        FlacEncoder b = new FlacEncoder(forward, 1, 44100);

        //Act
        a.EncodeInt32(input);
        b.EncodeInt32(input);
        a.Dispose();
        b.Dispose();
        byte[] seekableFile = seekable.ToArray();
        byte[] forwardFile = forward.ToArray();
        byte[] header = b.StreamHeader.ToArray();
        header.CopyTo(forwardFile, 0);

        //Assert
        a.StreamHeader.ToArray().Should().Equal(seekableFile.Take(a.StreamHeader.Length));
        header.Length.Should().Be(FlacStreamHeader.Parse(seekableFile).Length);
        forwardFile.Should().Equal(seekableFile);
    }

    [Fact]
    public void StreamHeader_before_Flush_carries_placeholder_totals()
    {
        //Arrange
        using MemoryStream output = new MemoryStream();
        using FlacEncoder encoder = new FlacEncoder(output, 2, 48000);

        //Act
        FlacStreamHeader header = FlacStreamHeader.Parse(encoder.StreamHeader.Span);

        //Assert
        header.TotalSamples.Should().Be(0);
        header.SampleRate.Should().Be(48000);
        header.Length.Should().Be((int)output.Length);
    }

    [Fact]
    public void Flush_patches_relative_to_where_the_stream_started()
    {
        //Arrange
        byte[] prefix = { 1, 2, 3, 4, 5, 6, 7 };
        int[] input = TestAudio.SineInt(440, 44100, 2, 10000, 16);
        using MemoryStream output = new MemoryStream();
        output.Write(prefix, 0, prefix.Length);

        //Act
        using (FlacEncoder encoder = new FlacEncoder(output, 2, 44100))
        {
            encoder.EncodeInt32(input);
        }

        byte[] all = output.ToArray();

        //Assert
        all.Take(prefix.Length).Should().Equal(prefix);
        all.Skip(prefix.Length).Should().Equal(TestAudio.Encode(input, 2, 44100, null));
    }

    [Fact]
    public void Encode_round_trips_comments_including_non_ascii()
    {
        //Arrange
        int[] input = TestAudio.SineInt(440, 44100, 1, 4410, 16);
        FlacEncoderOptions options = new FlacEncoderOptions();
        options.Comments["TITLE"] = "Round-trip test tone";
        options.Comments["ARTIST"] = "Café Ünïcode ✓ 日本語";

        //Act
        TestAudio.Decoded decoded = TestAudio.Decode(TestAudio.Encode(input, 1, 44100, options));

        //Assert
        decoded.Tags["TITLE"].Should().ContainSingle().Which.Should().Be("Round-trip test tone");
        decoded.Tags["ARTIST"].Should().ContainSingle().Which.Should().Be("Café Ünïcode ✓ 日本語");
    }

    [Fact]
    public void PaddingBytes_writes_a_padding_block_of_that_size()
    {
        //Arrange
        int[] input = TestAudio.SineInt(440, 44100, 1, 4410, 16);

        //Act
        byte[] flac = TestAudio.Encode(input, 1, 44100, new FlacEncoderOptions { PaddingBytes = 4096 });
        FlacStreamHeader header = FlacStreamHeader.Parse(flac);

        //Assert
        header.Blocks[0].Type.Should().Be(0);
        header.Blocks.Should().Contain(b => b.Type == 1 && b.Length == 4096);
        header.Blocks[^1].IsLast.Should().BeTrue();
        TestAudio.Decode(flac).Samples.Should().Equal(input);
    }

    [Fact]
    public void no_padding_block_by_default()
        => FlacStreamHeader.Parse(TestAudio.Encode(new int[100], 1, 44100, null)).Blocks.Should().NotContain(b => b.Type == 1);

    [Fact]
    public void SeekTableSamplesBetweenPoints_writes_a_filled_seek_table()
    {
        //Arrange
        const int frames = 44100 * 3;
        int[] input = TestAudio.SineInt(440, 44100, 2, frames, 16);
        FlacEncoderOptions options = new FlacEncoderOptions { TotalSamplesEstimate = frames, SeekTableSamplesBetweenPoints = 44100 };

        //Act
        byte[] flac = TestAudio.Encode(input, 2, 44100, options);
        FlacStreamHeader header = FlacStreamHeader.Parse(flac);
        List<KeyValuePair<long, long>> points = header.SeekPoints();

        //Assert
        points.Count.Should().BeGreaterThanOrEqualTo(3);
        points.Where(p => p.Key != -1).Select(p => p.Key).Should().BeInAscendingOrder();
        points[0].Key.Should().Be(0);
        points[0].Value.Should().Be(0);
        points.Where(p => p.Key > 0).Should().OnlyContain(p => p.Value > 0);
        TestAudio.Decode(flac).Samples.Should().Equal(input);
    }

    [Fact]
    public void higher_compression_level_is_never_larger()
    {
        //Arrange
        int[] input = TestAudio.SineInt(440, 44100, 2, 44100 * 2, 16);

        //Act
        byte[] fast = TestAudio.Encode(input, 2, 44100, new FlacEncoderOptions { CompressionLevel = 0 });
        byte[] small = TestAudio.Encode(input, 2, 44100, new FlacEncoderOptions { CompressionLevel = 8 });

        //Assert
        small.Length.Should().BeLessThanOrEqualTo(fast.Length);
        fast.Length.Should().BeLessThan(input.Length * 2);
    }

    [Fact]
    public void BlockSize_sets_the_block_size_recorded_in_streaminfo()
    {
        //Arrange
        int[] input = TestAudio.SineInt(440, 44100, 1, 10000, 16);

        //Act
        FlacStreamHeader header = FlacStreamHeader.Parse(TestAudio.Encode(input, 1, 44100, new FlacEncoderOptions { BlockSize = 1152 }));

        //Assert
        header.MaximumBlockSize.Should().Be(1152);
        header.MinimumBlockSize.Should().Be(1152);
    }

    [Fact]
    public void subset_refuses_a_non_subset_block_size_with_the_libflac_state()
    {
        //Arrange
        using MemoryStream output = new MemoryStream();
        Action create = () => new FlacEncoder(output, 2, 44100, new FlacEncoderOptions { BlockSize = 8192 });

        //Assert
        FlacEncoderException thrown = create.Should().Throw<FlacEncoderException>().Which;
        thrown.NativeCode.Should().Be(-4);
        thrown.NativeState.Should().Contain("NOT_STREAMABLE");
        thrown.Message.Should().Contain("NOT_STREAMABLE");
    }

    [Fact]
    public void subset_refuses_a_non_subset_bit_depth()
    {
        //Arrange
        Action create = () => new FlacPacketEncoder(2, 44100, new FlacEncoderOptions { BitsPerSample = 17 });

        //Assert
        FlacEncoderException thrown = create.Should().Throw<FlacEncoderException>().Which;
        thrown.NativeCode.Should().Be(-4);
        thrown.NativeState.Should().Contain("NOT_STREAMABLE");
    }

    [Fact]
    public void non_subset_allows_a_large_block_size_and_an_odd_bit_depth()
    {
        //Arrange
        int[] input = TestAudio.SineInt(440, 44100, 2, 20000, 17);
        FlacEncoderOptions options = new FlacEncoderOptions { BlockSize = 8192, BitsPerSample = 17, StreamableSubset = false };

        //Act
        byte[] flac = TestAudio.Encode(input, 2, 44100, options);

        //Assert
        FlacStreamHeader.Parse(flac).BitsPerSample.Should().Be(17);
        FlacStreamHeader.Parse(flac).Md5.Should().Equal(TestAudio.FlacMd5(input, 17));
    }

    [Fact]
    public void Encode_returns_whole_frames_consumed()
    {
        //Arrange
        using MemoryStream output = new MemoryStream();
        using FlacEncoder encoder = new FlacEncoder(output, 2, 44100);

        //Act
        int consumed = encoder.Encode(new float[1001]);

        //Assert
        consumed.Should().Be(500);
        encoder.FramesWritten.Should().Be(500);
        encoder.Duration.Should().Be(TimeSpan.FromSeconds(500.0 / 44100));
    }

    [Fact]
    public void many_small_calls_produce_the_same_file_as_one_call()
    {
        //Arrange
        int[] input = TestAudio.SineInt(440, 44100, 2, 25000, 16);
        using MemoryStream output = new MemoryStream();

        //Act
        using (FlacEncoder encoder = new FlacEncoder(output, 2, 44100))
        {
            for (int offset = 0; offset < input.Length; offset += 2 * 333)
            {
                encoder.EncodeInt32(input.AsSpan(offset, Math.Min(2 * 333, input.Length - offset)));
            }
        }

        //Assert
        output.ToArray().Should().Equal(TestAudio.Encode(input, 2, 44100, null));
    }

    [Fact]
    public void input_far_larger_than_one_native_call_is_encoded_completely()
    {
        //Arrange
        int[] input = TestAudio.SineInt(440, 44100, 2, 44100 * 5, 16);

        //Act
        byte[] flac = TestAudio.Encode(input, 2, 44100, null);

        //Assert
        FlacStreamHeader.Parse(flac).TotalSamples.Should().Be(44100 * 5);
        TestAudio.Decode(flac).Samples.Length.Should().Be(input.Length);
    }

    [Fact]
    public void an_empty_stream_is_a_valid_flac_file()
    {
        //Arrange
        using MemoryStream output = new MemoryStream();

        //Act
        using (new FlacEncoder(output, 2, 44100))
        {
        }

        byte[] flac = output.ToArray();

        //Assert
        FlacStreamHeader.Parse(flac).Length.Should().Be(flac.Length);
        FlacStreamHeader.Parse(flac).TotalSamples.Should().Be(0);
    }

    [Fact]
    public void constructor_rejects_bad_arguments()
    {
        //Arrange
        using MemoryStream readOnly = new MemoryStream(new byte[4], writable: false);
        Action nullStream = () => new FlacEncoder(null, 2, 44100);
        Action notWritable = () => new FlacEncoder(readOnly, 2, 44100);
        Action noChannels = () => new FlacEncoder(new MemoryStream(), 0, 44100);
        Action nineChannels = () => new FlacEncoder(new MemoryStream(), 9, 44100);
        Action noRate = () => new FlacEncoder(new MemoryStream(), 2, 0);
        Action hugeRate = () => new FlacEncoder(new MemoryStream(), 2, 655351);
        Action badOption = () => new FlacEncoder(new MemoryStream(), 2, 44100, new FlacEncoderOptions { CompressionLevel = 9 });

        //Assert
        nullStream.Should().Throw<ArgumentNullException>();
        notWritable.Should().Throw<ArgumentException>();
        noChannels.Should().Throw<ArgumentOutOfRangeException>();
        nineChannels.Should().Throw<ArgumentOutOfRangeException>();
        noRate.Should().Throw<ArgumentOutOfRangeException>();
        hugeRate.Should().Throw<ArgumentOutOfRangeException>();
        badOption.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Encode_after_Flush_throws()
    {
        //Arrange
        using MemoryStream output = new MemoryStream();
        using FlacEncoder encoder = new FlacEncoder(output, 1, 44100);
        encoder.Flush();
        Action encode = () => encoder.Encode(new float[4]);
        Action encode32 = () => encoder.EncodeInt32(new int[4]);

        //Assert
        encoder.IsFinished.Should().BeTrue();
        encode.Should().Throw<InvalidOperationException>();
        encode32.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Encode_after_Dispose_throws()
    {
        //Arrange
        using MemoryStream output = new MemoryStream();
        FlacEncoder encoder = new FlacEncoder(output, 1, 44100);
        encoder.Dispose();
        Action encode = () => encoder.Encode(new float[4]);
        Action flush = () => encoder.Flush();

        //Assert
        encoder.IsDisposed.Should().BeTrue();
        encode.Should().Throw<ObjectDisposedException>();
        flush.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_twice_and_Flush_twice_are_harmless()
    {
        //Arrange
        using MemoryStream output = new MemoryStream();
        FlacEncoder encoder = new FlacEncoder(output, 1, 44100);
        encoder.EncodeInt32(new int[1000]);
        encoder.Flush();
        long length = output.Length;

        //Act
        encoder.Flush();
        encoder.Dispose();
        encoder.Dispose();

        //Assert
        output.Length.Should().Be(length);
        encoder.StreamHeader.Length.Should().BeGreaterThan(42);
    }

    [Fact]
    public void the_output_stream_is_left_open()
    {
        //Arrange
        using MemoryStream output = new MemoryStream();

        //Act
        using (FlacEncoder encoder = new FlacEncoder(output, 1, 44100))
        {
            encoder.EncodeInt32(new int[100]);
        }

        //Assert
        output.CanWrite.Should().BeTrue();
    }

    [Theory]
    [InlineData(1, 44100, 16)]
    [InlineData(2, 48000, 16)]
    [InlineData(2, 44100, 24)]
    [InlineData(1, 48000, 24)]
    public void ffmpeg_decodes_the_output_sample_exact_and_cleanly(int channels, int sampleRate, int bits)
    {
        //Arrange
        Ffmpeg.SkipUnlessPresent();
        int frames = sampleRate + 4321;
        int[] input = TestAudio.SineInt(440, sampleRate, channels, frames, bits);
        FlacEncoderOptions options = new FlacEncoderOptions { BitsPerSample = bits };
        options.Comments["TITLE"] = "ffmpeg check";

        //Act
        byte[] flac = TestAudio.Encode(input, channels, sampleRate, options);
        int[] decoded = Ffmpeg.Decode(flac, bits, out string errors);
        Dictionary<string, string> probe = Ffmpeg.Probe(flac, out string probeErrors);

        //Assert
        errors.Should().BeEmpty();
        probeErrors.Should().BeEmpty();
        decoded.Should().Equal(input);
        probe["codec_name"].Should().Be("flac");
        probe["channels"].Should().Be(channels.ToString());
        probe["sample_rate"].Should().Be(sampleRate.ToString());
        probe["bits_per_raw_sample"].Should().Be(bits.ToString());
        double.Parse(probe["duration"], System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeApproximately((double)frames / sampleRate, 0.001);
    }

    [Fact]
    public void ffmpeg_decodes_a_forward_only_stream_with_placeholders()
    {
        //Arrange
        Ffmpeg.SkipUnlessPresent();
        int[] input = TestAudio.SineInt(440, 44100, 2, 30000, 16);
        ForwardOnlyStream output = new ForwardOnlyStream();

        //Act
        using (FlacEncoder encoder = new FlacEncoder(output, 2, 44100))
        {
            encoder.EncodeInt32(input);
        }

        int[] decoded = Ffmpeg.Decode(output.ToArray(), 16, out string errors);

        //Assert
        errors.Should().BeEmpty();
        decoded.Should().Equal(input);
    }

    [Fact]
    public void ffmpeg_decodes_a_file_with_padding_and_a_seek_table()
    {
        //Arrange
        Ffmpeg.SkipUnlessPresent();
        int[] input = TestAudio.SineInt(440, 48000, 2, 48000 * 2, 24);
        FlacEncoderOptions options = new FlacEncoderOptions
        {
            BitsPerSample = 24,
            CompressionLevel = 8,
            PaddingBytes = 1024,
            TotalSamplesEstimate = 48000 * 2,
            SeekTableSamplesBetweenPoints = 24000,
        };

        //Act
        byte[] flac = TestAudio.Encode(input, 2, 48000, options);
        int[] decoded = Ffmpeg.Decode(flac, 24, out string errors);

        //Assert
        errors.Should().BeEmpty();
        decoded.Should().Equal(input);
    }
}
