using System;
using System.Runtime.InteropServices;

namespace CodeBrix.Audio.FlacWriter.Interop;

/// <summary>
/// The result codes of the native contract (<c>CBF_*</c> in <c>codebrix_flac.h</c>), and the text the managed
/// side reports for each.
/// </summary>
internal static class FlacResult
{
    /// <summary><c>CBF_OK</c>.</summary>
    public const int Ok = 0;

    /// <summary><c>CBF_E_INVALID_ARGUMENT</c>.</summary>
    public const int InvalidArgument = -1;

    /// <summary><c>CBF_E_OUT_OF_MEMORY</c>.</summary>
    public const int OutOfMemory = -2;

    /// <summary><c>CBF_E_INVALID_STATE</c>.</summary>
    public const int InvalidState = -3;

    /// <summary><c>CBF_E_UNSUPPORTED</c>.</summary>
    public const int Unsupported = -4;

    /// <summary><c>CBF_E_VERIFY_MISMATCH</c>.</summary>
    public const int VerifyMismatch = -5;

    /// <summary><c>CBF_E_FLAC_FAULT</c>.</summary>
    public const int FlacFault = -6;

    /// <summary>Describes a result code in words.</summary>
    /// <param name="code">The code.</param>
    /// <returns>A short description.</returns>
    public static string Describe(int code) =>
        code switch
        {
            Ok => "success",
            InvalidArgument => "an argument was out of range (CBF_E_INVALID_ARGUMENT)",
            OutOfMemory => "the native encoder ran out of memory (CBF_E_OUT_OF_MEMORY)",
            InvalidState => "the call is not valid in the encoder's current state (CBF_E_INVALID_STATE)",
            Unsupported =>
                "libFLAC refused this combination of channels, sample rate, bits per sample, block size and "
                + "subset setting (CBF_E_UNSUPPORTED) - a bit depth other than 8, 12, 16, 20, 24 or 32, or a block "
                + "size outside the subset, needs StreamableSubset = false",
            VerifyMismatch =>
                "verification failed: decoding a frame the encoder had just produced did not give back the input "
                + "samples (CBF_E_VERIFY_MISMATCH). The output is NOT a faithful copy of the audio and must not be "
                + "kept; this points at a libFLAC, compiler or hardware fault, never at the input",
            FlacFault => "libFLAC failed (CBF_E_FLAC_FAULT)",
            _ => $"unknown native result code {code}",
        };

    /// <summary>Reads libFLAC's textual state for the last failing call on <paramref name="encoder" />.</summary>
    /// <param name="encoder">The native handle, or <see cref="IntPtr.Zero" />.</param>
    /// <returns>The state text, or an empty string.</returns>
    public static string LastState(IntPtr encoder)
    {
        if (encoder == IntPtr.Zero) return string.Empty;
        IntPtr text = FlacNative.EncoderLastState(encoder);
        return text == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(text) ?? string.Empty;
    }

    /// <summary>Builds the exception for a failed native call.</summary>
    /// <param name="code">The code the call returned.</param>
    /// <param name="operation">What was being done, for the message.</param>
    /// <param name="encoder">The native handle the call was made on, for libFLAC's state text.</param>
    /// <returns>The exception.</returns>
    public static FlacEncoderException Failure(int code, string operation, IntPtr encoder)
    {
        string state = LastState(encoder);
        string message = $"FLAC encoding failed while {operation}: {Describe(code)}.";
        if (state.Length > 0) message += $" libFLAC state: {state}.";
        return new FlacEncoderException(message, code, state);
    }

    /// <summary>Throws a <see cref="FlacEncoderException" /> when <paramref name="code" /> is negative.</summary>
    /// <param name="code">The code a native call returned.</param>
    /// <param name="operation">What was being done, for the message.</param>
    /// <param name="encoder">The native handle the call was made on.</param>
    public static void ThrowIfFailed(int code, string operation, IntPtr encoder)
    {
        if (code >= Ok) return;
        throw Failure(code, operation, encoder);
    }
}
