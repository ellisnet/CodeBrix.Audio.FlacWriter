using System;

namespace CodeBrix.Audio.FlacWriter;

/// <summary>
/// Thrown when the native FLAC encoder cannot be loaded or reports a failure.
/// </summary>
/// <remarks>
/// <see cref="NativeCode" /> carries the native result code that caused the failure:
/// -1 an argument was out of range; -2 out of memory; -3 the call is not valid in the encoder's current state;
/// -4 libFLAC refused the combination of settings; -5 verification found a frame that does not decode back to
/// its input (the output must be discarded); -6 any other libFLAC failure. It is 0 when the failure did not come
/// from a native call - for example when the native library could not be loaded at all.
/// <see cref="NativeState" /> carries libFLAC's own state name for the failure, when it reported one.
/// </remarks>
public class FlacEncoderException : Exception
{
    /// <summary>Creates the exception with a message and no native code.</summary>
    /// <param name="message">What went wrong.</param>
    public FlacEncoderException(string message) : base(message)
    {
        NativeState = string.Empty;
    }

    /// <summary>Creates the exception with a message and the native result code that caused it.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="nativeCode">The native result code (a negative <c>CBF_E_*</c> value).</param>
    public FlacEncoderException(string message, int nativeCode) : this(message, nativeCode, null)
    {
    }

    /// <summary>Creates the exception with a message, the native result code and libFLAC's state text.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="nativeCode">The native result code (a negative <c>CBF_E_*</c> value).</param>
    /// <param name="nativeState">libFLAC's state name for the failure, or null.</param>
    public FlacEncoderException(string message, int nativeCode, string nativeState) : base(message)
    {
        NativeCode = nativeCode;
        NativeState = nativeState ?? string.Empty;
    }

    /// <summary>Creates the exception with a message and the exception that caused it.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The underlying failure.</param>
    public FlacEncoderException(string message, Exception innerException) : base(message, innerException)
    {
        NativeState = string.Empty;
    }

    /// <summary>
    /// The native result code that caused the failure (a negative <c>CBF_E_*</c> value), or 0 when the failure
    /// did not come from a native call.
    /// </summary>
    public int NativeCode { get; }

    /// <summary>
    /// libFLAC's own state name for the failure - for example
    /// <c>FLAC__STREAM_ENCODER_VERIFY_MISMATCH_IN_AUDIO_DATA</c> or
    /// <c>FLAC__STREAM_ENCODER_INIT_STATUS_NOT_STREAMABLE</c> - or an empty string when it reported none.
    /// </summary>
    public string NativeState { get; }

    /// <summary>True when the failure is a verification mismatch: the encoded output is not trustworthy.</summary>
    public bool IsVerifyMismatch => NativeCode == -5;
}
