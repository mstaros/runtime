# Runtime-async checkpoint proof

This experiment resumes suspended runtime-async DynamicMethods in a fresh process
without executing their pre-await instructions. It remains an internal, opt-in,
Windows x64 exact-build proof.

## Run

```powershell
docs\experiments\runtime-async-checkpoint\run-proof.cmd -RuntimeRoot D:\repos\runtime-async-dynamicmethod
```

RuntimeRoot supplies an existing SDK and Core_Root dependency set. The runner builds
the candidate checkout and stages its coreclr, clrjit and IL CoreLib into a private
artifacts/experiments/runtime-async-checkpoint/Core_Root. Running MCP servers are not
replaced. Use -SkipBuild only after building this exact candidate.

To include the guarded Merkle/ScriptMcp proof, build ScriptMcp.Server.Tests in Release
and add -ScriptTests <absolute-test-assembly> -AspNetRoot <matching-framework-directory>.
The ScriptMcp companion must understand checkpoint record version 2.

## Restore contract (version 2)

- JIT checkpoint metadata is owned by its compilation attempt. ResetForJitRetry
  discards it. A successful compilation transfers ownership to PrepareCodeConfig;
  only the successful native-code publication CAS transfers it to the LCG resolver.
  Competing losers and failed attempts release their copies. A code-address check
  remains an additional check, not the lifetime guarantee. A separate publication
  completion flag is set by the winning thread even when no metadata was emitted.
  After code preparation, checkpoint readers wait for completion before interpreting
  a null schema; they cannot mistake the code/metadata publication gap for a nonowner.
- Capture matches the continuation's resume-info address against a method's table
  before looking up its state or enforcing the owner's return type. Unrelated
  methods with fewer states or valid non-Task return types are skipped.
- Each frame records the root IL offset of its logical await. Checkpoint builds do
  not merge different suspension points into one state. Ambiguous root offsets
  from multiple inlined awaits are rejected. Continuation-object reuse is enabled.
- The record includes a canonical identity for the method: IL, signature, local
  signature with native type handles normalized, EH clauses, implementation flags,
  and resolved token identities. Method/field tokens include module MVID and token;
  constructed generic contexts and referenced DynamicMethods are included.
  Full manifests are compared; names or slot shapes alone are insufficient.
- The schema includes the effective cached JIT configuration, VM compilation flags,
  and instruction-set flags. A change is rejected before resumption. This hash is
  a compatibility discriminator for trusted fixtures, not an authentication code.
- Reference slots carry their fresh JIT class and exactness in runtime metadata.
  Restore validates decoded references against that class/exactness and the
  captured concrete type identity, including null versus non-null, before writing
  a continuation. Persisted data contains type identities, never class handles.
- ContinueOnThreadPool is preserved. Direct-frame restoration honors its scheduling
  constraint under a custom synchronization context or task scheduler. The ordinary
  dispatcher may inline under the default context. Captured synchronization and
  task-scheduler contexts remain unsupported.
- The JIT/EE interface GUID is updated for the new metadata contract. Old checkpoint
  records are rejected; there is no version migration.

For checkpoint-enabled DynamicMethod roots, the JIT announces its cached proof mode
to the VM before importing IL. This policy also covers inlinees. The VM does not
fold current static readonly values or infer a reference's concrete class from a
current static value. Class-init queries, field flags and static-helper selection
retain runtime initialization checks instead of depending on the current initialized
bit. Ordinary compilation with the switch off keeps its existing optimizations.

The slot list still comes from the suspension's subLayout, exactly as the normal
JIT saves live locals and temporaries. Unsupported slot kinds fail explicitly.
Under the proof switch, native-handle CSE candidates are killed across awaits using
the existing async CSE kill mask. CSE within a segment remains available. This
improves supported coverage; rejecting unsupported native-int slots is the safety
boundary. The context-capture helper remains out of line for the bounded proof.

## Regression coverage

The runner uses DOTNET_JitAsyncCheckpointProof=2 for the emitted-method fixture.
This forces one real JIT backout/retry after metadata emission, and the snapshot
asserts that its metadata came from the subsequent compilation. Value 1 enables
metadata without forced retries; the default is 0.

