using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using CodeBrix.Audio.FlacWriter.Interop;
using SilverAssertions;
using Xunit;

namespace CodeBrix.Audio.FlacWriter.Tests;

public class FlacLibraryTests
{
    [Fact]
    public void EnumerateProbePaths_tries_beside_the_app_then_the_runtimes_folder()
    {
        //Arrange
        string root = Path.Combine(Path.GetTempPath(), "app");

        //Act
        IReadOnlyList<string> paths = FlacLibrary.EnumerateProbePaths(new[] { root, root, null }, "linux-x64", "libcodebrix_flac.so");

        //Assert
        paths.Should().Equal(
            Path.Combine(root, "libcodebrix_flac.so"),
            Path.Combine(root, "runtimes", "linux-x64", "native", "libcodebrix_flac.so"));
    }

    [Fact]
    public void DescribeLoadFailure_names_the_runtime_and_every_path()
    {
        //Arrange
        string root = Path.Combine(Path.GetTempPath(), "app");

        //Act
        string message = FlacLibrary.DescribeLoadFailure(new[] { root }, "osx-arm64", "libcodebrix_flac.dylib");

        //Assert
        message.Should().Contain("'osx-arm64'");
        message.Should().Contain(Path.Combine(root, "runtimes", "osx-arm64", "native", "libcodebrix_flac.dylib"));
        message.Should().Contain("runtimes/osx-arm64/native/");
    }

    [Fact]
    public void DescribeVersionMismatch_names_both_versions()
    {
        //Act
        string message = FlacLibrary.DescribeVersionMismatch("/x/libcodebrix_flac.so", 2, "9.9.9");

        //Assert
        message.Should().Contain("contract version 2");
        message.Should().Contain("contract version 1");
    }

    [Fact]
    public void RuntimeIdentifier_matches_the_running_platform()
    {
        //Act
        string rid = FlacLibrary.RuntimeIdentifier;

        //Assert
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            rid.Should().Be("linux-x64");
        }
        else
        {
            rid.Should().Contain("-");
        }
    }

    [Fact]
    public void NativeFileName_follows_the_platform_convention()
    {
        //Act
        string name = FlacLibrary.NativeFileName;

        //Assert
        name.Should().Contain("codebrix_flac");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) name.Should().Be("libcodebrix_flac.so");
    }

    [Fact]
    public void BaseDirectories_starts_with_the_app_base_directory()
        => FlacLibrary.BaseDirectories()[0].Should().Be(System.AppContext.BaseDirectory);
}
