# Async DynamicMethod versus collectible assembly: measurements

Measured 2026-09-15 on the existing patched Windows x64 Release Core_Root in `D:\repos\runtime-async-dynamicmethod`.

For this workload, one collectible assembly per delegate used **10.1 times the additional process private memory** of DynamicMethod. Emission and delegate creation took **35.0 times as long**. Batching 100 methods per assembly amortized the overhead and used slightly less private memory than DynamicMethod, while giving the methods a shared collection boundary.

## Retained footprint

1,000 generated async delegates per process; three fresh processes per case, with rotated case order. Values are medians of each process's delta from its own warmed baseline, measured after first execution and forced full GC while all delegates remain rooted. Parentheses give the observed minimum–maximum, not a confidence interval.

| Case | Additional private memory, MiB (range) | Additional virtual address space, MiB | Additional live managed memory, KiB |
| --- | ---: | ---: | ---: |
| DynamicMethod | 3.95 (3.92–4.04) | 2.25 | 1063.6 |
| RunAndCollect, 1 method/assembly | 40.03 (39.90–40.31) | 157.57 | 321.4 |
| RunAndCollect, 100 methods/assembly | 3.53 (3.45–3.63) | 7.50 | 66.2 |
| RunAndCollect, 1 method/assembly + reflected shape | 42.61 (42.33–42.71) | 157.62 | 321.4 |

Private memory includes native runtime allocations and managed heap commitment. Virtual address space is reservation, not committed RAM. The managed-only column misses most of the assembly overhead: a managed-allocation-only benchmark would give a misleading answer here. This experiment does not attribute every native byte to a specific allocator.

The shape case adds three instance fields and a constructor to the declaring type that the assembly already needs. Its generated body creates an instance and passes it to an ordinary external callee, which successfully reads and writes fields through standard reflection. It does not add an independent second type or retain shape instances.

## Creation and first execution

Times are medians for all 1,000 methods. “Emission” includes defining methods/types and creating delegates. “First execution” includes generated-code compilation and one completed await per method; it is not a pure JIT timer or a steady-state throughput measurement. The shape case also performs extra construction and reflection work.

| Case | Emission + delegate creation, ms | First execution, ms | Managed bytes allocated during emission, MiB |
| --- | ---: | ---: | ---: |
| DynamicMethod | 3.4 | 975.7 | 1.46 |
| RunAndCollect, 1 method/assembly | 118.4 | 1467.2 | 5.09 |
| RunAndCollect, 100 methods/assembly | 23.2 | 995.6 | 2.38 |
| RunAndCollect, 1 method/assembly + reflected shape | 161.6 | 1615.3 | 5.62 |

## Lifetime and release

Every one of the 12 processes passed these checks:

- All 1,000 invocations really suspended on an incomplete task.
- After clearing all delegate roots and collecting, zero delegate weak references remained alive, while all 1,000 owner-reference slots remained alive.
- The suspended calls resumed correctly and returned 42.
- After completing the calls, dropping task roots and collecting, zero tracked delegates and zero tracked owners remained alive.
- Generated assembly types were collectible in the default AssemblyLoadContext; no custom ALC was created.
- Process thread count stayed at nine across every recorded stage.

For DynamicMethod the owner observer targets its managed DynamicMethod. For assembly cases it targets the created RuntimeType. The 100-method batch repeats references to the same type; 1,000 owner-reference slots do not imply 1,000 distinct types. These weak checks establish that the observed managed objects died, not that every associated native page was immediately returned to the OS.

After release, process memory remained above the warmed baseline:

| Case | Residual private memory, MiB (range) | Residual live managed memory, KiB |
| --- | ---: | ---: |
| DynamicMethod | 2.98 (2.82–3.07) | 24.4 |
| RunAndCollect, 1 method/assembly | 11.05 (10.95–12.61) | 21.3 |
| RunAndCollect, 100 methods/assembly | 3.16 (2.29–3.18) | 12.3 |
| RunAndCollect, 1 method/assembly + reflected shape | 12.20 (11.75–12.92) | 21.3 |

Residual private memory can include retained GC commitment and runtime pools. This one-cycle experiment does not distinguish reusable caches from longer-lived native allocations or establish a repeated-cycle leak rate.

## Controls and provenance

All emitted methods use the same runtime-async await/add-one contract and `Async | NoInlining` flags. The shared-host DynamicMethod path is compared with `RunAndCollect` assembly paths. Workstation GC is selected; tiered compilation, ReadyToRun and GC stress are disabled. Each case warms 16 methods before baseline. Retention arrays and weak observers are preallocated before baseline.

Before every measured snapshot, eight full compacting collections/finalizer waits are performed. The worker pool is fixed at one worker and one IO completion thread to prevent pool expansion from affecting release measurements. This controlled setup is intended to isolate emission and retention costs, not model production concurrency or natural GC behavior.

The final dataset is [results.json](results.json). The implementation and full protocol are in [MemoryExperiment.cs](MemoryExperiment.cs), [run.ps1](run.ps1) and [README.md](README.md).

- Runtime: `.NET 11.0.0-dev`, x64, workstation GC.
- CoreLib MVID: `36d8bd66-9b46-4a81-8b3e-69002748af16`.
- CoreCLR SHA-256: `921df467cd5dfe1eee633cfabe4da09ca8db09aeb1d7ecd5d1b0e1be8f8b6530`.
- Full binary hashes and runtime settings are recorded in the dataset.
- Source checkout HEAD at measurement time: `2d5a5669f4a69a40efb95a051fcaba0fe032afe9`. This is context only; the existing binaries were not rebuilt, so that HEAD is not asserted to be their build revision.
- Final full-run operation: `44c02e5cb5f80ca2337c0928c24898d3`, successful exit.
- Raw run directory: `artifacts/experiments/emit-memory/20260915-164541` in the experiment transaction checkout.

## Implication for the runtime patch

The patch has a measured advantage for many small, independently collectible generated methods. A collectible assembly remains a viable alternative when methods and reflected data types can share a lifetime, especially when batching amortizes assembly overhead. A proposed assemblyless reflected-type feature was not implemented or measured here; these results establish the cost of the existing alternatives.
