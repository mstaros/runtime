param(
    [Parameter(Mandatory=$true)][string]$RuntimeRoot,
    [switch]$SkipBuild,
    [string]$ScriptTests,
    [string]$AspNetRoot,
    [switch]$Disassemble
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$runtime = (Resolve-Path -LiteralPath $RuntimeRoot).Path
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..\..\..")).Path
$output = [IO.Directory]::CreateDirectory((Join-Path $repo "artifacts\experiments\runtime-async-checkpoint")).FullName
Remove-Item -LiteralPath (Join-Path $output "receipt.json") -Force -ErrorAction SilentlyContinue
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot "build-runtime.cmd") $runtime
    if ($LASTEXITCODE -ne 0) { throw "Candidate runtime build failed: $LASTEXITCODE" }
}
$baseline = Join-Path $runtime "artifacts\tests\coreclr\windows.x64.Release\Tests\Core_Root"
$candidate = Join-Path $repo "artifacts\bin\coreclr\windows.x64.Release"
$coreRootPath = Join-Path $output "Core_Root"
if (Test-Path -LiteralPath $coreRootPath) { Remove-Item -LiteralPath $coreRootPath -Recurse -Force }
$coreRoot = [IO.Directory]::CreateDirectory($coreRootPath).FullName
foreach ($name in @("coreclr.dll", "clrjit.dll", "IL\System.Private.CoreLib.dll")) {
    if (-not (Test-Path -LiteralPath (Join-Path $candidate $name))) { throw "Missing candidate: $name" }
}
# Private staging only: never replace the runtime used by MCP servers.
Copy-Item -Path (Join-Path $baseline "*") -Destination $coreRoot -Recurse -Force
foreach ($name in @("coreclr.dll", "clrjit.dll")) {
    Copy-Item -LiteralPath (Join-Path $candidate $name) -Destination (Join-Path $coreRoot $name) -Force
}
Copy-Item -LiteralPath (Join-Path $candidate "IL\System.Private.CoreLib.dll") -Destination (Join-Path $coreRoot "System.Private.CoreLib.dll") -Force
if (Test-Path -LiteralPath (Join-Path $candidate "corerun.exe")) {
    Copy-Item -LiteralPath (Join-Path $candidate "corerun.exe") -Destination (Join-Path $coreRoot "corerun.exe") -Force
}
$dotnet = Join-Path $runtime ".dotnet\dotnet.exe"
$sdk = (Get-Content -LiteralPath (Join-Path $runtime "global.json") -Raw | ConvertFrom-Json).sdk.version
$csc = Join-Path $runtime ".dotnet\sdk\$sdk\Roslyn\bincore\csc.dll"
$worker = Join-Path $output "CheckpointProof.dll"
$compile = @("/nostdlib+", "/target:exe", "/langversion:preview", "/nullable:enable", "/optimize+", ('/out:"' + $worker + '"'))
foreach ($name in @("System.Private.CoreLib", "System.Runtime", "System.Console", "System.Linq",
    "System.Reflection.Emit", "System.Reflection.Emit.Lightweight", "System.Reflection.Primitives",
    "System.Threading", "System.Threading.Tasks", "System.Collections", "System.Runtime.Extensions", "System.IO.FileSystem")) {
    $compile += '/reference:"' + (Join-Path $coreRoot ($name + ".dll")) + '"'
}
$compile += '"' + (Join-Path $PSScriptRoot "CheckpointProof.cs") + '"'
$rsp = Join-Path $output "compile.rsp"
[IO.File]::WriteAllLines($rsp, $compile)
& $dotnet exec $csc /noconfig ("@" + $rsp)
if ($LASTEXITCODE -ne 0) { throw "Proof compilation failed: $LASTEXITCODE" }

