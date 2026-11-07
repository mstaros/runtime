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
The ScriptMcp companion must understand checkpoint record version 6.

## Restore contract (version 6)

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
Under the proof switch in runtime-async DynamicMethods, CSE candidates are killed
across awaits using the existing async CSE kill mask. Values are recomputed within
the resumed segment; CSE within a segment and general inlining remain enabled.
Forward substitution, SSA copy propagation and local copy-assertion propagation
preserve IL variable and promoted-field storage identities. These conservative
optimization restrictions are part of the checkpoint experiment, not a claim
that arbitrary JIT temporaries have stable identities. Unsupported slot kinds
and unidentified slots still fail the strict host policy. The context-capture
helper remains out of line for the bounded proof.

## Controlled host slot contract

Version 3 adds a slot origin and parent-field byte offset. Direct IL variables
have origin 1. Promoted fields have origin 2 and the original IL variable number;
the offset identifies the field rather than its position in the saved slot list.
Origins 3 and 4 identify saved execution/synchronization-context operands of the
await. Unknown JIT temporaries retain origin 0 and are not silently reclassified
by their runtime value or type. Separate continuation context storage remains
identified by the frame flags and the host context binding.

Version 4 adds kind 3 with origins 5 and 6: the hidden generic context of shared
generic code, naming the instantiated method or the instantiated class. In checkpoint
mode an async body keeps that context, or a generic receiver, live at every suspension.
Instantiations of shared code share resume addresses, so capture matches a continuation
by resume address and by its saved context, and rejects a continuation that several
supplied methods own. The context is never persisted: restore writes the recreated
instantiation's handle and the keep-alive object the continuation allocation helper
would store. A shared slot that reports a canonical (__Canon) class is checked against
its declared type from the IL signature, substituted with the closed instantiation;
an unidentified shared slot is rejected. A generic method's identity includes its
type arguments. The JIT/EE interface GUID changed with this contract.

Version 5 records what each await produces. Result kind 1 remains a plain Int32 await
and kind 3 a void await; kind 2 is any other awaited type, which the JIT reads from
the await call's signature (its primitive kind, or its class with enums unnormalized)
and the VM returns to CoreLib as a type. Shared generic code may report __Canon.
Awaiting a non-async Task-returning call (`await source.Task`) calls its async
version, which wraps the returned task in a transparent await. When the JIT inlines
that version, and the wrapper with it, the await that suspends is the call site's
own: in checkpoint mode it takes the call site's IL position, not the statement
locations of the inline contexts in between, which optimized code does not keep.
Several awaits inlined from one call site are ambiguous and rejected.
Restore checks that each caller awaits exactly what its callee returns (Task<X> and
ValueTask<X> give X, Task and ValueTask give void; a __Canon awaited type matches by
shape) and that the task's type is the outermost frame's. Frames may return Task,
Task<X>, ValueTask or ValueTask<X>. A suspended ValueTask<X> method's ValueTask wraps
its runtime-async task, so a node whose outermost frame returns ValueTask<X> is
captured from and restored as that task, which the host wraps. Awaiting a ValueTask
suspends behind its task's or its IValueTaskSource's continuation, which capture skips
as it skips a task's. The outcome hook seeds the first frame with a completed task: a
result must be a Task<X> of exactly the type that frame awaits, and a shared generic
first frame awaiting a type parameter is refused because that type is not known
exactly. A fault or cancellation is thrown
at the restored await by the ordinary dispatcher, which routes it to the nearest
frame with a handler. The Int32 hooks keep their behavior, including no injected
result for a void awaiter suspension. The JIT/EE interface GUID changed with this
contract. A frame suspended in a custom awaiter ([Unsafe]AwaitAwaiter) heads its own
chain; restored, it resumes directly and reads its result from the awaiter the host
restored. ValueTask frames and custom awaiters changed neither the schema nor the GUID.

