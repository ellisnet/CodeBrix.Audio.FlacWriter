using System;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Audio.FlacWriter.Tests;

public class FlacEncoderOptionsTests
{
    [Fact]
    public void defaults_are_16_bit_level_5_verify_subset()
    {
        //Act
        FlacEncoderOptions options = new FlacEncoderOptions();

        //Assert
        options.BitsPerSample.Should().Be(16);
        options.CompressionLevel.Should().Be(5);
        options.BlockSize.Should().Be(0);
        options.Verify.Should().BeTrue();
        options.StreamableSubset.Should().BeTrue();
        options.TotalSamplesEstimate.Should().Be(0);
        options.PaddingBytes.Should().Be(0);
        options.SeekTableSamplesBetweenPoints.Should().Be(0);
        options.Comments.Should().BeEmpty();
    }

    [Fact]
    public void Validate_accepts_the_defaults()
    {
        //Arrange
        Action validate = () => new FlacEncoderOptions().Validate();

        //Assert
        validate.Should().NotThrow();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(33)]
    public void Validate_rejects_BitsPerSample_out_of_range(int bits)
    {
        //Arrange
        Action validate = () => new FlacEncoderOptions { BitsPerSample = bits }.Validate();

        //Assert
        validate.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(4)]
    [InlineData(24)]
    [InlineData(32)]
    public void Validate_accepts_BitsPerSample_in_range(int bits)
    {
        //Arrange
        Action validate = () => new FlacEncoderOptions { BitsPerSample = bits, StreamableSubset = false }.Validate();

        //Assert
        validate.Should().NotThrow();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9)]
    public void Validate_rejects_CompressionLevel_out_of_range(int level)
    {
        //Arrange
        Action validate = () => new FlacEncoderOptions { CompressionLevel = level }.Validate();

        //Assert
        validate.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(15)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void Validate_rejects_BlockSize_out_of_range(int size)
    {
        //Arrange
        Action validate = () => new FlacEncoderOptions { BlockSize = size }.Validate();

        //Assert
        validate.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_rejects_negative_or_huge_counts()
    {
        //Arrange
        Action estimate = () => new FlacEncoderOptions { TotalSamplesEstimate = -1 }.Validate();
        Action hugeEstimate = () => new FlacEncoderOptions { TotalSamplesEstimate = 1L << 36 }.Validate();
        Action padding = () => new FlacEncoderOptions { PaddingBytes = -1 }.Validate();
        Action hugePadding = () => new FlacEncoderOptions { PaddingBytes = 16_777_216 }.Validate();
        Action spacing = () => new FlacEncoderOptions { SeekTableSamplesBetweenPoints = -1 }.Validate();

        //Assert
        estimate.Should().Throw<ArgumentOutOfRangeException>();
        hugeEstimate.Should().Throw<ArgumentOutOfRangeException>();
        padding.Should().Throw<ArgumentOutOfRangeException>();
        hugePadding.Should().Throw<ArgumentOutOfRangeException>();
        spacing.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_requires_an_estimate_for_a_seek_table()
    {
        //Arrange
        Action validate = () => new FlacEncoderOptions { SeekTableSamplesBetweenPoints = 44100 }.Validate();

        //Assert
        validate.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("SeekTableSamplesBetweenPoints");
    }

    [Theory]
    [InlineData("")]
    [InlineData("A=B")]
    [InlineData("TAB\t")]
    [InlineData("TILDE~")]
    public void Validate_rejects_bad_comment_names(string name)
    {
        //Arrange
        FlacEncoderOptions options = new FlacEncoderOptions();
        options.Comments[name] = "value";
        Action validate = () => options.Validate();

        //Assert
        validate.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_rejects_null_and_nul_comment_values()
    {
        //Arrange
        FlacEncoderOptions nullValue = new FlacEncoderOptions();
        nullValue.Comments["TITLE"] = null;
        FlacEncoderOptions nulValue = new FlacEncoderOptions();
        nulValue.Comments["TITLE"] = "a\0b";
        Action first = () => nullValue.Validate();
        Action second = () => nulValue.Validate();

        //Assert
        first.Should().Throw<ArgumentException>();
        second.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_accepts_null_comments()
    {
        //Arrange
        Action validate = () => new FlacEncoderOptions { Comments = null }.Validate();

        //Assert
        validate.Should().NotThrow();
    }

    [Fact]
    public void Clone_copies_every_setting_and_the_comments_independently()
    {
        //Arrange
        FlacEncoderOptions original = new FlacEncoderOptions
        {
            BitsPerSample = 24,
            CompressionLevel = 8,
            BlockSize = 1152,
            Verify = false,
            StreamableSubset = false,
            TotalSamplesEstimate = 1000,
            PaddingBytes = 10,
            SeekTableSamplesBetweenPoints = 100,
        };
        original.Comments["TITLE"] = "x";

        //Act
        FlacEncoderOptions copy = original.Clone();
        original.Comments["TITLE"] = "changed";

        //Assert
        copy.BitsPerSample.Should().Be(24);
        copy.CompressionLevel.Should().Be(8);
        copy.BlockSize.Should().Be(1152);
        copy.Verify.Should().BeFalse();
        copy.StreamableSubset.Should().BeFalse();
        copy.TotalSamplesEstimate.Should().Be(1000);
        copy.PaddingBytes.Should().Be(10);
        copy.SeekTableSamplesBetweenPoints.Should().Be(100);
        copy.Comments["TITLE"].Should().Be("x");
    }
}