$binaries = @("corerun.exe", "coreclr.dll", "clrjit.dll", "System.Private.CoreLib.dll") | ForEach-Object {
    [ordered]@{ name = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $coreRoot $_) -Algorithm SHA256).Hash }
}
$fingerprint = ($binaries | ForEach-Object { $_.name + ":" + $_.sha256 }) -join ";"
$env:CHECKPOINT_PROOF_RUNTIME_SHA256 = $fingerprint
$env:CHECKPOINT_PROOF_CORE_ROOT = $coreRoot
$env:CORE_ROOT = $coreRoot
$env:CORE_LIBRARIES = $coreRoot
# Value 2 forces one real JIT backout/retry after metadata emission.
$env:DOTNET_JitAsyncCheckpointProof = "2"
$env:DOTNET_JitNoInline = "0"
$env:COMPlus_JitNoInline = "0"
$env:DOTNET_TieredCompilation = "0"
$env:DOTNET_TC_OnStackReplacement = "0"
$env:DOTNET_ReadyToRun = "0"
$env:COMPlus_TieredCompilation = "0"
$env:COMPlus_ReadyToRun = "0"
$corerun = Join-Path $coreRoot "corerun.exe"
$snapshot = Join-Path $output "checkpoint.bin"
Remove-Item -LiteralPath $snapshot -Force -ErrorAction SilentlyContinue
$producerOut = Join-Path $output "producer.stdout.log"
$producerErr = Join-Path $output "producer.stderr.log"
$producer = Start-Process -FilePath $corerun -ArgumentList @(('"' + $worker + '"'), "produce", ('"' + $snapshot + '"')) -PassThru -NoNewWindow -RedirectStandardOutput $producerOut -RedirectStandardError $producerErr
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        if ($producer.HasExited) {
            Get-Content -LiteralPath $producerOut
            Get-Content -LiteralPath $producerErr
            throw "Producer exited before its checkpoint: $($producer.ExitCode)"
        }
        if ([DateTime]::UtcNow -gt $deadline) { throw "Producer checkpoint timed out." }
        Start-Sleep -Milliseconds 100
    } while (-not (Test-Path -LiteralPath $snapshot) -or -not ((Get-Content -LiteralPath $producerOut -Raw) -match "READY producer="))
    $producer.Kill()
    $producer.WaitForExit()
} finally {
    if (-not $producer.HasExited) { $producer.Kill(); $producer.WaitForExit() }
}
Get-Content -LiteralPath $producerOut
Get-Content -LiteralPath $producerErr
Write-Host "Producer PID $($producer.Id) was killed; starting a fresh process."
$restoreLog = Join-Path $output "restore.log"
& $corerun $worker restore $snapshot 2>&1 | Tee-Object -FilePath $restoreLog
if ($LASTEXITCODE -ne 0) { throw "Fresh-process restore failed: $LASTEXITCODE" }

# Reverse class-initialization histories in separate processes, retaining the same
# method IL/config and distinct evaluation-stack temporaries.
foreach ($direction in @(@("cold", "hot"), @("hot", "cold"))) {
    $staticSnapshot = Join-Path $output ("statics-" + $direction[0] + ".bin")
    & $corerun $worker ("statics-produce-" + $direction[0]) $staticSnapshot
    if ($LASTEXITCODE -ne 0) { throw "Class-state capture failed." }
    & $corerun $worker ("statics-restore-" + $direction[1]) $staticSnapshot
    if ($LASTEXITCODE -ne 0) { throw "Class-state restore failed." }
}
# Value 3 adds a bounded publication barrier; the reader must observe the gap.
$env:DOTNET_JitAsyncCheckpointProof = "3"
& $corerun $worker publication $snapshot
if ($LASTEXITCODE -ne 0) { throw "Forced publication-gap regression failed." }
$env:DOTNET_JitAsyncCheckpointProof = "0"
& $corerun $worker disabled $snapshot
if ($LASTEXITCODE -ne 0) { throw "Metadata-disabled capture failed to return." }
$env:DOTNET_JitAsyncCheckpointProof = "2"

# The runtime itself must reject an effective-JIT-config mismatch, even when the host
# binary fingerprint is unchanged.
$savedDisasm = [Environment]::GetEnvironmentVariable("DOTNET_JitDisasm")
$env:DOTNET_JitDisasm = "__checkpoint_configuration_probe_no_matching_method__"
& $corerun $worker config $snapshot
if ($LASTEXITCODE -ne 0) { throw "Changed JIT configuration was not rejected." }
$env:DOTNET_JitDisasm = $savedDisasm

# Host rejects mismatched runtime identity before invoking the native restoration hook.
$env:CHECKPOINT_PROOF_RUNTIME_SHA256 = "intentionally-mismatched"
$negativeOut = Join-Path $output "wrong-runtime.stdout.log"
$negativeErr = Join-Path $output "wrong-runtime.stderr.log"
$negative = Start-Process -FilePath $corerun -ArgumentList @(('"' + $worker + '"'), "restore", ('"' + $snapshot + '"')) -PassThru -Wait -NoNewWindow -RedirectStandardOutput $negativeOut -RedirectStandardError $negativeErr
if ($negative.ExitCode -eq 0 -or -not ((Get-Content -LiteralPath $negativeErr -Raw).Contains("fingerprint mismatch"))) {
    throw "Mismatched runtime fingerprint was not rejected correctly."
}
$env:CHECKPOINT_PROOF_RUNTIME_SHA256 = $fingerprint
# Existing process-isolated continuation/GC regression, with metadata off and on.
$regression = Join-Path $output "DynamicMethodContinuationConcurrency.dll"
$regressionArgs = @($compile[0..($compile.Count - 2)] | Where-Object { $_ -notlike '/out:*' })
$regressionArgs += '/out:"' + $regression + '"'
$regressionArgs += '"' + (Join-Path $repo "src\tests\async\reflection\dynamicmethod-continuation-concurrency.cs") + '"'
$regressionRsp = Join-Path $output "regression.rsp"
[IO.File]::WriteAllLines($regressionRsp, $regressionArgs)
& $dotnet exec $csc /noconfig ("@" + $regressionRsp)
if ($LASTEXITCODE -ne 0) { throw "Regression compilation failed: $LASTEXITCODE" }
foreach ($enabled in @("0", "1")) {
    $env:DOTNET_JitAsyncCheckpointProof = $enabled
    & $corerun $regression
    if ($LASTEXITCODE -ne 100) { throw "Continuation/GC regression failed with metadata=$enabled : $LASTEXITCODE" }
}

