# Runtime-async checkpoint proof

This experiment tests one narrow question: can a runtime-async DynamicMethod continuation
resume in another process without reexecuting its pre-await instructions?

Run from a Windows x64 checkout:

```powershell
docs\experiments\runtime-async-checkpoint\run-proof.cmd -RuntimeRoot D:\repos\runtime-async-dynamicmethod
```

RuntimeRoot supplies an existing SDK and Core_Root dependency set. The script builds the
candidate checkout and stages its coreclr, clrjit and IL CoreLib into a private
artifacts/experiments/runtime-async-checkpoint/Core_Root. It does not replace the source
Core_Root. Use -SkipBuild only for diagnostic reruns after a successful candidate build.

## Success conditions

1. Two nested emitted runtime-async methods execute their prefixes and suspend on a
   controlled IValueTaskSource<int> that never completes.
2. Capture saves both frames' integer and string values, method-table indices, suspension
   states and pointer-free layout schemas to a flushed file.
3. The runner kills the producer process.
4. A different process creates unrelated DynamicMethods, recreates the program and JITs
   its async bodies without invoking their entry points.
5. Restore checks the schemas, rebuilds continuation objects and resolver keepalives,
   and uses the normal runtime dispatcher to inject 41. The final result must be 84.
6. Any prefix execution in the restoring process throws. Its prefix count must remain zero.
7. Unsupported live objects, modified schemas, modified value types and a different
   runtime fingerprint must be rejected.
8. The existing continuation/GC stress test passes with metadata disabled and enabled.

The runner writes receipt.json, process logs and checkpoint.bin under
artifacts/experiments/runtime-async-checkpoint. A passing receipt is evidence for this
bounded experiment, not a general checkpointing guarantee.

## Verified result

On 2026-10-17, Windows x64 Release: the producer was killed after writing a 596-byte
snapshot. A fresh process restored both frames, returned 84, and executed zero pre-await
prefix calls. All four rejection checks and the existing continuation/GC stress test
(with metadata disabled and enabled) passed. General inlining was enabled; only the
context-capture helper described below was kept out of line.

## Runtime changes

DOTNET_JitAsyncCheckpointProof=1 enables complete live-slot metadata for runtime-async
DynamicMethods. The JIT publishes it through the existing reportMetadata channel. Each
LCG resolver owns a copy tied to the native code version; a mismatched code address is
rejected before accessing its resume table. This does not change the resume-info ABI.

With the proof switch enabled, the JIT leaves its context-capture helper out of line in
DynamicMethods. Otherwise inlining introduces an extra Thread temporary that the existing
liveness analysis retains across suspension, despite the context locals being intended
for the synchronous path. The proof does not serialize or silently discard that object.
Normal inlining remains enabled elsewhere. General support needs explicit classification
of runtime bindings and a broader review of context/liveness behavior.

An internal QCall prepares the recreated async body and returns its current continuation
type, resume binding, resolver root and pointer-free schema. Internal CoreLib testing
hooks capture the suspended chain and rebuild it. Native addresses, tasks, delegates and
resolvers are never written into the snapshot.

The proof program is identified by its assembly MVID, fixed method-index mapping,
CoreLib MVID and exact runtime binary hashes. Real generated scripts need a retained
program artifact and a stronger program manifest; that is separate work.

## Original proof limits

- Windows x64, exact runtime and proof assembly, tiering/OSR/R2R disabled.
- A sequential chain of at most 16 runtime-async DynamicMethods returning Task<int>.
- Int32 and null/string live slots. Compiler-generated live slots are included and
  rejected if unsupported, rather than silently omitted.
- Strings round-trip by value; reference identity between saved slots is not preserved.
- One controlled, quiescent pending await whose result is an Int32.
- No captured execution/synchronization/scheduler contexts, exception state, native
  pointers, arbitrary object graphs, closures, fan-out or concurrent checkpointing.
- The caller owns suspension: the testing hook is not a stop-the-world mechanism and
  must not be raced with source completion.
