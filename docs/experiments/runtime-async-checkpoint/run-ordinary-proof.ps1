param(
    [Parameter(Mandatory=$true)][string]$RuntimeRoot,
    [Parameter(Mandatory=$true)][string]$ScriptTests,
    [switch]$StatementCoverage,
    [switch]$SkipBuild,
    [string]$HostTests,
    [string]$AspNetRoot
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
$output = [IO.Directory]::CreateDirectory((Join-Path $runtime "artifacts\experiments\runtime-async-ordinary-checkpoint")).FullName
$proofDir = Join-Path $repo "artifacts\experiments\runtime-async-checkpoint"
$hostArgs = @{}
$stagedHost = $null
if (([bool]$HostTests) -ne ([bool]$AspNetRoot)) { throw "Supply -HostTests and -AspNetRoot together." }
try {
    if ($HostTests) {
        # ScriptMcp's test project consumes a packaged ScriptPlatform. Exercise the same compiler and
        # Merkle assemblies as the script suite without replacing any files in either build output.
        $hostAssembly = (Resolve-Path -LiteralPath $HostTests).Path
        $stagedHost = [IO.Directory]::CreateDirectory((Join-Path $proofDir ("host-" + [Guid]::NewGuid().ToString("N")))).FullName
        Get-ChildItem -LiteralPath (Split-Path -Parent $hostAssembly) -Exclude TestResults |
            Copy-Item -Destination $stagedHost -Recurse -Force
        foreach ($name in @("MerkleV2", "MpsAgent.Merkle.Bridge", "MpsAgent.Merkle.Compiler",
                "MpsAgent.Merkle.ControlFlow", "MpsAgent.Merkle.DynamicMethodIlVerification",
                "MpsAgent.Merkle.EmitRuntime", "MpsAgent.Merkle.Runtime")) {
            Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $tests) "$name.dll") -Destination $stagedHost -Force
        }
        $hostArgs = @{ ScriptTests = Join-Path $stagedHost ([IO.Path]::GetFileName($hostAssembly)); AspNetRoot = $AspNetRoot }
        $hostReceipt = Join-Path $output "scriptmcp-receipt.json"
        if (Test-Path -LiteralPath $hostReceipt) { Remove-Item -LiteralPath $hostReceipt -Force }
    }
    & (Join-Path $PSScriptRoot "run-proof.ps1") -RuntimeRoot $runtime -SkipBuild @hostArgs
    if ($HostTests) {
        $receiptPath = Join-Path $proofDir "scriptmcp\receipt.json"
        $receipt = Get-Content -Raw -LiteralPath $receiptPath | ConvertFrom-Json
        if (-not $receipt.slotContract.pendingTaskInBag -or -not $receipt.slotContract.wrongWaitRejected -or
            -not $receipt.slotContract.globalsBound -or -not $receipt.slotContract.wrongGlobalsRejected -or
            -not $receipt.slotContract.protectedGlobalsFinally -or -not $receipt.slotContract.protectedGlobalsCatchAndFilter -or
            $receipt.slotContract.globalsFinallyCases.Count -ne 13 -or
            @($receipt.slotContract.globalsFinallyCases | Where-Object {
                $_.finallyCount -ne 1 -or $_.decodedGlobals -ne 1 -or $_.prefixCalls -ne 0 -or $_.betweenCalls -ne 0 -or
                ($_.outcome -eq "success" -and ($_.filterCalls -ne 0 -or $_.catchCount -ne 0 -or $_.fallbackCatchCount -ne 0)) -or
                ($_.outcome -ne "success" -and ($_.filterCalls -ne 1 -or -not $_.filterGlobalsValid -or
                    ($_.filterOutcome -eq "accept" -and ($_.catchCount -ne 1 -or $_.fallbackCatchCount -ne 0)) -or
                    ($_.filterOutcome -ne "accept" -and ($_.catchCount -ne 0 -or $_.fallbackCatchCount -ne 1))))
            }).Count -ne 0) {
            throw "The host receipt lacks the ordinary Task<T> bag and protected catch/filter/finally globals proof."
        }
        Copy-Item -LiteralPath $receiptPath -Destination $hostReceipt -Force
    }
}
finally {
    if ($stagedHost) { Remove-Item -LiteralPath $stagedHost -Recurse -Force }
}
$source = Join-Path $proofDir "Core_Root"
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
$proofTests = @("Script_calls_external_DLL_with_nested_await_and_validator", "External_DLL_checkpoint_survives_repeated_restarts",
        "Script_calls_generic_DLL_methods", "Generic_DLL_checkpoint_survives_restart",
        "Collectible_DLL_checkpoint_survives_restart", "Collectible_generic_DLL_checkpoint_survives_restart",
        "DLL_result_types_checkpoint_survives_restart", "DLL_generic_result_types_checkpoint_survives_restart",
        "DLL_value_slots_checkpoint_survives_restart", "DLL_reference_struct_slots_checkpoint_survives_restart",
        "DLL_value_task_and_awaiter_checkpoint_survives_restart",
        "Retained_local_function_environment_requires_compiler_binding",
        "Retained_local_function_rejects_a_different_capture_cell")