$scriptReceipt = $null
if ($ScriptTests) {
    if (-not $AspNetRoot) { throw "-AspNetRoot is required with -ScriptTests." }
    $scriptAssembly = (Resolve-Path -LiteralPath $ScriptTests).Path
    $aspNet = (Resolve-Path -LiteralPath $AspNetRoot).Path
    # Add the host's web framework dependencies without replacing the candidate runtime.
    foreach ($file in Get-ChildItem -LiteralPath $aspNet -Filter "*.dll" -File) {
        $destination = Join-Path $coreRoot $file.Name
        if (-not (Test-Path -LiteralPath $destination)) { Copy-Item -LiteralPath $file.FullName -Destination $destination }
    }
    # corerun does not resolve native RID assets from the application deps.json.
    $nativeAssets = Join-Path ([IO.Path]::GetDirectoryName($scriptAssembly)) "runtimes\win-x64\native"
    if (Test-Path -LiteralPath $nativeAssets) {
        foreach ($file in Get-ChildItem -LiteralPath $nativeAssets -Filter "*.dll" -File) {
            $destination = Join-Path $coreRoot $file.Name
            if (Test-Path -LiteralPath $destination) { throw "Native asset collides with staged runtime: $($file.Name)" }
            Copy-Item -LiteralPath $file.FullName -Destination $destination
        }
    }
    $scriptArtifacts = [IO.Directory]::CreateDirectory((Join-Path $output "scriptmcp")).FullName
    Remove-Item -LiteralPath (Join-Path $scriptArtifacts "receipt.json") -Force -ErrorAction SilentlyContinue
    # corerun must expose application dependencies through its trusted assembly list.
    $env:CORE_LIBRARIES = [IO.Path]::GetDirectoryName($scriptAssembly)
    $env:APP_ASSEMBLIES = "PROPERTY"
    $env:SCRIPTMCP_CHECKPOINT_CORE_ROOT = $coreRoot
    $env:SCRIPTMCP_CHECKPOINT_ARTIFACTS = $scriptArtifacts
    $env:DOTNET_JitAsyncCheckpointProof = "1"
    if ($Disassemble) {
        $env:DOTNET_JitDisasm = "Merkle_A*"
        $env:DOTNET_JitStdOutFile = Join-Path $output "script-jit.log"
    }
    & $corerun $scriptAssembly --treenode-filter "/*/*/ScriptExecutionTests/Runtime_checkpoint_survives_host_restart" --maximum-parallel-tests 1
    if ($LASTEXITCODE -ne 0) { throw "ScriptMcp checkpoint proof failed: $LASTEXITCODE" }
    $scriptReceipt = [IO.File]::ReadAllText((Join-Path $scriptArtifacts "receipt.json")) | ConvertFrom-Json
    if (-not $scriptReceipt.succeeded) { throw "ScriptMcp checkpoint receipt is not successful." }
    Write-Host ("ScriptMcp checkpoint receipt: " + ($scriptReceipt | ConvertTo-Json -Depth 4 -Compress))
}
$receipt = [ordered]@{
    scriptMcp = $scriptReceipt
    succeeded = $true; producerPid = $producer.Id; producerKilled = $true
    snapshotBytes = (Get-Item -LiteralPath $snapshot).Length
    expectedResult = 343; expectedReuseResult = 30061; expectedPreAwaitCallsOnRestore = 0
    runtimeSettings = [ordered]@{
        JitAsyncCheckpointProof = "2 (forced retry)"; JitNoInline = "0"; TieredCompilation = "0"
        TC_OnStackReplacement = "0"; ReadyToRun = "0"
    }
    runtime = $binaries; restoreOutput = [IO.File]::ReadAllText($restoreLog)
    continuationGcRegression = @("metadata disabled: passed", "metadata enabled: passed")
    negativeCases = @("unsupported live object", "changed schema", "changed value type", "swapped method index", "swapped method table", "changed method IL", "swapped await state", "effective JIT configuration", "runtime fingerprint")
    restoredNonzeroStateResuspended = $true; restoredContinuationReused = $true; jitRetryVerified = $true
    continueOnThreadPoolVerified = $true
    classInitializationDirections = @("cold to hot", "hot to cold")
    forcedPublicationGapVerified = $true; nonTaskDecoysSkipped = $true; disabledCaptureReturned = $true
}
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output "receipt.json") -Encoding UTF8
Write-Host "CHECKPOINT PROOF PASSED. Receipt: $(Join-Path $output 'receipt.json')"
