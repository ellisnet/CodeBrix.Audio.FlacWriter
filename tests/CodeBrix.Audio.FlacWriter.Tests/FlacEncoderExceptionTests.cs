using System;
using CodeBrix.Audio.FlacWriter.Interop;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Audio.FlacWriter.Tests;

public class FlacEncoderExceptionTests
{
    [Fact]
    public void message_only_has_no_native_code_or_state()
    {
        //Act
        FlacEncoderException exception = new FlacEncoderException("boom");

        //Assert
        exception.NativeCode.Should().Be(0);
        exception.NativeState.Should().BeEmpty();
        exception.IsVerifyMismatch.Should().BeFalse();
    }

    [Fact]
    public void native_code_and_state_are_kept()
    {
        //Act
        FlacEncoderException exception = new FlacEncoderException("boom", -5, "FLAC__STREAM_ENCODER_VERIFY_MISMATCH_IN_AUDIO_DATA");

        //Assert
        exception.NativeCode.Should().Be(-5);
        exception.NativeState.Should().Be("FLAC__STREAM_ENCODER_VERIFY_MISMATCH_IN_AUDIO_DATA");
        exception.IsVerifyMismatch.Should().BeTrue();
    }

    [Fact]
    public void inner_exception_is_kept()
    {
        //Arrange
        InvalidOperationException inner = new InvalidOperationException("inner");

        //Act
        FlacEncoderException exception = new FlacEncoderException("outer", inner);

        //Assert
        exception.InnerException.Should().BeSameAs(inner);
        exception.NativeState.Should().BeEmpty();
    }

    [Fact]
    public void Describe_explains_a_verify_mismatch_clearly()
    {
        //Act
        string text = FlacResult.Describe(FlacResult.VerifyMismatch);

        //Assert
        text.Should().Contain("CBF_E_VERIFY_MISMATCH");
        text.Should().Contain("must not be kept");
    }

    [Theory]
    [InlineData(-1, "CBF_E_INVALID_ARGUMENT")]
    [InlineData(-2, "CBF_E_OUT_OF_MEMORY")]
    [InlineData(-3, "CBF_E_INVALID_STATE")]
    [InlineData(-4, "CBF_E_UNSUPPORTED")]
    [InlineData(-6, "CBF_E_FLAC_FAULT")]
    [InlineData(-99, "unknown")]
    public void Describe_names_every_code(int code, string expected)
        => FlacResult.Describe(code).Should().Contain(expected);

    [Fact]
    public void Failure_without_a_handle_carries_the_code_and_no_state()
    {
        //Act
        FlacEncoderException exception = FlacResult.Failure(FlacResult.InvalidState, "testing", IntPtr.Zero);

        //Assert
        exception.NativeCode.Should().Be(-3);
        exception.NativeState.Should().BeEmpty();
        exception.Message.Should().Contain("while testing");
    }
}
