# Runtime-Async DynamicMethod Patch TODO

Scope: harden the `DynamicMethod` runtime-async patch on branch `runtime-async-dynamicmethod-118074` after the source audit of commit `9ddcfdd` and current branch state.

## Work ledger

| Row | Done | Status | Todo | Comment | CommitHash |
| ---: | :--: | --- | --- | --- | --- |
| 1 | [x] | done | Correct paired descriptor destruction ownership so a collectible `LoaderAllocator` is released once per managed dynamic method. | Fixed pair ownership: one collectible `LoaderAllocator` reference/release per managed `DynamicMethod` pair. | c884f441c6f92035e83c9daccb72984883d125c4 |
| 2 | [x] | done | Make paired descriptor construction exception-safe, including descriptor checkout, signature/name ownership, weak handles, and reflection stub publication. | Rollback holders now cover both descriptor checkouts, native buffers, long-weak handles, stub allocation, and collectible loader ownership until every throwing step succeeds. | transaction 03f0621f7234 |
| 3 | [x] | done | Clear portable-entrypoint pending state for both descriptors before either descriptor is recycled. | Finalization now clears pending state for both paired descriptors under `FEATURE_PORTABLE_ENTRYPOINTS`. | c884f441c6f92035e83c9daccb72984883d125c4 |
| 4 | [x] | done | Balance profiler and ETW unload reporting for every paired descriptor that can emit JIT/load notifications. | Finalization now emits ETW and profiler unload notifications for both descriptors. | c884f441c6f92035e83c9daccb72984883d125c4 |
| 5 | [x] | done | Define and enforce non-CoreCLR behavior for `MethodImplAttributes.Async` on `DynamicMethod`; update runtime gating tests. | Mono now rejects `Async` explicitly, uses its real bake sentinel, and runtime-async tests are gated on supported runtimes. | transaction a82c65757117 |
| 6 | [x] | done | Resolve the implementation-flags contract: either apply supported flags to execution policy or reject/normalize unsupported combinations instead of reporting ineffective values. | `DynamicMethod` now reports `IL \| NoInlining` plus optional `Async`; every other nonzero implementation flag is rejected. | transaction a82c65757117 |
| 7 | [ ] | open | Guard synthetic dynamic-method pair RID exhaustion or replace the unbounded 24-bit token-derived identity. | `m_NextMethodRid` is never reused and `TokenFromRid` does not validate overflow. |  |
| 8 | [ ] | active | Add focused regression coverage for pair lifetime, forced finalization/reuse, failure cleanup, portable entrypoints, profiler lifecycle, runtime gating, and implementation-flag semantics. | The repository-local SDK/hostfxr blocker is fixed. CoreCLR and Mono runtime-gating/implementation-flag tests are complete; canonical Core_Root promotion and consumer validation are active, while focused lifetime, failure-cleanup, portable-entrypoint, and profiler tests remain. | transaction d21af63ba3b7 |
| 9 | [ ] | open | Assess and document the memory cost of allocating async metadata for every dynamic descriptor chunk. | Non-blocking unless measurement shows material regression. |  |
## Transaction sequence

1. Add this ledger.
2. Fix paired destruction and lifecycle notifications.
3. Fix paired construction exception safety.
4. Define runtime support boundaries and implementation-flag semantics.
5. Guard pair identity exhaustion and complete remaining focused tests/documentation.

Each implementation transaction must update the applicable ledger rows with status and commit hash after integration.