Version 6 restores live values. Each slot record carries its size, and the JIT
reports kind 4 for a value without GC references (a primitive, a struct, a vector)
and kind 5 for a struct with GC references, with the struct's type. A value is
admitted by its variable's declared type: bool, char, the 8- to 64-bit integers,
float, double, enums over them, and structs whose fields all qualify or hold
references (decimal, DateTime, Guid, Nullable<T>, KeyValuePair<TKey, TValue> and the
like). Native-sized integers and pointers are refused, since they may be handles, and
so are ref structs. The declared size must equal the slot's size, and a struct slot's
type must equal the declared type. A promoted field is admitted only when its variable
is admitted whole, so admission does not depend on whether the JIT promotes. Capture
boxes a value as its type (an empty Nullable<T> as null) for the host to bind; restore
requires a box of exactly that type and copies it, with write barriers when it holds
references. A promoted variable whose type holds references reaches the host whole as
well: capture checks each saved field's offset and kind against where the type keeps
references and assembles the fields into one box, leaving fields the JIT did not save
at their defaults, and restore writes the saved fields back from the box. The host
thus decides on the struct's type whatever the JIT promotes; which references and
struct types it binds is its own policy.
CancellationToken stays a binding: saved whole or with its single field promoted, it
reaches the host as a CancellationToken, and the host supplies the token the restored
frame observes. A JIT temporary has no variable to restore a value as, so a value slot
without an identified variable is refused. In checkpoint mode the JIT skips physical
promotion in async methods, whose replacement locals would be saved as unidentified
temporaries. It also stores zero to a whole local that is not promoted before copying
a promoted struct into it field by field: liveness does not count the field stores as
definitions, so without the zero store an await before the copy would save the local's
stale contents, which for a call's argument temporary is a slot without a variable.
Only metadata frames take values; script frames keep Int32 and reference slots. The
JIT/EE interface GUID changed with version 6; structs with references and the zero
store changed neither the schema nor the GUID.

The host capture/restore hooks accept an optional layout validator. Capture invokes
it before encoding any live object; restore invokes it before decoding or installing
values. The validator receives a copy of the schema. A controlled host must require
an explicit allowed set of IL variables/fields and context roles, reject every
unknown slot, and validate reference bindings separately. Existing exploratory
fixtures may omit the validator; their success does not establish this stricter
contract. Quiescence and ownership are separate host obligations, not consequences
of correct slot metadata.

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

## External DLL experiment (mode 4)

The Merkle integration fixture also covers a script calling methods defined in a
separately emitted C# DLL. Build the existing
MpsAgent.Merkle.Compiler.Bridge.Integration.Tests project in Release, then run:

```powershell
docs\experiments\runtime-async-checkpoint\run-ordinary-proof.cmd -RuntimeRoot D:\repos\runtime-async-dynamicmethod -ScriptTests <absolute-integration-test-assembly>
```

Mode 4 extends checkpoint layout emission and the conservative IL-local policy to
metadata runtime-async methods. Modes 1 through 3 retain their DynamicMethod scope.
The extended internal host hooks accept MethodInfo arrays and multiple frames per
task. Metadata methods must be closed; generic methods and methods of generic types
are admitted as closed instantiations. Fresh lookup creates and JITs a missing
metadata async body without invoking the method; an instantiation of shared code is
resolved to its shared body.

Collectible methods and instantiations are admitted. Capture attributes a frame by
its code address, so a frame names the load that owns its code, and a snapshot names
methods and types by identity only: restore binds the methods the host recreates in
new contexts. A suspended frame keeps its context alive through its continuation's
type, allocated in the method's loader allocator, or through the generic-context
keep-alive; restore allocates the destination's type and writes the destination's
keep-alive. Restore refuses a method whose code, or a type of whose instantiation,
belongs to a context that is already unloading: the host binds only through
instances it holds, so such a method is a stale binding. Holding them is the host's
lease; the check only detects a binding that is already stale.

A second fixture runs two reference-type instantiations of one shared generic method
in one chain, each with live values of both type parameters, through a fresh-process
restore. Capture with one instantiation missing and restore with swapped
instantiations are rejected. The collectible fixtures restore the non-generic chain
and three generic shapes (collectible code over core-library types, collectible code
over a type of another collectible context, default-context code over a collectible
type) into fresh collectible contexts. They check that the restored chains alone keep
their contexts alive while suspended and that the contexts are collected after the
runs complete.

A result-types fixture chains a frame that awaits a long and returns a string, a
frame that awaits that string and returns nothing, and a frame that awaits that,
returns an int and handles InvalidOperationException. A fresh process restores it
three times: with a value, with a fault the handler catches, and with a cancellation
that cancels the DLL frames' task and then the script's. An outcome of another type
and frames reordered so that a caller no longer awaits what its callee returns are
refused before any frame runs. A generic variant runs two frames of shared code with
String results; restore matches a frame's __Canon await to its callee's String by
shape, and refuses a shared first frame that awaits a type parameter and a callee whose
Int32 lacks that shape.

A value-slots fixture keeps primitives, enums, decimal, DateTime, Guid, a nullable, a
padded struct, a nested struct and a struct parameter live in two frames, promoted and
whole, together with a CancellationToken. A fresh process restores them and binds the
token to one the restoring host owns; each frame describes its values after the
restore exactly as before the await, and no slot without a variable is saved. A live
native-sized integer is refused at capture, and a decoded value of another type at
restore.

A reference-structs fixture keeps KeyValuePair<string, int>, a (string, long) tuple, a
nullable KeyValuePair and structs that mix strings, a padded struct and a
StringBuilder live in two frames under a script, whole and promoted. One builder is
shared by both frames and several of the structs, and a promoted struct is passed by
value to a call after the await. A fresh process restores them with the builder as one
object. A promoted struct copied after the await into the first field of a larger
local leaves the local's other field, set before the await, intact. A live
List<int>.Enumerator is refused by the host, which cannot restore its list's private
version; a live struct holding a native-sized integer is refused at capture, and a box
of another type at restore.

