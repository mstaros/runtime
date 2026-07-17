<#
.SYNOPSIS
Builds revision-pinned runtime-async IL verification artifacts.

.DESCRIPTION
Builds the ILVerify command-line tool and ILVerification library from this runtime
checkout, verifies that both projects import the same ILVerification.projitems file,
and writes a manifest containing the exact Git revision and SHA-256 hashes.

External consumers should accept only clean artifacts whose manifest revision matches
the runtime revision they require. Use -AllowDirty only for local validation.
#>
[CmdletBinding()]
param(
    [string]$RuntimeRoot = "",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$OutputDirectory = "",

    [string]$ExpectedRevision = "",

    [switch]$AllowDirty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RuntimeRoot)) {
    $RuntimeRoot = $PSScriptRoot
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Action,

        [Parameter(Mandatory)]
        [string]$Description
    )

    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Get-SharedVerifierImport {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectPath
    )

    [xml]$project = Get-Content -LiteralPath $ProjectPath -Raw
    $imports = @($project.Project.Import | Where-Object {
        $_.Project -and $_.Project.EndsWith("ILVerification.projitems", [StringComparison]::OrdinalIgnoreCase)
    })

    if ($imports.Count -ne 1) {
        throw "Expected exactly one ILVerification.projitems import in $ProjectPath; found $($imports.Count)."
    }

    $projectDirectory = Split-Path -Parent $ProjectPath
    return [IO.Path]::GetFullPath((Join-Path $projectDirectory $imports[0].Project))
}

function Get-ArtifactRecord {
    param(
        [Parameter(Mandatory)]
        [string]$ArtifactRoot,

        [Parameter(Mandatory)]
        [string]$FilePath
    )

    $resolvedRoot = (Resolve-Path -LiteralPath $ArtifactRoot).Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
    $resolvedFile = (Resolve-Path -LiteralPath $FilePath).Path
    $prefix = $resolvedRoot + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedFile.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Artifact file is outside the artifact root: $resolvedFile"
    }

    $relativePath = $resolvedFile.Substring($prefix.Length).Replace('\', '/')
    $item = Get-Item -LiteralPath $resolvedFile
    $hash = (Get-FileHash -LiteralPath $resolvedFile -Algorithm SHA256).Hash.ToLowerInvariant()

    return [ordered]@{
        path = $relativePath
        length = $item.Length
        sha256 = $hash
    }
}

$runtimeRootPath = (Resolve-Path -LiteralPath $RuntimeRoot).Path
$cliProject = Join-Path $runtimeRootPath "src\coreclr\tools\ILVerify\ILVerify.csproj"
$libraryProject = Join-Path $runtimeRootPath "src\coreclr\tools\ILVerification\ILVerification.csproj"
$sharedProjectItems = Join-Path $runtimeRootPath "src\coreclr\tools\ILVerification\ILVerification.projitems"
$dotnetCommandName = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { "dotnet.cmd" } else { "dotnet.sh" }
$dotnetCommand = Join-Path $runtimeRootPath $dotnetCommandName

foreach ($requiredFile in @($cliProject, $libraryProject, $sharedProjectItems, $dotnetCommand)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required file was not found: $requiredFile"
    }
}

$cliImport = Get-SharedVerifierImport -ProjectPath $cliProject
$libraryImport = Get-SharedVerifierImport -ProjectPath $libraryProject
$expectedSharedImport = [IO.Path]::GetFullPath($sharedProjectItems)

