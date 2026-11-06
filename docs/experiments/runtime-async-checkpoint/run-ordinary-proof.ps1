param(
    [Parameter(Mandatory=$true)][string]$RuntimeRoot,
    [Parameter(Mandatory=$true)][string]$ScriptTests,
    [switch]$SkipBuild
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$runtime = (Resolve-Path -LiteralPath $RuntimeRoot).Path
$tests = (Resolve-Path -LiteralPath $ScriptTests).Path
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..\..")).Path
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot "build-runtime.cmd") $runtime
    if ($LASTEXITCODE -ne 0) { throw "Candidate runtime build failed: $LASTEXITCODE" }
}
# Re-run the existing isolated DynamicMethod and GC/publication/configuration proof.
& (Join-Path $PSScriptRoot "run-proof.ps1") -RuntimeRoot $runtime -SkipBuild
$source = Join-Path $repo "artifacts\experiments\runtime-async-checkpoint\Core_Root"
$output = [IO.Directory]::CreateDirectory((Join-Path $runtime "artifacts\experiments\runtime-async-ordinary-checkpoint")).FullName
$core = Join-Path $output "Core_Root"
if (Test-Path -LiteralPath $core) { Remove-Item -LiteralPath $core -Recurse -Force }
[IO.Directory]::CreateDirectory($core) | Out-Null
Copy-Item -Path (Join-Path $source "*") -Destination $core -Recurse -Force
$env:MERKLE_ORDINARY_CHECKPOINT_CORE_ROOT = $core
$env:CORE_ROOT = $core
$env:CORE_LIBRARIES = Split-Path -Parent $tests
$env:APP_ASSEMBLIES = "PROPERTY"
$env:DOTNET_JitAsyncCheckpointProof = "4"
$env:DOTNET_JitNoInline = "0"
$env:COMPlus_JitNoInline = "0"
$env:DOTNET_TieredCompilation = "0"
$env:COMPlus_TieredCompilation = "0"
$env:DOTNET_TC_OnStackReplacement = "0"
$env:DOTNET_ReadyToRun = "0"
$env:COMPlus_ReadyToRun = "0"
$env:MPSAGENT_EXPECTED_RUNNER_PATH = Join-Path $core "corerun.exe"
$env:MPSAGENT_EXPECTED_CORELIB_PATH = Join-Path $core "System.Private.CoreLib.dll"
Remove-Item Env:MPSAGENT_RUNTIME_EVIDENCE_FILE -ErrorAction SilentlyContinue
Remove-Item Env:MERKLE_NESTED_CHECKPOINT_MODE -ErrorAction SilentlyContinue
Remove-Item Env:MERKLE_NESTED_CHECKPOINT_DIRECTORY -ErrorAction SilentlyContinue
$corerun = Join-Path $core "corerun.exe"
$log = Join-Path $output "external-dll-proof.log"
Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue
foreach ($test in @("Script_calls_external_DLL_with_nested_await_and_validator", "External_DLL_checkpoint_survives_repeated_restarts",
        "Script_calls_generic_DLL_methods", "Generic_DLL_checkpoint_survives_restart",
        "Collectible_DLL_checkpoint_survives_restart", "Collectible_generic_DLL_checkpoint_survives_restart",
        "DLL_result_types_checkpoint_survives_restart", "DLL_generic_result_types_checkpoint_survives_restart",
        "DLL_value_slots_checkpoint_survives_restart", "DLL_reference_struct_slots_checkpoint_survives_restart",
        "DLL_value_task_and_awaiter_checkpoint_survives_restart")) {
    $lines = & $corerun $tests --treenode-filter ("/*/*/NestedCheckpointTests/" + $test) --maximum-parallel-tests 1 2>&1 | Tee-Object -FilePath $log -Append
    $lines | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "External DLL proof failed: $test ($LASTEXITCODE)" }
    $summary = $lines -join [Environment]::NewLine
    if ($summary -notmatch '(?im)^\s*total:\s*1\s*$' -or $summary -notmatch '(?im)^\s*skipped:\s*0\s*$') {
        throw "Expected one executed case for $test. Build the current Merkle test project before running this proof."
    }
}
$receipt = Join-Path (Split-Path -Parent $tests) "TestResults\external-dll-checkpoint.receipt.json"
if (-not (Test-Path -LiteralPath $receipt)) { throw "External DLL proof did not publish its receipt." }
Copy-Item -LiteralPath $receipt -Destination (Join-Path $output "receipt.json") -Force
$genericReceipt = Join-Path (Split-Path -Parent $tests) "TestResults\generic-dll-checkpoint.receipt.json"
if (-not (Test-Path -LiteralPath $genericReceipt)) { throw "Generic DLL proof did not publish its receipt." }
Copy-Item -LiteralPath $genericReceipt -Destination (Join-Path $output "generic-receipt.json") -Force
foreach ($name in @("collectible-dll", "collectible-generic", "result-types", "generic-result-types", "value-slots", "reference-struct-slots", "value-task-awaiter")) {
    $namedReceipt = Join-Path (Split-Path -Parent $tests) "TestResults\$name-checkpoint.receipt.json"
    if (-not (Test-Path -LiteralPath $namedReceipt)) { throw "Proof did not publish its receipt: $name" }
    Copy-Item -LiteralPath $namedReceipt -Destination (Join-Path $output "$name-receipt.json") -Force
}
Write-Host "ORDINARY METHOD CHECKPOINT PROOF PASSED: $output"
