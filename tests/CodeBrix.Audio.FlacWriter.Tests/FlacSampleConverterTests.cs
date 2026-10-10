using CodeBrix.Audio.FlacWriter.Internal;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Audio.FlacWriter.Tests;

public class FlacSampleConverterTests
{
    [Theory]
    [InlineData(0f, 16, 0)]
    [InlineData(1f, 16, 32767)]
    [InlineData(-1f, 16, -32768)]
    [InlineData(2f, 16, 32767)]
    [InlineData(-2f, 16, -32768)]
    [InlineData(0.5f, 16, 16384)]
    [InlineData(1f, 24, 8388607)]
    [InlineData(-1f, 24, -8388608)]
    [InlineData(1f, 32, int.MaxValue)]
    [InlineData(-1f, 32, int.MinValue)]
    [InlineData(1f, 8, 127)]
    public void Quantize_scales_rounds_and_clips(float sample, int bits, int expected)
        => FlacSampleConverter.Quantize(sample, bits).Should().Be(expected);

    [Fact]
    public void Quantize_maps_nan_to_zero()
        => FlacSampleConverter.Quantize(float.NaN, 16).Should().Be(0);

    [Fact]
    public void Quantize_clips_infinities()
    {
        //Act
        int high = FlacSampleConverter.Quantize(float.PositiveInfinity, 16);
        int low = FlacSampleConverter.Quantize(float.NegativeInfinity, 16);

        //Assert
        high.Should().Be(32767);
        low.Should().Be(-32768);
    }

    [Fact]
    public void FloatToInt_converts_a_run()
    {
        //Arrange
        int[] destination = new int[3];

        //Act
        FlacSampleConverter.FloatToInt(new[] { 0f, 1f, -1f }, destination, 16);

        //Assert
        destination.Should().Equal(0, 32767, -32768);
    }
}
