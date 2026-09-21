# Licensed to the .NET Foundation under one or more agreements.
# The .NET Foundation licenses this file to you under the MIT license.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RuntimeRoot,
    [Parameter(Mandatory = $true)][string]$CaptureDirectory,
    [string]$PackageRoot = "D:\cache\nuget"
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$runtime = (Resolve-Path -LiteralPath $RuntimeRoot).Path
$capture = (Resolve-Path -LiteralPath $CaptureDirectory).Path
# Preserve original traces outside the disposable transaction worktree.
$output = [IO.Directory]::CreateDirectory((Join-Path $runtime ("artifacts\experiments\pipeline-profile\" + [IO.Path]::GetFileName($capture)))).FullName
foreach ($file in Get-ChildItem -LiteralPath $capture -File) {
    if ($file.Extension -in @(".nettrace", ".json") -or $file.Name.EndsWith(".stderr.txt")) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $output $file.Name) -Force
    }
}
$analysis = [IO.Directory]::CreateDirectory((Join-Path $output "analysis")).FullName
$core = Join-Path $runtime "artifacts\tests\coreclr\windows.x64.Release\Tests\Core_Root"
$traceLib = Join-Path $PackageRoot "microsoft.diagnostics.tracing.traceevent\3.1.28\lib\netstandard2.0"
foreach ($dll in Get-ChildItem -LiteralPath $traceLib -Filter "*.dll" -File) { Copy-Item $dll.FullName $analysis -Force }
Copy-Item (Join-Path $PackageRoot "microsoft.diagnostics.netcore.client\0.2.510501\lib\net6.0\Microsoft.Diagnostics.NETCore.Client.dll") $analysis -Force
$native = [IO.Directory]::CreateDirectory((Join-Path $analysis "amd64")).FullName
foreach ($dll in Get-ChildItem -LiteralPath (Join-Path $PackageRoot "microsoft.diagnostics.tracing.traceevent\3.1.28\build\native\amd64") -Filter "*.dll" -File) {
    Copy-Item $dll.FullName $native -Force
}
$sdk = (Get-Content -LiteralPath (Join-Path $runtime "global.json") -Raw | ConvertFrom-Json).sdk.version
$csc = Join-Path $runtime ".dotnet\sdk\$sdk\Roslyn\bincore\csc.dll"
$exe = Join-Path $analysis "ProfileAnalysis.dll"
$compileArgs = @("/nologo", "/target:exe", "/optimize+", "/nullable:enable", "/langversion:preview", "/nostdlib+", ('/out:"' + $exe + '"'))
foreach ($name in @("System.Private.CoreLib", "System.Runtime", "System.Console", "System.Collections", "System.Collections.Immutable", "System.Linq", "System.Linq.Expressions", "System.Memory", "System.Text.Json", "System.Runtime.Extensions", "netstandard")) {
    $compileArgs += '/reference:"' + (Join-Path $core ($name + ".dll")) + '"'
}
$compileArgs += '/reference:"' + (Join-Path $analysis "Microsoft.Diagnostics.Tracing.TraceEvent.dll") + '"'
$compileArgs += '/reference:"' + (Join-Path $analysis "Microsoft.Diagnostics.FastSerialization.dll") + '"'
$compileArgs += '"' + (Join-Path $PSScriptRoot "ProfileAnalysis.cs") + '"'
$rsp = Join-Path $analysis "compile.rsp"
[IO.File]::WriteAllLines($rsp, $compileArgs)
& (Join-Path $runtime ".dotnet\dotnet.exe") exec $csc ("@" + $rsp)
if ($LASTEXITCODE -ne 0) { throw "Analysis compilation failed: $LASTEXITCODE" }
foreach ($trace in Get-ChildItem -LiteralPath $output -Filter "*.nettrace" -File) {
    $summary = Join-Path $output ($trace.BaseName + ".profile.json")
    & (Join-Path $core "corerun.exe") $exe $trace.FullName $summary
    if ($LASTEXITCODE -ne 0) { throw "Trace analysis failed: $LASTEXITCODE" }
    $hash = (Get-FileHash -LiteralPath $trace.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [ordered]@{ trace = $trace.FullName; bytes = $trace.Length; sha256 = $hash; analyzer = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot "ProfileAnalysis.cs") -Algorithm SHA256).Hash.ToLowerInvariant(); traceEventVersion = "3.1.28" } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output ($trace.BaseName + ".trace-manifest.json")) -Encoding UTF8
}
Write-Host "PROFILE_OUTPUT=$output"
