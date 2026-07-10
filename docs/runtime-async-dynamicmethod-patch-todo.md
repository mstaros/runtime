# Runtime-Async DynamicMethod Patch TODO

Scope: harden the `DynamicMethod` runtime-async patch on branch `runtime-async-dynamicmethod-118074` after the source audit of commit `9ddcfdd` and current branch state.

## Work ledger

| Row | Done | Status | Todo | Comment | CommitHash |
| ---: | :--: | --- | --- | --- | --- |
| 1 |  | open | Correct paired descriptor destruction ownership so a collectible `LoaderAllocator` is released once per managed dynamic method. | Creation currently adds one reference while pair destruction releases twice. |  |
| 2 |  | open | Make paired descriptor construction exception-safe, including descriptor checkout, signature/name ownership, weak handles, and reflection stub publication. | No checked-out descriptor or native allocation may leak when pair setup throws. |  |
| 3 |  | open | Clear portable-entrypoint pending state for both descriptors before either descriptor is recycled. | Applies when `FEATURE_PORTABLE_ENTRYPOINTS` is enabled. |  |
| 4 |  | open | Balance profiler and ETW unload reporting for every paired descriptor that can emit JIT/load notifications. | The async implementation currently receives JIT notifications but no unload notification. |  |
| 5 |  | open | Define and enforce non-CoreCLR behavior for `MethodImplAttributes.Async` on `DynamicMethod`; update runtime gating tests. | Mono exposes the shared setter but its creation path ignores runtime-async flags and its bake sentinel is unused. |  |
| 6 |  | open | Resolve the implementation-flags contract: either apply supported flags to execution policy or reject/normalize unsupported combinations instead of reporting ineffective values. | Native creation currently consumes only `Async` and always forces `NoInlining`. |  |
| 7 |  | open | Guard synthetic dynamic-method pair RID exhaustion or replace the unbounded 24-bit token-derived identity. | `m_NextMethodRid` is never reused and `TokenFromRid` does not validate overflow. |  |
| 8 |  | open | Add focused regression coverage for pair lifetime, forced finalization/reuse, failure cleanup, portable entrypoints, profiler lifecycle, runtime gating, and implementation-flag semantics. | Keep tests with the transaction that changes the corresponding behavior. |  |
| 9 |  | open | Assess and document the memory cost of allocating async metadata for every dynamic descriptor chunk. | Non-blocking unless measurement shows material regression. |  |

## Transaction sequence

1. Add this ledger.
2. Fix paired destruction and lifecycle notifications.
3. Fix paired construction exception safety.
4. Define runtime support boundaries and implementation-flag semantics.
5. Guard pair identity exhaustion and complete remaining focused tests/documentation.

Each implementation transaction must update the applicable ledger rows with status and commit hash after integration.