- The saved input is a trusted proof fixture. This is not an untrusted snapshot loader.
- No scheduler, persistence service, activity delivery, crash-consistent external effects,
  version migration, ApiCatalog storage or Merkle closure changes are included.


## Guarded ScriptMcp host extension

The optional host experiment uses a real Merkle script through ScriptMcp's ScriptRunner
and CSharpScriptExecutionService. Build ScriptMcp.Server.Tests in Release, then run:

```powershell
docs\experiments\runtime-async-checkpoint\run-proof.cmd -RuntimeRoot D:\repos\runtime-async-dynamicmethod -ScriptTests <absolute-path-to-ScriptMcp.Server.Tests.dll> -AspNetRoot <absolute-path-to-matching-Microsoft.AspNetCore.App-directory>
```

The runner adds missing ASP.NET framework dependencies and the application's win-x64
native assets to a freshly staged private Core_Root, exposes application DLLs in
corerun's trusted assembly list, and
selects the explicit Runtime_checkpoint_survives_host_restart test. The live MCP servers
and their runtime directories are not changed.

The host hook supports one guarded Task<object> script frame suspended on a controlled
Task<int>. Its trusted codec encodes the live string, guard and pending-operation descriptors and replaces
the captured ExecutionContext with a host-context descriptor. On restore, a fresh
platform invocation recreates the guard and execution context. Guard aliases retain
identity. Merkle's source awaiter lowering suspends directly in the script frame at a
void helper call. Its optimized continuation retains the TaskAwaiter<int>'s task
reference. Capture encodes that reference as the pending-operation token;
restore supplies a completed task,
then the script's ordinary GetResult reads 41. No task object graph is persisted.
Unpromoted value types and configured/custom awaiter layouts remain outside this fixture.

The opt-in checkpoint build also excludes native handle constants from CSE in async
DynamicMethods. The initial host run exposed an Int32 method-table handle cached as
an Int64 JIT temporary across the await, because both the prefix and result box an
integer. Fresh code must rematerialize that handle, never restore the old address.
General inlining and other CSE candidates remain enabled. Unsupported slots retain
a diagnostic kind (100 plus the JIT type) and are rejected by the managed hook.
Inactive exception-handling slots are accepted; active saved exceptions and
scheduling contexts are rejected. The JIT's opt-in metadata now describes inactive EH
frames; OSR remains excluded. The original four-field capture/restore interface retains
its original restrictions; the host variant uses a separate five-field record.

The test writes the source, exact runtime/application fingerprint, continuation and
pending operation descriptor together, then returns a pending run ID through the normal
ScriptRunner result path. The parent kills the producer only after that return. Fresh
processes compile unrelated scripts before restoring, inject 41, and must return 84
without running the prefix. The saved integer and string must both be present. Separate
processes exercise guard cancellation and reject changed schema, unknown bindings and
a changed fingerprint. Guard branch accounting must reach zero after completion and
cancellation. The runner retains child logs and a receipt under the experiment's
scriptmcp artifact directory, and includes that receipt in its main receipt.json.

This extension is a trusted, exact-build test fixture. The internal hooks do not validate
arbitrary reference types for an untrusted loader. The codec rejects unknown objects;
there is no general graph serializer, closure support, resource adapter, second wait,
parallel branch, production submission API, scheduler or persistent artifact store.
The existing Merkle closure conversion work remains separate.

For a diagnostic rerun after building, add -SkipBuild -Disassemble to retain the
selected Merkle DynamicMethod disassembly as script-jit.log in the experiment artifacts.

### Verified host result

On 2026-10-17, Windows x64 Release, the complete runner passed in native operation
06dfa578ee2c1d1b1f88f1dd4a14cc6f. ScriptMcp producer 648572 returned pending and was
killed; process 625584 restored a 1,046-byte snapshot and returned 84 with zero
prefix calls. Guard cancellation, changed schema, unknown binding and fingerprint
rejections all passed in separate processes. The original two-frame proof and both
continuation/GC stress modes also passed. The script snapshot contains only the
guard/string/pending-task descriptors, live integer and host-context descriptor;
no native type handle or task object graph is persisted.
