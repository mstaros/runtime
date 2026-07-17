# Runtime-async DynamicMethod stack-trace / EH crash — fix plan

Status: root cause established with dump evidence (2026-07-16). This doc is the fix plan + task
list only. Not a full root-cause writeup.

## One-line cause

A suspended runtime-async LCG (DynamicMethod) is rooted only by its `Continuation`, which holds
native `ResumeInfo*` / `DiagnosticIP` and **no GC reference to the managed `DynamicResolver`**. If the
caller drops the delegate during suspension, the resolver is collected and `DynamicMethodDesc::TryDestroy`
(via DestroyScout) reclaims the paired `DynamicMethodDesc`s and their JIT code. Any later stack walk over
that frame — EH dispatch **or** `.StackTrace` — operates on a freed method identity.

## Exact failing function (evidence)

`StackFrameIterator` (coreclr `SfiInit` / `SfiInitWorker` → `StackFrameIterator::Init` / `NextRaw`,
`src/coreclr/vm/stackwalk.cpp`), reached from managed `System.Runtime.EH.DispatchEx` during exception
dispatch. Live dump (`suspend-collect` repro, Release corerun): the managed walk cannot produce any frame
above the async LCG throw; `!ip2md` on the throwing frame's IP returns "not in JIT code range" (code already
unmapped); the exception goes unhandled and the process exits 0xE0434352. The production report's AV in
`FormatSolutionEditorException` → `.StackTrace` is the **same** iterator/identity defect reached via
`DebugStackTrace::GetStackFramesFromException` instead of the EH path.

## Fix (option 3: repair the invariant + defensive guards)

Primary — root the resolver through the existing continuation keepalive machinery (do NOT invent a new field):

- EE→JIT: surface an "is-LCG" bit for the async transform (preferred: in `CORINFO_ASYNC_INFO`, already
  fetched during the transform; alternative: a `CorInfoOptions` bit). Internal JIT-EE contract only;
  bump `jiteeversionguid`; no public API.
- JIT: make `ContinuationNeedsKeepAlive` true for LCG methods and select the synthesized GC-ref keepalive
  slot (`KeepAliveOffset`); `CreateAllocContinuationCall` uses the `_METHOD` alloc helper with the method's
  own handle.
- CoreLib: `AllocContinuationMethod` populates the keepalive slot with
  `RuntimeMethodHandle.GetResolver(h) ?? (object)GetLoaderAllocator(h)` (resolver non-null at suspension
  because the executing physical frame GC-reports it; falls back to LA for non-LCG).
- Assert LCG and generic-context keepalive are mutually exclusive (DynamicMethods are non-generic statics),
  so one slot suffices.

Defense in depth (not a substitute for the invariant fix):

- `StackFrameIterator` / async-frame unwind: degrade gracefully when the frame's `EECodeInfo` / `MethodDesc`
  is unresolvable (skip/placeholder) instead of walking into freed memory.
- `AsyncHelpers_AddContinuationToExInternal` (`debugdebugger.cpp`): replace assert-only `IsValid()` with a
  runtime guard.
- `LCGMethodResolver::GetManagedResolver` (`dynamicmethod.cpp`): make DAC-independent null-handle safe.
- Stack-trace capture: skip / placeholder-append an LCG element whose managed resolver is gone (mirror of the
  existing `STEF_KEEPALIVE` path).

## Tests (two safety properties)

Regression harness (coreclr test or `eng/Test-RuntimeAsyncDynamicMethodStackTrace.ps1`):

- Suspended-code lifetime: suspend a valid runtime-async DynamicMethod; keep the task/continuation alive; drop
  delegate + DynamicMethod + compiler-host refs; run the full finalization ladder; resume and throw; assert a
  managed exception, not a crash. (Catches the resume-into-freed-code path.)
- Captured-stack lifetime: throw after resume; catch without touching the trace; full finalization ladder;
  materialize `ex.StackTrace`, `ex.ToString()`, `new StackTrace(ex, true)`; assert no crash.
- Retain variants: before-first-await (negative control), nested async DynamicMethod, forced-GC, 1000-iteration stress.
- Oracle rigor: weak-reference the **actual managed resolver** (reflection/internal), not just the delegate,
  to prove external roots were dropped. Determinism needs a multi-stage
  `GC.Collect()` / `WaitForPendingFinalizers()` ladder (the resolver handle is long-weak; one collect is
  insufficient) — establish the minimum experimentally.
- Run on Checked (deterministic invariant asserts) and Release under cdb `sxe av` (production-equivalent fault).

## Repro / debugging assets (already built, this machine)

- `%TEMP%\radm-sttrace-repro\` — console repro; variants: `sync-throw`, `await-throw`, `edi`, `nested`,
  `suspend-collect` (deterministically fatal pre-fix), `trace-uaf`, `stress`. Build with the repo-local
  `.dotnet\dotnet.exe`; `SetImplementationFlags(MethodImplAttributes.Async)` is called via reflection so it
  compiles off-tree and binds on the patched corerun.
- cdb from `Microsoft.WinDbg` store package (`cdbX64.exe`); driven via CSharpMpc `run_process`. Boot/post
  debugger script files live beside the repro. Fatal-moment dump: `sc_fatal.dmp`.

## Open questions (need decision before implementation)

- Interpreter parity: the interpreter has the parallel machinery
  (`InterpAsyncSuspendData.keepAliveOffset`, generic-context only today). PatchedCoreRun x64 is JIT-only, but
  earlier fork regressions touched WASM/interp. Include interpreter parity now, or defer with a comment?
- Checked `clr+libs` build for the deterministic-assert test leg is a manual step (too long for the sync tool).

## Task list

- [ ] Confirm the precise `stackwalk.cpp` async-frame unwind call (read-only) and finalize the one-paragraph writeup.
- [ ] Decide interpreter parity scope (open question above).
- [ ] EE→JIT is-LCG bit + `jiteeversionguid` bump.
- [ ] JIT: `ContinuationNeedsKeepAlive` + alloc-helper selection for LCG.
- [ ] CoreLib: `AllocContinuationMethod` populates keepalive with resolver (fallback LA).
- [ ] Defensive guards (iterator degrade, `AddContinuationToExInternal`, `GetManagedResolver`, capture skip).
- [ ] Regression harness: suspended-code + captured-stack properties + variants, resolver weak-ref oracle.
- [ ] Verify on Checked (asserts) and Release under cdb.
- [ ] Integration: MpsAgent Bridge capture tests green on rebuilt PatchedCoreRun; `run_workspace_script`
      throwing returns a tool error with the server alive (verified against a build WITHOUT the consumer-side
      `.StackTrace` mitigation).
