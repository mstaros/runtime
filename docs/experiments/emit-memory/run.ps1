# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RuntimeRoot,
    [ValidateRange(1, 4096)][int]$Count = 1000,
    [ValidateRange(1, 10)][int]$Repetitions = 3,
    [string]$OutputDirectory = "",
    [switch]$SizeSweep
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$runtime = (Resolve-Path -LiteralPath $RuntimeRoot).Path
$experimentRepo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..\..")).Path
$coreRoot = Join-Path $runtime "artifacts\tests\coreclr\windows.x64.Release\Tests\Core_Root"
$corerun = Join-Path $coreRoot "corerun.exe"
$dotnet = Join-Path $runtime ".dotnet\dotnet.exe"
$sdk = (Get-Content -LiteralPath (Join-Path $runtime "global.json") -Raw | ConvertFrom-Json).sdk.version
$compiler = Join-Path $runtime ".dotnet\sdk\$sdk\Roslyn\bincore\csc.dll"
foreach ($required in @($corerun, $dotnet, $compiler)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Missing component: $required" }
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $experimentRepo ("artifacts\experiments\emit-memory\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
}
$output = [IO.Directory]::CreateDirectory($OutputDirectory).FullName
$worker = Join-Path $output "MemoryExperiment.dll"
$response = Join-Path $output "compile.rsp"
$references = @(
    "System.Private.CoreLib", "System.Runtime", "System.Console", "System.Collections", "System.Collections.NonGeneric",
    "System.Reflection.Emit", "System.Reflection.Emit.ILGeneration", "System.Reflection.Emit.Lightweight",
    "System.Diagnostics.Process", "System.ComponentModel.Primitives", "System.Runtime.InteropServices",
    "System.Runtime.Loader", "System.Threading", "System.Threading.Tasks"
)
$arguments = @("/nologo", "/target:exe", "/optimize+", "/nullable:enable", "/langversion:preview", "/nostdlib+",
    ('/out:"' + $worker + '"'))
foreach ($reference in $references) {
    $file = Join-Path $coreRoot ($reference + ".dll")
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing reference: $file" }
    $arguments += '/reference:"' + $file + '"'
}
$arguments += '"' + (Join-Path $PSScriptRoot "MemoryExperiment.cs") + '"'
[IO.File]::WriteAllLines($response, $arguments)
& $dotnet exec $compiler ("@" + $response)
if ($LASTEXITCODE -ne 0) { throw "Experiment compilation failed: $LASTEXITCODE" }

$binaryInfo = @()
foreach ($name in @("corerun.exe", "coreclr.dll", "clrjit.dll", "System.Private.CoreLib.dll")) {
    $file = Join-Path $coreRoot $name
    $info = Get-Item -LiteralPath $file
    $binaryInfo += [ordered]@{
        name = $name
        sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        productVersion = $info.VersionInfo.ProductVersion
        size = $info.Length
    }
}
$sourceHead = (& git -C $runtime rev-parse HEAD).Trim()
$settings = [ordered]@{
    COMPlus_TieredCompilation = "0"; DOTNET_TieredCompilation = "0"
    COMPlus_ReadyToRun = "0"; DOTNET_ReadyToRun = "0"
    COMPlus_gcServer = "0"; DOTNET_gcServer = "0"
    COMPlus_GCStress = "0"; DOTNET_GCStress = "0"
}
if ($SizeSweep) {
    $cases = @()
    foreach ($statements in @(0, 24, 48, 96, 192)) {
        $cases += [ordered]@{ mode = "dynamic"; batch = 1; bodyStatements = $statements }
        $cases += [ordered]@{ mode = "assembly"; batch = 1; bodyStatements = $statements }
    }
} else {
    $cases = @(
        [ordered]@{ mode = "dynamic"; batch = 1; bodyStatements = 0 },
        [ordered]@{ mode = "assembly"; batch = 1; bodyStatements = 0 },
        [ordered]@{ mode = "assembly"; batch = [Math]::Min(100, $Count); bodyStatements = 0 },
        [ordered]@{ mode = "assembly-shape"; batch = 1; bodyStatements = 0 }
    )
}
$experimentSources = @()
foreach ($name in @("MemoryExperiment.cs", "run.ps1")) {
    $experimentSources += [ordered]@{
        name = $name
        sha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$runs = @()
for ($rep = 1; $rep -le $Repetitions; $rep++) {
    for ($offset = 0; $offset -lt $cases.Count; $offset++) {
        $case = $cases[($offset + ($rep - 1) * 3) % $cases.Count]
        $id = "$($case.mode)-b$($case.batch)-s$($case.bodyStatements)-n$Count-r$rep"
        Write-Host "Running $id"
        $start = New-Object Diagnostics.ProcessStartInfo
        $start.FileName = $corerun
        $start.Arguments = '"' + $worker + '" ' + $case.mode + " " + $Count + " " + $case.batch + " " + $case.bodyStatements
        $start.WorkingDirectory = $output
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($key in $settings.Keys) { $start.EnvironmentVariables[$key] = $settings[$key] }
        $process = New-Object Diagnostics.Process
        $process.StartInfo = $start
        if (-not $process.Start()) { throw "Unable to start $id" }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(180000)) {
            $process.Kill()
            throw "Worker exceeded 180 seconds: $id"
        }
        $stdout = $stdoutTask.Result
        $stderr = $stderrTask.Result
        $exitCode = $process.ExitCode
        $process.Dispose()
        [IO.File]::WriteAllText((Join-Path $output ($id + ".tsv")), $stdout)
        [IO.File]::WriteAllText((Join-Path $output ($id + ".stderr.txt")), $stderr)
        if ($exitCode -ne 0) { throw "Worker $id failed ($exitCode): $stderr" }
        $snapshots = @()
        foreach ($row in ($stdout | ConvertFrom-Csv -Delimiter "`t")) {
            if ($row.stage -eq "warmup") { continue }
            $snapshot = [ordered]@{ stage = $row.stage }
            foreach ($property in $row.PSObject.Properties) {
                if ($property.Name -eq "stage") { continue }
                if ($property.Name -eq "phaseMs") {
                    $snapshot[$property.Name] = [double]::Parse($property.Value, [Globalization.CultureInfo]::InvariantCulture)
                } else {
                    $snapshot[$property.Name] = [long]::Parse($property.Value, [Globalization.CultureInfo]::InvariantCulture)
                }
            }
            $snapshots += $snapshot
        }
        if ($snapshots.Count -ne 6) { throw "Expected six measured stages for $id" }
        $runs += [ordered]@{ id = $id; mode = $case.mode; batch = $case.batch; repetition = $rep; bodyStatements = $case.bodyStatements; runtime = $stderr.Trim(); snapshots = $snapshots }
        [ordered]@{
            utc = (Get-Date).ToUniversalTime().ToString("o"); count = $Count; repetitions = $Repetitions
            runtimeRoot = $runtime; sourceHeadContextOnly = $sourceHead; binaries = $binaryInfo
            settings = $settings; sizeSweep = [bool]$SizeSweep; experimentSources = $experimentSources; runs = $runs
        } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output "results.json") -Encoding UTF8
        $base = $snapshots | Where-Object { $_.stage -eq "baseline" }
        $live = $snapshots | Where-Object { $_.stage -eq "jitted" }
        Write-Host ("Retained private delta: {0:N2} MiB; managed live delta: {1:N2} MiB" -f
            (($live.privateBytes - $base.privateBytes) / 1MB), (($live.managedLiveBytes - $base.managedLiveBytes) / 1MB))
    }
}
Write-Host "RESULTS=$output\results.json"
