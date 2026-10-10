using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace CodeBrix.Audio.FlacWriter.Interop;

/// <summary>
/// Finds and loads the native codebrix_flac library, and checks that the one it found speaks the contract
/// this binding was written against.
/// </summary>
/// <remarks>
/// <para>
/// The package ships one native per runtime identifier in the standard NuGet <c>runtimes/&lt;rid&gt;/native/</c>
/// layout. When an application publishes for one runtime identifier, the build system copies the right one
/// beside the application and the operating system finds it without help. When an application runs without a
/// runtime identifier - the ordinary case for a library test run, and common for desktop applications - the
/// natives stay in their <c>runtimes/&lt;rid&gt;/native/</c> folders and nothing looks there. The resolver
/// installed here does.
/// </para>
/// <para>
/// Android packages the selected library inside the APK. Its native loader locates that library by name;
/// neither an assembly directory nor a physical file beside the application is required.
/// </para>
/// <para>
/// If nothing can be loaded, the exception lists every path that was tried. A missing native is nearly always
/// a packaging or publishing question - the wrong runtime identifier, a trimmed output folder, a single-file
/// bundle that did not extract - and the list of paths is what answers it.
/// </para>
/// </remarks>
internal static class FlacLibrary
{
    /// <summary>The name the <c>LibraryImport</c> declarations ask for.</summary>
    public const string LibraryName = "codebrix_flac";

    /// <summary>The contract version (<c>CODEBRIX_FLAC_API_VERSION</c>) this binding is written against.</summary>
    public const int ExpectedApiVersion = 1;

    private static readonly object Gate = new object();

    private static IntPtr handle;
    private static string loadedPath;
    private static bool resolverInstalled;
    private static bool verified;
    private static string versionString;

    /// <summary>The full path of the native library that was loaded, once one has been.</summary>
    public static string LoadedPath
    {
        get
        {
            lock (Gate) return loadedPath;
        }
    }

    /// <summary>The libFLAC version string the loaded library reports.</summary>
    public static string Version
    {
        get
        {
            EnsureLoaded();
            lock (Gate) return versionString;
        }
    }

    /// <summary>True once the native library has been loaded and its contract version accepted.</summary>
    public static bool IsLoaded
    {
        get
        {
            lock (Gate) return handle != IntPtr.Zero && verified;
        }
    }

    /// <summary>The runtime identifier folder this platform's native library lives in.</summary>
    /// <exception cref="FlacEncoderException">This package ships no native for the current platform.</exception>
    public static string RuntimeIdentifier
    {
        get
        {
            string os = OperatingSystemMoniker();
            string architecture = ArchitectureMoniker();

            if (os == null || architecture == null || (os != "linux" && architecture == "riscv64"))
            {
                throw new FlacEncoderException(
                    "CodeBrix.Audio.FlacWriter ships native libraries for Windows (x64, ARM64), macOS (x64, ARM64), "
                    + "Linux (x64, ARM64, RISC-V 64) and Android 13+ (x64, ARM64); this process is running on "
                    + $"{RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}, which is "
                    + "none of them.");
            }

            return os + "-" + architecture;
        }
    }