$env:MERKLE_CHECKPOINT_CORE_ROOT = $core
$restartReceipts = [ordered]@{
    Direct_durable_event_survives_restart = "durable-event-checkpoint.receipt.json"
    Checkpointed_string_task_rebinds_from_the_bag = "protected-checkpoint-ordinary-text.receipt.json"
    Protected_parameters_restore_result_and_timeout = "protected-checkpoint-handler-parameters.receipt.json"
    Completed_prefix_callables_restore_with_strict_slots = "protected-checkpoint-prefix-callables.receipt.json"
    Callables_created_after_resume_restore_with_strict_slots = "protected-checkpoint-suffix-callables.receipt.json"
    Retained_local_function_environment_restores_with_strict_slots = "protected-checkpoint-retained-local-function.receipt.json"
    Retained_nested_function_environments_restore_shared_cells = "protected-checkpoint-retained-nested-functions.receipt.json"
}
if ($StatementCoverage) {
    $statementCases = [ordered]@{
        Checkpointed_statement_scopes_restore_with_strict_slots = "statement-scopes"
        Checkpointed_deconstructing_vector_restores_with_strict_slots = "deconstructing-vector"
        Checkpointed_do_and_goto_restore_with_strict_slots = "do-goto"
        Checkpointed_checked_overflow_restores_with_strict_slots = "checked-overflow"
        Checkpointed_using_segments_restore_with_strict_slots = "using-segments"
        Checkpointed_fixed_segments_restore_with_strict_slots = "fixed-segments"
    }
    foreach ($test in $statementCases.Keys) {
        $restartReceipts[$test] = "protected-checkpoint-" + $statementCases[$test] + ".receipt.json"
    }
}
$proofTests += $restartReceipts.Keys
foreach ($test in $proofTests) {
    $restartReceipt = $null
    if ($restartReceipts.Contains($test)) {
        $restartReceipt = Join-Path (Split-Path -Parent $tests) ("TestResults\" + $restartReceipts[$test])
        if (Test-Path -LiteralPath $restartReceipt) { Remove-Item -LiteralPath $restartReceipt -Force }
    }
    $lines = & $corerun $tests --treenode-filter ("/*/*/NestedCheckpointTests/" + $test) --maximum-parallel-tests 1 2>&1 | Tee-Object -FilePath $log -Append
    $lines | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Checkpoint proof failed: $test ($LASTEXITCODE)" }
    $summary = $lines -join [Environment]::NewLine
    if ($summary -notmatch '(?im)^\s*total:\s*1\s*$' -or $summary -notmatch '(?im)^\s*skipped:\s*0\s*$') {
        throw "Expected one executed case for $test. Build the current Merkle test project before running this proof."
    }
    if ($restartReceipt) {
        if (-not (Test-Path -LiteralPath $restartReceipt)) { throw "Missing restart receipt for $test." }
        Copy-Item -LiteralPath $restartReceipt -Destination $output -Force
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
