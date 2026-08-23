<#
.SYNOPSIS
Builds and validates the canonical CoreCLR Core_Root used by MpsAgent and CSharpMpc.

.DESCRIPTION
Refreshes the selected runtime checkout's Core_Root, builds the filtered runtime-async
DynamicMethod reflection tests, removes the runtime repository's pinned Roslyn assemblies
from Core_Root, verifies the coherent runtime components, and executes the filtered test
assembly with the promoted corerun.

The runtime repository remains independent from MpsAgent. This script promotes a complete
Core_Root in the runtime checkout; it does not copy individual runtime binaries into MpsAgent.
#>
[CmdletBinding()]
param(
    [string]$RuntimeRoot = $PSScriptRoot,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$ExpectedPatchCommit = "050562076a95a63e671c4f345dc805535251eb96"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$Description,

        [int[]]$SuccessExitCodes = @(0)
    )

    & $Action
    $exitCode = $LASTEXITCODE
    if ($SuccessExitCodes -notcontains $exitCode) {
        throw "$Description failed with exit code $exitCode."
    }
}

$runtimeRootPath = (Resolve-Path -LiteralPath $RuntimeRoot).Path
$repoDotnet = Join-Path $runtimeRootPath ".dotnet"
$dotnetHost = Join-Path $repoDotnet "dotnet.exe"

if (-not (Test-Path -LiteralPath $dotnetHost -PathType Leaf)) {
    throw "Repository-local dotnet host was not found: $dotnetHost"
}

Push-Location $runtimeRootPath
try {
    if (-not [string]::IsNullOrWhiteSpace($ExpectedPatchCommit)) {
        & git merge-base --is-ancestor $ExpectedPatchCommit HEAD
        if ($LASTEXITCODE -ne 0) {
            throw "Runtime checkout HEAD does not contain required patch commit $ExpectedPatchCommit."
        }
    }

    foreach ($name in @(
        "VisualStudioVersion",
        "MSBuildSDKsPath",
        "MSBuildExtensionsPath",
        "MSBuildExtensionsPath32",
        "MSBuildExtensionsPath64",
        "MSBUILD_EXE_PATH",
        "MSBuildToolsPath",
        "MSBuildBinPath",
        "MSBUILDDEBUGPATH",
        "MSBUILDDEBUGONSTART",
        "MSBUILDDEBUGENGINE",
        "MSBUILDDEBUGCOMMUNICATION",
        "MSBUILDDEBUGSCHEDULER",
        "DOTNET_GLOBAL_INSTALL_DIR")) {
        Remove-Item "Env:$name" -ErrorAction SilentlyContinue
    }

    $env:DOTNET_INSTALL_DIR = $repoDotnet
    $env:DOTNET_ROOT = $repoDotnet
    $env:DOTNET_HOST_PATH = $dotnetHost
    $env:DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR = $repoDotnet
    $env:PATH = "$repoDotnet;$env:PATH"

    $restoreConfig = Join-Path $runtimeRootPath "NuGet.config"
    $runtimeConfigurationArgument = "-rc"
    $librariesConfigurationArgument = "-lc"

    Invoke-Checked -Description "CoreCLR/CoreLib product refresh" -Action {
        & ".\build.cmd" `
            "clr.corelib+clr.nativecorelib+libs.pretest" `
            "-c" $Configuration `
            $runtimeConfigurationArgument $Configuration `
            $librariesConfigurationArgument $Configuration
    }

    Invoke-Checked -Description "Filtered reflection test preparation" -Action {
        & ".\src\tests\build.cmd" `
            "x64" `
            $Configuration `
            "test" `
            "async\reflection\reflection.csproj" `
            "/p:LibrariesConfiguration=$Configuration" `
            "/p:RestoreConfigFile=$restoreConfig"
    }

    Invoke-Checked -Description "Standalone filtered reflection test build" -Action {
        & ".\dotnet.cmd" build `
            "-c" $Configuration `
            "src\tests\async\reflection\reflection.csproj" `
            "--no-restore" `
            "/p:TargetOS=windows" `
            "/p:TargetArchitecture=x64" `
            "/p:RuntimeFlavor=coreclr" `
            "/p:RuntimeConfiguration=$Configuration" `
            "/p:LibrariesConfiguration=$Configuration" `
            "/p:BuildAsStandalone=true" `
            "/p:TestFilter=DynamicMethod_SetImplementationFlags"
    }

    $configurationSegment = "windows.x64.$Configuration"
    $coreRoot = Join-Path $runtimeRootPath "artifacts\tests\coreclr\$configurationSegment\Tests\Core_Root"
    $testDirectory = Join-Path $runtimeRootPath "artifacts\tests\coreclr\$configurationSegment\async\reflection\reflection"

    if (-not (Test-Path -LiteralPath $coreRoot -PathType Container)) {
        throw "Core_Root was not produced: $coreRoot"
    }

    $roslynAssemblies = @(
        Get-ChildItem -LiteralPath $coreRoot -Filter "Microsoft.CodeAnalysis*.dll" -File -ErrorAction SilentlyContinue)
    foreach ($assembly in $roslynAssemblies) {
        Remove-Item -LiteralPath $assembly.FullName -Force
    }

    $requiredComponents = @(
        "corerun.exe",
        "coreclr.dll",
        "clrjit.dll",
        "System.Private.CoreLib.dll")

    foreach ($component in $requiredComponents) {
        $componentPath = Join-Path $coreRoot $component
        if (-not (Test-Path -LiteralPath $componentPath -PathType Leaf)) {
            throw "Canonical Core_Root component is missing: $componentPath"
        }
    }

    $remainingRoslyn = @(
        Get-ChildItem -LiteralPath $coreRoot -Filter "Microsoft.CodeAnalysis*.dll" -File -ErrorAction SilentlyContinue)
    if ($remainingRoslyn.Count -ne 0) {
        throw "Roslyn assemblies remain in canonical Core_Root after stripping."
    }

    $corerun = Join-Path $coreRoot "corerun.exe"
    $testAssembly = Join-Path $testDirectory "reflection.dll"
    if (-not (Test-Path -LiteralPath $testAssembly -PathType Leaf)) {
        throw "Filtered reflection test assembly was not produced: $testAssembly"
    }

    Push-Location $testDirectory
    try {
        Invoke-Checked `
            -Description "Filtered runtime-async DynamicMethod execution" `
            -SuccessExitCodes @(100) `
            -Action { & $corerun ".\reflection.dll" }
    }
    finally {
        Pop-Location
    }

    $head = (& git rev-parse HEAD).Trim()
    Write-Host "Canonical Core_Root promoted successfully."
    Write-Host "Runtime commit: $head"
    Write-Host "Core_Root: $coreRoot"
    Write-Host "Removed Roslyn assemblies: $($roslynAssemblies.Count)"
}
finally {
    Pop-Location
}
