# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RuntimeRoot,
    [Parameter(Mandatory = $true)][string]$CompilerRoot,
    [string]$PackageRoot = "D:\cache\nuget",
    [ValidateRange(1, 2000)][int]$Count = 32,
    [ValidateRange(1, 5)][int]$Repetitions = 3,
    [string]$OutputDirectory = "",
    [switch]$Pilot,
    [switch]$Profile,
    # Staged bin DLLs are checked against the <project>.build_info.json sidecar in their folder, written by
    # CSharpMpc dotnet_build. Findings are recorded in results.json and warned about; -RequireBuildInfo fails the run.
    [string]$ExpectedCommit = "",
    [switch]$RequireBuildInfo
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$runtime = (Resolve-Path -LiteralPath $RuntimeRoot).Path
$compilerRepo = (Resolve-Path -LiteralPath $CompilerRoot).Path
$experimentRepo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..\..")).Path
$coreRoot = Join-Path $runtime "artifacts\tests\coreclr\windows.x64.Release\Tests\Core_Root"
$corerun = Join-Path $coreRoot "corerun.exe"
$dotnet = Join-Path $runtime ".dotnet\dotnet.exe"
$sdk = (Get-Content -LiteralPath (Join-Path $runtime "global.json") -Raw | ConvertFrom-Json).sdk.version
$csc = Join-Path $runtime ".dotnet\sdk\$sdk\Roslyn\bincore\csc.dll"
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $experimentRepo ("artifacts\experiments\pipeline-memory\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
}
$output = [IO.Directory]::CreateDirectory($OutputDirectory).FullName
$worker = Join-Path $output "PipelineExperiment.dll"
$staged = @{}
$buildInfoSources = @{}
$buildInfoIssues = New-Object System.Collections.Generic.List[string]
function Get-BuildInfo([string]$directory, [string]$project) {
    if ($buildInfoSources.ContainsKey($directory)) { return $buildInfoSources[$directory] }
    $preferred = Join-Path $directory ($project + ".build_info.json")
    $candidates = @(Get-ChildItem -LiteralPath $directory -Filter "*.build_info.json" -File)
    $path = $null
    if (Test-Path -LiteralPath $preferred -PathType Leaf) { $path = $preferred }
    elseif ($candidates.Count -eq 1) { $path = $candidates[0].FullName }
    $info = [ordered]@{ directory = $directory; sidecar = $path; commit = $null; dirty = $null; configuration = $null; sdk = $null; files = @{} }
    if ($null -eq $path) {
        $buildInfoIssues.Add("No build info sidecar for $project in $directory ($($candidates.Count) candidates).")
    } else {
        try {
            $document = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            if ($document.formatVersion -ne 1) { throw "unsupported formatVersion $($document.formatVersion)" }
            $info.commit = [string]$document.commit
            $info.dirty = [bool]$document.dirty
            $info.configuration = [string]$document.configuration
            $info.sdk = [string]$document.sdk
            foreach ($file in @($document.files)) { $info.files[[string]$file.relativePath] = $file }
        } catch {
            $buildInfoIssues.Add("Unreadable build info sidecar ${path}: $($_.Exception.Message)")
        }
    }
    $buildInfoSources[$directory] = $info
    return $info
}
function Test-BuildInfo([string]$source, [string]$project, [string]$hash, [long]$length) {
    $info = Get-BuildInfo ([IO.Path]::GetDirectoryName($source)) $project
    if ($null -eq $info.sidecar) { return }
    $name = [IO.Path]::GetFileName($source)
    if (-not $info.files.ContainsKey($name)) {
        $buildInfoIssues.Add("$source is not listed in $($info.sidecar).")
    } elseif ([long]$info.files[$name].length -ne $length -or [string]$info.files[$name].sha256 -ne $hash) {
        $buildInfoIssues.Add("$source does not match $($info.sidecar); the output changed after the recorded build.")
    }
}
function Copy-Payload([string]$source, [string]$project = "") {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing payload: $source" }
    $name = [IO.Path]::GetFileName($source)
    if (Test-Path -LiteralPath (Join-Path $coreRoot $name)) { return }
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    $length = (Get-Item -LiteralPath $source).Length
    if ($project) { Test-BuildInfo $source $project $hash $length }
    if ($staged.ContainsKey($name)) {
        if ($staged[$name].sha256 -ne $hash) { throw "Conflicting versions of $name" }
        return
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $output $name)
    $staged[$name] = [ordered]@{ name = $name; source = $source; sha256 = $hash; bytes = $length }
}
foreach ($project in @("MpsAgent.Merkle.Compiler", "MpsAgent.Merkle.Bridge")) {
    $built = Join-Path $compilerRepo "$project\bin\Release\net11.0"
    foreach ($file in Get-ChildItem -LiteralPath $built -Filter "*.dll" -File) { Copy-Payload $file.FullName $project }
    $deps = Get-Content -LiteralPath (Join-Path $built "$project.deps.json") -Raw | ConvertFrom-Json
    $target = @($deps.targets.PSObject.Properties)[0].Value
    foreach ($library in $target.PSObject.Properties) {
        $definition = $deps.libraries.PSObject.Properties[$library.Name].Value
        if ($definition.type -ne "package") { continue }
        $runtimeProperty = $library.Value.PSObject.Properties["runtime"]
        if ($null -eq $runtimeProperty) { continue }
        foreach ($asset in $runtimeProperty.Value.PSObject.Properties) {
            if ($asset.Name.EndsWith(".dll")) {
                Copy-Payload (Join-Path (Join-Path $PackageRoot $definition.path) $asset.Name)
            }
        }
    }
}
$verifiedSources = @($buildInfoSources.Values | Where-Object { $null -ne $_.sidecar -and $null -ne $_.commit })
$buildInfoCommits = @($verifiedSources | ForEach-Object { $_.commit } | Sort-Object -Unique)
if ($buildInfoCommits.Count -gt 1) {
    $buildInfoIssues.Add("Staged outputs were built from different commits: $($buildInfoCommits -join ', ').")
}
foreach ($source in $verifiedSources) {
    if ($source.dirty) { $buildInfoIssues.Add("$($source.sidecar) records a dirty working tree.") }
    if ($ExpectedCommit -and -not $source.commit.StartsWith($ExpectedCommit, [StringComparison]::OrdinalIgnoreCase)) {
        $buildInfoIssues.Add("$($source.sidecar) records commit $($source.commit), expected $ExpectedCommit.")
    }
}
if ($buildInfoIssues.Count -gt 0) {
    $buildInfoMessage = "Build info verification failed:`n  " + ($buildInfoIssues -join "`n  ")
    if ($RequireBuildInfo) { throw $buildInfoMessage }
    Write-Warning $buildInfoMessage
}
$buildInfoCommit = $null
if ($buildInfoCommits.Count -eq 1) { $buildInfoCommit = $buildInfoCommits[0] }
$buildInfo = [ordered]@{
    required = [bool]$RequireBuildInfo
    verified = ($buildInfoIssues.Count -eq 0)
    expectedCommit = $ExpectedCommit
    commit = $buildInfoCommit
    issues = @($buildInfoIssues)
    sources = @($buildInfoSources.Values | Sort-Object { $_.directory } | ForEach-Object {
        [ordered]@{ directory = $_.directory; sidecar = $_.sidecar; commit = $_.commit; dirty = $_.dirty; configuration = $_.configuration; sdk = $_.sdk }
    })
}
# The source checkout HEAD is context only; buildInfo carries the verified build commit.
$compilerSourceHead = (& git -C $compilerRepo rev-parse HEAD).Trim()
$referenceNames = @(
    "System.Private.CoreLib", "System.Runtime", "System.Console", "System.Collections", "System.Collections.NonGeneric",
    "System.Collections.Immutable", "System.Reflection.Metadata", "System.Reflection.Emit", "System.Reflection.Emit.ILGeneration",
    "System.Reflection.Emit.Lightweight", "System.Diagnostics.Process", "System.ComponentModel.Primitives",
    "System.Runtime.InteropServices", "System.Runtime.Loader", "System.Threading", "System.Threading.Tasks",
    "System.Linq", "System.Memory", "System.Text.Json", "System.Text.Encoding.Extensions", "System.Diagnostics.Tracing"
)
$compileArgs = @("/nologo", "/target:exe", "/optimize+", "/nullable:enable", "/langversion:preview", "/nostdlib+", ('/out:"' + $worker + '"'))
foreach ($name in $referenceNames) { $compileArgs += '/reference:"' + (Join-Path $coreRoot ($name + ".dll")) + '"' }
foreach ($name in @("Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp", "MpsAgent.Merkle.Compiler", "MpsAgent.Merkle.Bridge", "MerkleV2")) {
    $compileArgs += '/reference:"' + (Join-Path $output ($name + ".dll")) + '"'
}
$compileArgs += '"' + (Join-Path $PSScriptRoot "PipelineExperiment.cs") + '"'
$rsp = Join-Path $output "compile.rsp"
[IO.File]::WriteAllLines($rsp, $compileArgs)
& $dotnet exec $csc ("@" + $rsp)
if ($LASTEXITCODE -ne 0) { throw "Worker compilation failed: $LASTEXITCODE" }
$binaries = @()
foreach ($name in @("corerun.exe", "coreclr.dll", "clrjit.dll", "System.Private.CoreLib.dll")) {
    $path = Join-Path $coreRoot $name
    $binaries += [ordered]@{ name = $name; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$settings = [ordered]@{
    COMPlus_TieredCompilation = "0"; DOTNET_TieredCompilation = "0"
    COMPlus_ReadyToRun = "0"; DOTNET_ReadyToRun = "0"
    COMPlus_gcServer = "0"; DOTNET_gcServer = "0"
    COMPlus_GCStress = "0"; DOTNET_GCStress = "0"
}
$cases = @()
$sizes = if ($Pilot) { @(24) } elseif ($Profile) { @(192) } else { @(24, 96, 192) }
foreach ($size in $sizes) {
    $modes = if ($Profile) { @("merkle") } else { @("merkle", "roslyn-classic") }
    foreach ($mode in $modes) {
        $caseCount = if ($size -ge 192) { [Math]::Min($Count, 2) } elseif ($size -ge 96) { [Math]::Min($Count, 8) } else { $Count }
        $cases += [ordered]@{ mode = $mode; statements = $size; count = $caseCount }
    }
}
if (-not $Profile) {
foreach ($size in $sizes) { $cases += [ordered]@{ mode = "diagnostics"; statements = $size; count = $Count } }
$controlCount = if ($Pilot) { $Count } else { 1000 }
$cases += [ordered]@{ mode = "alc-load"; statements = 24; count = $controlCount }
$cases += [ordered]@{ mode = "alc-empty"; statements = 0; count = $controlCount }
}
$runs = @()
for ($rep = 1; $rep -le $Repetitions; $rep++) {
    for ($offset = 0; $offset -lt $cases.Count; $offset++) {
        $case = $cases[($offset + ($rep - 1) * 2) % $cases.Count]
        $id = "$($case.mode)-s$($case.statements)-n$($case.count)-r$rep"
        Write-Host "Running $id"
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $corerun
        $start.Arguments = '"' + $worker + '" ' + $case.mode + " " + $case.count + " " + $case.statements
        $start.WorkingDirectory = $output
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($key in $settings.Keys) { $start.EnvironmentVariables[$key] = $settings[$key] }
        if ($Profile) {
            $start.EnvironmentVariables["PIPELINE_PROFILE"] = "1"
            $start.EnvironmentVariables["DOTNET_EnableEventPipe"] = "1"
            $start.EnvironmentVariables["DOTNET_EventPipeOutputPath"] = (Join-Path $output ($id + ".nettrace"))
            $start.EnvironmentVariables["DOTNET_EventPipeCircularMB"] = "100"
            $start.EnvironmentVariables["DOTNET_EventPipeConfig"] = "Microsoft-Windows-DotNETRuntime:40000019:5,Microsoft-DotNETCore-SampleProfiler:0:5,Pipeline-Profile:ffffffffffffffff:5"
        }
        $process = New-Object Diagnostics.Process
        $process.StartInfo = $start
        if (-not $process.Start()) { throw "Unable to start $id" }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(240000)) {
            $process.Kill()
            throw "Worker exceeded 240 seconds: $id"
        }
        $stdout = $stdoutTask.Result
        $stderr = $stderrTask.Result
        $exitCode = $process.ExitCode
        $process.Dispose()
        [IO.File]::WriteAllText((Join-Path $output ($id + ".json")), $stdout)
        [IO.File]::WriteAllText((Join-Path $output ($id + ".stderr.txt")), $stderr)
        if ($exitCode -ne 0) { throw "Worker $id failed ($exitCode): $stderr" }
        $result = $stdout | ConvertFrom-Json
        $runs += [ordered]@{ id = $id; repetition = $rep; result = $result }
        [ordered]@{
            utc = (Get-Date).ToUniversalTime().ToString("o"); count = $Count; repetitions = $Repetitions; profile = [bool]$Profile
            runtimeRoot = $runtime; compilerRoot = $compilerRepo
            compilerSourceHeadContextOnly = $compilerSourceHead
            buildInfo = $buildInfo
            experimentSources = @("PipelineExperiment.cs", "run-pipeline.ps1") | ForEach-Object { [ordered]@{ name = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $_) -Algorithm SHA256).Hash.ToLowerInvariant() } }
            binaries = $binaries; payload = @($staged.Values | Sort-Object name); settings = $settings
            runs = $runs
        } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output "results.json") -Encoding UTF8
        if ($case.mode -eq "diagnostics") {
            Write-Host ("First diagnostics {0:N3} ms/script; repeat {1:N3} ms/script" -f ($result.phases.firstDiagnostics.Milliseconds / $case.count), ($result.phases.repeatDiagnostics.Milliseconds / $case.count))
            continue
        }
        $baseline = $result.snapshots | Where-Object stage -eq "baseline"
        $stage = if ($case.mode -eq "alc-empty") { "generated" } else { "jitted" }
        $live = $result.snapshots | Where-Object stage -eq $stage
        Write-Host ("Generation {0:N3} ms/script; first call {1:N3} ms/script; retained private {2:N1} KiB/script; managed {3:N1} KiB/script" -f
            ($result.generationMs / $case.count), ($result.firstInvokeMs / $case.count),
            (($live.privateBytes - $baseline.privateBytes) / 1KB / $case.count),
            (($live.managedLiveBytes - $baseline.managedLiveBytes) / 1KB / $case.count))
    }
}
Write-Host "RESULTS=$output\results.json"
