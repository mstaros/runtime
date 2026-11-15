# Runtime-Async IL Verification TODO

Scope: complete and harden static IL verification for the runtime-async contract, including focused `ILVerification` regressions, source-built verifier artifact ownership, metadata-backed materialization fidelity, and external compiler integration. This ledger is separate from `runtime-async-dynamicmethod-patch-todo.md`, whose DynamicMethod runtime implementation work is complete.

## Work ledger

| Row | Done | Status | Todo | Comment | CommitHash |
| ---: | :--: | --- | --- | --- | --- |
| 1 | [x] | done | Define the static IL verification contract and ownership boundary in the implemented DynamicMethod design. | Documents the unwrapped async `ret` rule, metadata-backed surrogate requirement, runtime-versus-verifier test ownership, source-revision matching, materialization fidelity, and complete generated-method-graph coverage. | 3b0ddff5ce4 |
| 2 | [x] | done | Add focused `ILVerification` positive regressions for `Task`, `Task<T>`, `ValueTask`, and `ValueTask<T>` return shapes. | `RuntimeAsyncTests.il` covers all four valid unwrapped return shapes; the focused `ILMethodTester` suite validates them. | transaction 318a8e8aa8d4 |
| 3 | [x] | done | Add focused negative regressions for missing `Async`, unsupported async return signatures, missing return values, extra stack values, and incompatible inner return types. | Existing async-invalid cases cover signature and stack failures; four ordinary-method controls prove that removing `Async` restores the declared return-type rule. | transaction 318a8e8aa8d4 |
| 4 | [x] | done | Confirm and document that the `ILVerify` CLI and `Microsoft.ILVerification` library build from the same verifier source in this fork. | Both projects import the same `ILVerification.projitems`; the artifact publisher resolves and asserts that import identity before either build. | transaction 9dda2fed8dc9 |
| 5 | [x] | done | Define source-built verifier artifact discovery and source-revision pinning for external consumers. | A source publisher emits deterministic CLI/library paths plus a manifest containing the exact Git revision, dirty state, shared-source hash, artifact hashes, and fail-closed consumer rules. | transaction 9dda2fed8dc9 |
| 6 | [x] | done | Prove that `PersistedAssemblyBuilder` preserves the `Async` implementation bit, signatures, locals, exception regions, and method bodies required by the verifier. | A focused in-memory PE round trip compares identical valid/missing-`Async` methods, asserts metadata and reflection fidelity, and verifies the expected positive/negative outcomes. | transaction d87d20524f59 |
| 7 | [x] | done | Evaluate serialization of executable `MethodBuilder` assemblies as an optional consumer-side materialization path. | Not adopted as the runtime-owned path: runnable `AssemblyBuilder` has no framework persistence API and would require an unproven third-party serializer. `PersistedAssemblyBuilder` is authoritative; consumers may qualify another serializer only with the same fidelity matrix. | transaction d87d20524f59 |
| 8 | [x] | done | Add an end-to-end MpsAgent compiler verification test using the source-built fork verifier. | Integrated MpsAgent commit `9c6da9e55e716b2acdc41bd77a72d3806f707b68` validates the source-built manifest before compilation, verifies a real runtime-async `Task<int>` artifact, and submits the complete persisted method-host graph—including entry methods, nested lambdas, local functions, iterator helpers, cleanup delegates, and other generated methods—to one ILVerify invocation. The focused service suite passed 9/9 and the combined stock/patched host-policy gate passed. | 9c6da9e55e716b2acdc41bd77a72d3806f707b68 |
| 9 | [x] | done | Define CI ownership, matching `Core_Root` reference resolution, `System.Private.CoreLib` system-module selection, diagnostics capture, and temporary-image cleanup. | CI publishes the verifier from clean runtime revision `b99f39469c7091e0809fdd0135be68edb17f0821`, rejects dirty or mismatched manifests, selects `System.Private.CoreLib`, and supplies the full matching explicit `Core_Root`. The fork CLI response-file contract keeps the Windows process command line bounded without reducing reference coverage. Evidence records the process host, CLI, manifest, revision, references, stdout/stderr, exit code, timeout, and truncation; unique per-compilation images are parallel-safe, retained on failure, and removed after success. The combined stock/patched gate and impacted suites passed with zero failures. | 9c6da9e55e716b2acdc41bd77a72d3806f707b68 |

## Transaction sequence

1. Land the static-verification design contract and this ledger.
2. Add direct `ILVerification` positive and negative regression coverage.
3. Establish source-built verifier artifact and reference-resolution conventions.
4. Prove metadata materialization fidelity for supported producer paths.
5. Add the MpsAgent end-to-end consumer verification gate and reconcile the design with implementation results.

Rows may cite an integrated commit hash or the transaction that produced the change when the ledger is updated inside that transaction.