if (-not $cliImport.Equals($libraryImport, [StringComparison]::OrdinalIgnoreCase)) {
    throw "ILVerify and ILVerification do not import the same verifier project-items file."
}
if (-not $cliImport.Equals($expectedSharedImport, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The shared verifier import resolved to an unexpected path: $cliImport"
}

Push-Location $runtimeRootPath
try {
    $sourceRevision = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceRevision)) {
        throw "Unable to determine the runtime Git revision."
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedRevision) -and
        -not $sourceRevision.Equals($ExpectedRevision, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Runtime revision $sourceRevision does not match expected revision $ExpectedRevision."
    }

    $sourceBranch = (& git rev-parse --abbrev-ref HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to determine the runtime Git branch."
    }

    $statusLines = @(& git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to determine the runtime working-tree status."
    }
    $sourceDirty = $statusLines.Count -ne 0
    if ($sourceDirty -and -not $AllowDirty) {
        throw "The runtime checkout is dirty. Commit or discard changes, or use -AllowDirty for local validation only."
    }

    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $artifactRoot = Join-Path $runtimeRootPath "artifacts\runtime-async-ilverification\$Configuration"
    }
    elseif ([IO.Path]::IsPathRooted($OutputDirectory)) {
        $artifactRoot = [IO.Path]::GetFullPath($OutputDirectory)
    }
    else {
        $artifactRoot = [IO.Path]::GetFullPath((Join-Path $runtimeRootPath $OutputDirectory))
    }

    $cliOutput = Join-Path $artifactRoot "cli"
    $libraryOutput = Join-Path $artifactRoot "library"

    if (Test-Path -LiteralPath $artifactRoot) {
        Remove-Item -LiteralPath $artifactRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $cliOutput -Force | Out-Null
    New-Item -ItemType Directory -Path $libraryOutput -Force | Out-Null

    Invoke-Checked `
        -Description "ILVerification library build" `
        -Action { & $dotnetCommand build $libraryProject -c $Configuration -o $libraryOutput --nologo }

    Invoke-Checked `
        -Description "ILVerify command-line publish" `
        -Action { & $dotnetCommand publish $cliProject -c $Configuration -o $cliOutput --nologo --no-self-contained }

    $libraryAssembly = Join-Path $libraryOutput "ILVerification.dll"
    $cliAssembly = Join-Path $cliOutput "ILVerify.dll"
    $cliRuntimeConfig = Join-Path $cliOutput "ILVerify.runtimeconfig.json"

    foreach ($artifactFile in @($libraryAssembly, $cliAssembly, $cliRuntimeConfig)) {
        if (-not (Test-Path -LiteralPath $artifactFile -PathType Leaf)) {
            throw "Expected verifier artifact was not produced: $artifactFile"
        }
    }

    $artifactFiles = @(
        Get-ChildItem -LiteralPath $artifactRoot -File -Recurse |
            Sort-Object FullName |
            ForEach-Object { Get-ArtifactRecord -ArtifactRoot $artifactRoot -FilePath $_.FullName }
    )

    $manifest = [ordered]@{
        schemaVersion = 1
        contract = "runtime-async-ilverification"
        sourceRevision = $sourceRevision
        sourceBranch = $sourceBranch
        sourceDirty = $sourceDirty
        configuration = $Configuration
        sharedVerifierProjectItems = "src/coreclr/tools/ILVerification/ILVerification.projitems"
        sharedVerifierProjectItemsSha256 = (Get-FileHash -LiteralPath $sharedProjectItems -Algorithm SHA256).Hash.ToLowerInvariant()
        cliProject = "src/coreclr/tools/ILVerify/ILVerify.csproj"
        libraryProject = "src/coreclr/tools/ILVerification/ILVerification.csproj"
        cliEntryAssembly = "cli/ILVerify.dll"
        libraryAssembly = "library/ILVerification.dll"
        requiredSystemModule = "System.Private.CoreLib"
        files = $artifactFiles
    }

    $manifestPath = Join-Path $artifactRoot "runtime-async-ilverification-manifest.json"
    $json = $manifest | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($manifestPath, $json + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))

    Write-Host "Runtime-async IL verification artifacts published."
    Write-Host "Runtime revision: $sourceRevision"
    Write-Host "Source dirty: $sourceDirty"
    Write-Host "Artifact root: $artifactRoot"
    Write-Host "Manifest: $manifestPath"
}
finally {
    Pop-Location
}
