# =============================================================================================
# build-win-x64.ps1 - build codebrix_flac.dll for the win-x64 runtime identifier
# =============================================================================================
#
#   Written on Linux on 2026-10-10 as a copy of CodeBrix.Audio.Vorbis's script of the same name
#   (run successfully by Jeremy that day on his x64 Windows 11 machine), adapted to FLAC.
#   NOT YET RUN. If something breaks, fix it IN THE SCRIPT (or build-common.ps1) and record it in
#   ..\BUILD-PROVENANCE.txt.
#
# USAGE (from any PowerShell prompt - the script sets up the compiler environment itself):
#
#     cd flac-native-tools\windows
#     .\build-win-x64.ps1              # build, gate, and (gate COMPLETE) adopt into src\
#     .\build-win-x64.ps1 -NoAdopt     # build + gate only
#
# MSVC (cl) through CMake's Visual Studio generator, -A x64, static CRT. Output:
# ..\output\win-x64\ ; adopted into ..\..\src\CodeBrix.Audio.FlacWriter\runtimes\win-x64\native\
# (codebrix_flac.dll + LICENSE-Flac.txt), the .pdb into ..\unstripped\win-x64\.
# It installs nothing. Anything missing is named, with the command that installs it.
# Prerequisites: Visual Studio 2022+ (or Build Tools) with "Desktop development with C++",
# cmake >= 3.16 on PATH.
# =============================================================================================

[CmdletBinding()]
param(
    # Where cmake builds and the gate runs. Kept out of the repository.
    [string] $BuildRoot = (Join-Path $env:TEMP 'codebrix-flac-build-win-x64'),
    [switch] $NoAdopt
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot\build-common.ps1"

if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64') {
    throw "build-win-x64.ps1 runs on an x64 Windows machine; this host is $env:PROCESSOR_ARCHITECTURE."
}

Invoke-WindowsFlacBuild -Rid 'win-x64' -Platform 'x64' -ExpectedMachine 0x8664 -VcVarsArch 'x64' `
                          -Route 'native (x64 host)' -CanRun $true -BuildRoot $BuildRoot `
                          -ScriptName 'build-win-x64.ps1' -NoAdopt:$NoAdopt