A value-task fixture chains, under a script, a frame that returns ValueTask<long> and
awaits a custom awaitable, a frame that returns ValueTask and awaits it, and a frame
that returns Task<int>. The awaiter is a struct holding a reference to a wait object;
the host binds the wait and restores the awaiter over a completed one, from which the
first frame reads its result. The fixture also checks timeout and cancellation and counts registrations across restore. A second node is one frame that returns ValueTask<long>
and awaits an IValueTaskSource: a fresh process restores it with a completed task, and
the host wraps the node's task in a ValueTask. Restoring that node as an Int32 result
is refused.

Ordinary-method metadata is copied into its loader allocator before native-code
publication and indexed by MethodDesc plus code address. Lookup selects the entry
for installed code, so a losing compilation cannot provide the winner's layout.
These proof-only entries remain until allocator teardown. DynamicMethods retain
their existing winner-owned resolver publication. Metadata method identity uses
module MVID, token, declaring type and signature, and the fixture separately pins
the complete DLL bytes by SHA-256. It reloads the saved DLL; it does not rebuild
the DLL in restoring processes or persist native code and pointers.

The external fixture has two nested DLL async methods, a live integer local, one
shared receiver and receiver-bound call/validation delegates. The restart test
checks two rejected values with fresh snapshots, followed by acceptance and the
script's continuation. It checks that constructors and pre-await prefixes are not
replayed. The DLL is compiled with the C# runtime-async feature; a conventional
compiler-state-machine DLL is a required negative control. The codec admits only
this known object/delegate/task graph. DLL bodies are not given Merkle execution
guards by ordinary C# compilation; only the script guard is restored.

The runner re-executes the original DynamicMethod proof, then stages the candidate
under RuntimeRoot/artifacts/experiments/runtime-async-ordinary-checkpoint. The
external receipt is published only after the process-isolated assertions pass.
This extends the experiment, not the production durability API or DLL admission
policy. Arbitrary existing DLLs, arbitrary serialization and general optimized-JIT
portability remain unproved.

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
- Each task node admits at most 16 frames. The managed schema gate permits 1,024
  slots and 1 MiB of continuation data; the native lookup permits 65,536 states
  and 1 MiB of layout metadata. Schema 6 uses 11 header integers and seven per
  slot. The standalone file writer and reader share bounds of 1–16 frames,
  11–7,179 schema integers and 0–1,024 reference types/values per frame, plus 1 MiB
  per file. Invalid counts are rejected before staging a write; an oversized
  write cannot replace the published checkpoint. The reader checks the length
  of the stream it opened before decoding, avoiding a separate path-length check.
  The runner's `format-limits` child round-trips the minimum and exact maximum
  counts and an exactly 1 MiB file, then requires 18 below-minimum/one-over
  rejections from the reader and writer. Rejected writes must preserve the
  published bytes. These synthetic file-codec cases do not prove native layout,
  resume-state, identity-graph or host allocation/graph boundary behavior.
  File format v3 preserves string reference identity and frame positions. Recapture
  older standalone files with the same candidate build used for restore.
- Frame count, position, method, suspension state and effective thread-pool
  dispatch are recorded together. Slot policies for every frame in one runtime
  task run before its first value codec callback. The host owns graph-wide
  prevalidation, authentication of task edges, generation and exact retained
  method/load-context bindings. The structural validation hook can preflight a
  task before dispatch. Injecting a generic leaf outcome requires an exact
  result type. Separate hooks restore a nongeneric Task or ValueTask root.
  The fixture covers success, timeout and cancellation for both.
- The original chain supports at most 16 Task<int> frames and Int32/null/string
  values. The original DynamicMethod host hook supports one guarded script frame
  with explicitly encoded bindings and List<int> identity. No arbitrary graph or closure serialization.
- Host chains admit Task, Task<X>, ValueTask and ValueTask<X> frames with any awaited
  result type that the runtime can validate, suspended at a task, a value task or a custom awaiter. A script frame
  awaiting a ValueTask or a custom awaiter is not proven, nor is ConfigureAwait;
  ConfigureAwaitOptions.SuppressThrowing suspends in a CoreLib frame that no host
  supplies and is refused. Live slots are Int32, references,
  values, structs with references and CancellationToken. A value without references
  is restored from its bytes, which are portable only between identical builds, and
  the host decides which references and struct types it binds. A value slot without a
  variable is refused: the zero store removes the argument temporaries seen so far,
  and any other JIT temporary that holds a value fails the capture.
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
feasibility; each new schema version's validation must use freshly captured snapshots.
