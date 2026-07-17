# Runtime-Async IL Verification TODO

Scope: complete and harden static IL verification for the runtime-async contract, including focused `ILVerification` regressions, source-built verifier artifact ownership, metadata-backed materialization fidelity, and external compiler integration. This ledger is separate from `runtime-async-dynamicmethod-patch-todo.md`, whose DynamicMethod runtime implementation work is complete.

## Work ledger

| Row | Done | Status | Todo | Comment | CommitHash |
| ---: | :--: | --- | --- | --- | --- |
| 1 | [x] | done | Define the static IL verification contract and ownership boundary in the implemented DynamicMethod design. | Documents the unwrapped async `ret` rule, metadata-backed surrogate requirement, runtime-versus-verifier test ownership, source-revision matching, materialization fidelity, and complete generated-method-graph coverage. | 3b0ddff5ce4 |
| 2 | [x] | done | Add focused `ILVerification` positive regressions for `Task`, `Task<T>`, `ValueTask`, and `ValueTask<T>` return shapes. | `RuntimeAsyncTests.il` covers all four valid unwrapped return shapes; the focused `ILMethodTester` suite validates them. | transaction 318a8e8aa8d4 |
| 3 | [x] | done | Add focused negative regressions for missing `Async`, unsupported async return signatures, missing return values, extra stack values, and incompatible inner return types. | Existing async-invalid cases cover signature and stack failures; four ordinary-method controls prove that removing `Async` restores the declared return-type rule. | transaction 318a8e8aa8d4 |
| 4 | [ ] | open | Confirm and document that the `ILVerify` CLI and `Microsoft.ILVerification` library build from the same verifier source in this fork. | The command-line and in-process paths must not diverge in runtime-async semantics. | |
| 5 | [ ] | open | Define source-built verifier artifact discovery and source-revision pinning for external consumers. | Consumers must not silently fall back to an unrelated global tool or public package build. | |
| 6 | [ ] | open | Prove that `PersistedAssemblyBuilder` preserves the `Async` implementation bit, signatures, locals, exception regions, and method bodies required by the verifier. | Cover both valid and deliberately invalid runtime-async methods. | |
| 7 | [ ] | open | Evaluate serialization of executable `MethodBuilder` assemblies as an optional consumer-side materialization path. | Adopt only if focused metadata and IL round-trip tests prove fidelity; otherwise use `PersistedAssemblyBuilder`. | |
| 8 | [ ] | open | Add an end-to-end MpsAgent compiler verification test using the source-built fork verifier. | Verify entry methods plus generated nested lambdas, local functions, iterator cores, cleanup methods, and other helper methods. | |
| 9 | [ ] | open | Define CI ownership, matching `Core_Root` reference resolution, `System.Private.CoreLib` system-module selection, diagnostics capture, and temporary-image cleanup. | Verification must be deterministic, parallel-safe, and actionable on failure. | |

## Transaction sequence

1. Land the static-verification design contract and this ledger.
2. Add direct `ILVerification` positive and negative regression coverage.
3. Establish source-built verifier artifact and reference-resolution conventions.
4. Prove metadata materialization fidelity for supported producer paths.
5. Add the MpsAgent end-to-end consumer verification gate and reconcile the design with implementation results.

Rows may cite an integrated commit hash or the transaction that produced the change when the ledger is updated inside that transaction.
