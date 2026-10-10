using SilverAssertions;
using Xunit;

namespace CodeBrix.Audio.FlacWriter.Tests;

public class FlacPacketTests
{
    [Fact]
    public void constructor_keeps_every_value()
    {
        //Arrange
        byte[] data = { 0xFF, 0xF8, 1, 2 };

        //Act
        FlacPacket packet = new FlacPacket(data, 4096, 1152);

        //Assert
        packet.Data.ToArray().Should().Equal(data);
        packet.FirstSample.Should().Be(4096);
        packet.SampleCount.Should().Be(1152);
    }

    [Fact]
    public void default_packet_is_empty()
        => default(FlacPacket).Data.IsEmpty.Should().BeTrue();
}