The two nested methods use order-dependent arithmetic and must return 343 after
injecting 41. Swapped method indices/tables, changed method IL, changed schemas and
wrong value kinds must be rejected. Concurrent initial calls exercise publication.

Mode 3 forces a bounded gap after native-code installation and before metadata
publication. A second invocation captures while the winner is paused; the test
requires that the checkpoint reader actually observed and waited in the gap.
The barrier expires after ten seconds if the reader never arrives, causing the
regression to fail without leaving publication permanently incomplete. A separate
switch-off process verifies that absent metadata is rejected without waiting.
Both native and host capture lists put valid int/void decoys before the owner.

Two fresh-process pairs reverse class initialization: cold capture/hot restore and
hot capture/cold restore. Distinct values on the IL evaluation stack (17000, 310,
1731 and a reference) must survive as JIT temporaries and produce -506851. An
aggressively inlined static read exercises the root policy. Cold restoration must
not execute the static constructor or any pre-await instructions.

A separate method has three awaits. Its producer saves a nonzero state; a fresh
process restores it, reaches the third suspension, verifies that the actual restored
continuation object was reused, captures again, and finishes with 30061. Changed and
unchanged locals must survive. A one-state decoy precedes this method in the capture
list. A changed await state must be rejected.

A configured runtime-async task await also checks that ContinueOnThreadPool is set
by the JIT and honored when restoration starts under a custom task scheduler.

A separate process changes JIT configuration while retaining the binary fingerprint;
the runtime must reject the snapshot. The host's mismatched-runtime check and the
existing continuation/GC stress test (metadata off and on) also run.

The optional ScriptMcp fixture retains MessagePack live values, list aliases,
distinct equal-content lists, guard and pending-task bindings, and its two process
restarts. It adds wrong/null decoded reference, method identity, await identity and
JIT-environment rejection cases to its existing cancellation and corruption checks.
See ScriptMcp/docs/RuntimeCheckpointProof.md for the host flow and retained receipts.

## Limits

- Windows x64, exact runtime/application builds, tiering/OSR/R2R disabled. Exact DLL
  hashes and dependency manifests are enforced by the host; the runtime does not
  independently hash its own binaries.
- Class-initialization-dependent static folding and init-check elision are disabled
  for checkpoint compilation. This closes the identified cold/hot compilation gap.
  Ordinary JIT temporaries still have UNKNOWN_ILNUM and are compared by position:
  matching method/await identity, configuration, types and layout is not a general
  proof of temporary semantics across every process-dependent JIT optimization.
  Detected identity/layout/type mismatches are rejected; do not interpret this as
  a universal fail-closed guarantee. Ambiguous suspension identities (including
  duplicated awaits after optimization) and unsupported signatures are rejected.
- Merkle embeds artifact IDs in method names and guard IL. The host proof reproduces
  the same compilation order/IDs in each process and perturbs native allocations
  independently. Rebinding different IDs requires an explicit relocation contract;
  matching source text alone is insufficient.
- Full canonical manifests are retained per frame for this bounded proof. The
  MessagePack value codec is unchanged, but identity metadata increases snapshot size.
- The original chain supports at most 16 Task<int> frames and Int32/null/string
  values. The host supports one guarded script frame with explicitly encoded
  bindings and List<int> identity. No arbitrary graph or closure serialization.
- A serializable Int32 can still encode process-local meaning: identity or randomized
  string hashes, thread IDs and similar values are not portable merely because
  their bits fit the schema. The script/host must avoid persisting such values as
  portable data or recreate them through an explicit binding.
- The caller owns a quiescent pending wait. Capture is not stop-the-world and must
  not race completion. Active exceptions, native pointers and captured scheduling
  contexts are unsupported.
- Snapshots and generated code are trusted fixtures. The hooks are not an untrusted
  loader, and the codec remains responsible for valid binding semantics and aliases.
- No production scheduler, persistence service, crash-consistent external effects,
  activity delivery, artifact storage or code-version migration is implemented.

Receipts, snapshots and child logs are retained under
artifacts/experiments/runtime-async-checkpoint. Earlier version-1 results established
feasibility; version-2 validation must use freshly captured snapshots.
