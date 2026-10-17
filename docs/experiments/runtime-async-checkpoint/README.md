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

## Deliberate limits

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

The next step after this proof is a host-owned durable await boundary and snapshot
contract. General graph serialization and the Merkle captured-data/method-binding split
remain separate prerequisites for real scripts.
