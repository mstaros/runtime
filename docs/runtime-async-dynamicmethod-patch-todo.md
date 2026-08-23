# Runtime-Async DynamicMethod Patch TODO

Scope: complete and harden the `DynamicMethod` runtime-async patch on branch `runtime-async-dynamicmethod-118074`, including paired lifetime, diagnostics, portable-entrypoint cleanup, platform behavior, focused regressions, and implemented-design documentation.

## Work ledger

| Row | Done | Status | Todo | Comment | CommitHash |
| ---: | :--: | --- | --- | --- | --- |
| 1 | [x] | done | Correct paired descriptor destruction ownership so a collectible `LoaderAllocator` is released once per managed dynamic method. | Fixed pair ownership: one collectible `LoaderAllocator` reference/release per managed `DynamicMethod` pair. | c884f441c6f92035e83c9daccb72984883d125c4 |
| 2 | [x] | done | Make paired descriptor construction exception-safe, including descriptor checkout, signature/name ownership, weak handles, and reflection stub publication. | Rollback holders cover both descriptor checkouts, native buffers, long-weak handles, stub allocation, and collectible loader ownership until every throwing step succeeds. | transaction 03f0621f7234 |
| 3 | [x] | done | Clear portable-entrypoint pending state for both descriptors before either descriptor is recycled. | Finalization clears both pending flags under `FEATURE_PORTABLE_ENTRYPOINTS`. A CoreCLR WASM-only regression marks both pair descriptors pending, forces finalization and reuse, and rejects either descriptor if stale pending state reaches free-list checkout. | c884f441c6f92035e83c9daccb72984883d125c4; transaction db9a2eaf1a5e |
| 4 | [x] | done | Balance profiler and ETW unload reporting for every paired descriptor that can emit JIT/load notifications. | Finalization emits ETW and profiler unload notifications for both descriptors. Transaction `4eff83853cbe` verifies identical two-descriptor identity sets across profiler JIT start/finish/unload callbacks and EventPipe MethodLoad/MethodUnload events. | c884f441c6f92035e83c9daccb72984883d125c4; transaction 4eff83853cbe |
| 5 | [x] | done | Define and enforce non-CoreCLR behavior for `MethodImplAttributes.Async` on `DynamicMethod`; update runtime gating tests. | Mono rejects `Async` explicitly, uses its real bake sentinel, and runtime-async tests are gated on supported runtimes. | transaction a82c65757117 |
| 6 | [x] | done | Resolve the implementation-flags contract: either apply supported flags to execution policy or reject/normalize unsupported combinations instead of reporting ineffective values. | `DynamicMethod` reports `IL \| NoInlining` plus optional `Async`; every other nonzero implementation flag is rejected. | transaction a82c65757117 |
| 7 | [x] | done | Guard synthetic dynamic-method pair RID exhaustion or replace the unbounded 24-bit token-derived identity. | Async-pair MethodDef tokens are reserved under the table lock before descriptor checkout, capped at the 24-bit RID limit, and fail with `OverflowException` before wrap or collision. A process-isolated test lowers the ceiling to one and verifies ordinary DynamicMethods remain usable after exhaustion. | transaction 046238274084 |
| 8 | [x] | done | Add focused regression coverage for pair lifetime, forced finalization/reuse, failure cleanup, portable entrypoints, profiler lifecycle, runtime gating, and implementation-flag semantics. | Coverage now includes runtime gating/flags, consumer validation, forced pair finalization/reuse, second-descriptor checkout backout, all four late-construction unwind stages, portable-entrypoint pending-state cleanup, and profiler/EventPipe pair lifecycle. | transactions d21af63ba3b7, 60ea9962c6ef, af002a690ee1, 4eff83853cbe, db9a2eaf1a5e |
| 9 | [x] | done | Assess and document the memory cost of allocating async metadata for every dynamic descriptor chunk. | Uniform dynamic descriptor chunks reserve `AsyncMethodData` for ordinary and async descriptors: 24 bytes per descriptor on 64-bit targets and 12 bytes on 32-bit targets. The fixed cost and chunk-capacity formula are documented; a split pool or sidecar is deferred unless workload measurements show a material regression. | transaction db9a2eaf1a5e |

## Transaction sequence

1. Add this ledger.
2. Fix paired destruction and lifecycle notifications.
3. Fix paired construction exception safety.
4. Define runtime support boundaries and implementation-flag semantics.
5. Guard pair identity exhaustion and complete focused tests, design reconciliation, and memory assessment.

Rows may cite an integrated commit hash or the transaction that produced the change when the ledger is updated inside that transaction.