    /// <summary>The file name the native library has on this platform.</summary>
    public static string NativeFileName
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "codebrix_flac.dll";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "libcodebrix_flac.dylib";
            return "libcodebrix_flac.so";
        }
    }

    /// <summary>Loads the native library if it is not loaded already, and checks its contract version.</summary>
    /// <exception cref="FlacEncoderException">
    /// The library could not be found - the message names the runtime identifier and lists every path that was
    /// tried - or it reports a contract version this binding was not written against.
    /// </exception>
    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (handle != IntPtr.Zero && verified) return;

            InstallResolverNoLock();

            if (handle == IntPtr.Zero)
            {
                IntPtr loaded = LoadNoLock();
                if (loaded == IntPtr.Zero)
                {
                    throw new FlacEncoderException(
                        DescribeLoadFailure(BaseDirectories(), RuntimeIdentifier, NativeFileName));
                }
            }
        }

        // Outside the lock: these call into the native library, which loads through the resolver above.
        int reportedApi = FlacNative.ApiVersion();
        string reportedVersion = ReadVersionString();

        if (reportedApi != ExpectedApiVersion)
        {
            throw new FlacEncoderException(DescribeVersionMismatch(LoadedPath, reportedApi, reportedVersion));
        }

        lock (Gate)
        {
            versionString = reportedVersion;
            verified = true;
        }
    }

    /// <summary>Builds the "wrong contract version" message.</summary>
    /// <param name="path">Where the library was loaded from.</param>
    /// <param name="reportedApi">The contract version it reports.</param>
    /// <param name="reportedVersion">The libFLAC version string it reports.</param>
    /// <returns>The message.</returns>
    internal static string DescribeVersionMismatch(string path, int reportedApi, string reportedVersion) =>
        $"The native codebrix_flac library at '{path}' reports contract version {reportedApi} "
        + $"(libFLAC '{reportedVersion}'), but this package is written against contract version {ExpectedApiVersion}. "
        + "Replace the native library with the one shipped in this package, or use the version of this package "
        + "that the library was built for.";

    /// <summary>Builds the "nothing could be loaded" message, listing every path that would be tried.</summary>
    /// <param name="baseDirectories">The directories to probe, in order.</param>
    /// <param name="runtimeIdentifier">The runtime identifier folder to look under.</param>
    /// <param name="fileName">The native file name to look for.</param>
    /// <returns>The message.</returns>
    /// <remarks>Separated out so a test can read it without having to hide the real library first.</remarks>
    internal static string DescribeLoadFailure(
        IReadOnlyList<string> baseDirectories,
        string runtimeIdentifier,
        string fileName)
    {
        StringBuilder message = new StringBuilder();
        message.Append("The native codebrix_flac library could not be loaded for runtime '");
        message.Append(runtimeIdentifier);
        message.Append("'. CodeBrix.Audio.FlacWriter looked for '");
        message.Append(fileName);
        message.Append("' at:");

        foreach (string candidate in EnumerateProbePaths(baseDirectories, runtimeIdentifier, fileName))
        {
            message.Append(Environment.NewLine);
            message.Append("    ");
            message.Append(candidate);
        }

        message.Append(Environment.NewLine);
        message.Append("    ");
        message.Append(fileName);
        message.Append("  (the operating system's own search path)");
        message.Append(Environment.NewLine);
        message.Append(
            "The package ships this library under runtimes/" + runtimeIdentifier + "/native/. If it is missing, "
            + "the application was probably published in a way that dropped it - a trimmed or single-file "
            + "publish, a manual copy of the managed assembly alone, or a runtime identifier the package has no "
            + "native for.");
        return message.ToString();
    }

    /// <summary>Lists, in order, every path the loader would try.</summary>
    /// <param name="baseDirectories">The directories to probe.</param>
    /// <param name="runtimeIdentifier">The runtime identifier folder to look under.</param>
    /// <param name="fileName">The native file name to look for.</param>
    /// <returns>The candidate paths.</returns>
    internal static IReadOnlyList<string> EnumerateProbePaths(
        IReadOnlyList<string> baseDirectories,
        string runtimeIdentifier,
        string fileName)
    {
        List<string> paths = new List<string>();

        foreach (string directory in baseDirectories)
        {
            if (string.IsNullOrEmpty(directory)) continue;
            Add(paths, Path.Combine(directory, fileName));
            Add(paths, Path.Combine(directory, "runtimes", runtimeIdentifier, "native", fileName));
        }

        return paths;
    }

    /// <summary>The directories the loader probes, most likely first.</summary>
    /// <returns>The directories, without duplicates.</returns>
    internal static IReadOnlyList<string> BaseDirectories()
    {
        List<string> directories = new List<string>();
        Add(directories, AppContext.BaseDirectory);

        string assemblyDirectory = null;
        try
        {
            string location = typeof(FlacLibrary).Assembly.Location;
            if (!string.IsNullOrEmpty(location)) assemblyDirectory = Path.GetDirectoryName(location);
        }
        catch (NotSupportedException)
        {
            // A single-file or in-memory assembly has no location; the base directory is all there is.
        }

        Add(directories, assemblyDirectory);
        return directories;
    }

    /// <summary>Installs the resolver, without loading anything.</summary>
    /// <remarks>
    /// Called from the static constructor of the type that carries the imports, so the resolver is in place
    /// before any of them can run - including when something calls an import without going through
    /// <see cref="EnsureLoaded" /> first.
    /// </remarks>
    public static void EnsureResolverInstalled()
    {
        lock (Gate) InstallResolverNoLock();
    }

    private static void InstallResolverNoLock()
    {
        if (resolverInstalled) return;
        NativeLibrary.SetDllImportResolver(typeof(FlacLibrary).Assembly, Resolve);
        resolverInstalled = true;
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal)) return IntPtr.Zero;

        lock (Gate)
        {
            if (handle != IntPtr.Zero) return handle;
            return LoadNoLock();
        }
    }

    private static IntPtr LoadNoLock()
    {
        string fileName = NativeFileName;
        string runtimeIdentifier;

        try
        {
            runtimeIdentifier = RuntimeIdentifier;
        }
        catch (FlacEncoderException)
        {
            runtimeIdentifier = null;
        }

        // Android loads the library the APK carries by name; there is no runtimes/ folder on the device.
        if (runtimeIdentifier != null && !OperatingSystem.IsAndroid())
        {
            foreach (string candidate in EnumerateProbePaths(BaseDirectories(), runtimeIdentifier, fileName))
            {
                if (!File.Exists(candidate)) continue;
                if (!NativeLibrary.TryLoad(candidate, out IntPtr found)) continue;

                handle = found;
                loadedPath = candidate;
                return found;
            }
        }

        if (NativeLibrary.TryLoad(LibraryName, typeof(FlacLibrary).Assembly, null, out IntPtr system))
        {
            handle = system;
            loadedPath = fileName + " (operating-system search path)";
            return system;
        }

        return IntPtr.Zero;
    }

    private static string ReadVersionString()
    {
        IntPtr text = FlacNative.LibraryVersion();
        return text == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(text);
    }

    private static string OperatingSystemMoniker()
    {
        // Android must be identified before the more general Linux check.
        if (OperatingSystem.IsAndroid()) return "android";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "win";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "osx";
        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : null;
    }

    private static string ArchitectureMoniker() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.RiscV64 => "riscv64",
            _ => null,
        };

    private static void Add(List<string> target, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        foreach (string existing in target)
        {
            if (string.Equals(existing, value, StringComparison.Ordinal)) return;
        }

        target.Add(value);
    }
}
