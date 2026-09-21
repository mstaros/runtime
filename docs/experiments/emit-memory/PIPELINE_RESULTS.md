# Actual C# compiler pipeline: measured results

Measured 2026-09-15T16:11:52.6650396Z. This experiment compares the existing Merkle Release compiler and patched async DynamicMethod with Roslyn PE emission plus one collectible AssemblyLoadContext per independently collectible script. It does not rebuild or modify the compiler.

**The current compiler snapshot has a severe size-dependent compilation/allocation problem on this workload.** The 192-statement case takes a median 6.70 seconds to generate and allocates 3,235 MiB per script. The same arithmetic/conditional body also takes seconds through the live ScriptMcp tool. These results cannot be presented as the inherent cost of DynamicMethod emission or as a prediction for an optimized compiler.

## Generation and first execution

Medians of three fresh-process repetitions; milliseconds and cumulative managed allocation per script. Each repetition retains 32 scripts at 24 statements, 8 at 96, and 2 at 192. Sources and metadata references are cached/prepared before timing. The scripts themselves are newly compiled with different constants.

| Statements | Path | Generate, ms | First execution, ms | Generate + first, ms | Allocated, MiB |
|---:|---|---:|---:|---:|---:|
| 24 | Merkle → async DynamicMethod | 91.04 | 1.76 | 92.80 | 63.006 |
| 24 | Roslyn → PE → collectible ALC | 2.64 | 1.39 | 4.47 | 0.580 |
| 96 | Merkle → async DynamicMethod | 1080.91 | 2.16 | 1082.92 | 832.569 |
| 96 | Roslyn → PE → collectible ALC | 9.29 | 1.99 | 11.26 | 1.335 |
| 192 | Merkle → async DynamicMethod | 6699.05 | 4.94 | 6703.48 | 3235.496 |
| 192 | Roslyn → PE → collectible ALC | 12.24 | 2.82 | 15.90 | 2.974 |

The sum column is the median of each repetition's combined time, not the sum of separately calculated medians. Merkle generation ranges were 24 statements: 79.73–248.21 ms; 96 statements: 887.34–1098.39 ms; 192 statements: 6073.61–8373.93 ms. These are small samples, not confidence intervals. There was substantial timing variation.

## Where generation time goes

Each cell is a median milliseconds/script. All production validation, guards and provenance remain enabled. The public bridge itself calls GetDiagnostics again; that repeat is included in the bridge phase.

| Statements | Merkle frontend | Roslyn → Merkle bridge | CompileInstanceScriptUnit | Roslyn frontend | Roslyn Emit | ALC load + bind |
|---:|---:|---:|---:|---:|---:|---:|
| 24 | 1.59 | 24.54 | 64.91 | 0.89 | 1.52 | 0.19 |
| 96 | 4.60 | 272.07 | 789.14 | 4.03 | 4.99 | 0.21 |
| 192 | 11.21 | 1465.62 | 5221.98 | 6.02 | 6.04 | 0.25 |

A separate probe starts with a fresh compilation for each script, measures the initial GetDiagnostics, then immediately repeats it on the same compilation:

| Statements | Parse + compilation, ms | Initial GetDiagnostics, ms | Repeat GetDiagnostics, ms |
|---:|---:|---:|---:|
| 24 | 0.157 | 0.685 | 0.393 |
| 96 | 0.426 | 2.048 | 1.726 |
| 192 | 0.655 | 2.976 | 2.726 |

GetDiagnostics does not explain seconds of elapsed time here. At 192 statements, bridge allocations are 280.02 MiB/script and CompileInstanceScriptUnit allocations are 2952.78 MiB/script. Allocation growth from 24 to 192 statements is about 51× for 8× as many statements. This is evidence of superlinear work on this workload, not proof of a particular asymptotic complexity or root cause.

These are statements, not IR-node counts. Each expression/conditional contains several nodes; this experiment does not count them. CompileInstanceScriptUnit includes validation, lowering, control-flow/provenance work and emission. No CPU/allocation stack profile has yet identified the specific expensive functions. Source inspection finds repeated validation/traversal and source-map construction, but that alone does not establish the cause.

## Cross-check through the live script tool

After the benchmark workers finished, the same unrolled arithmetic/conditional body was submitted to ScriptMcp. Server operation timestamps give these end-to-end durations:

| Body | Operation | Duration | Result |
|---|---|---:|---:|
| 192 statements, salt 1000 | op_61e82067a53a4689 | 6.378 s | 1584255456 |
| 192 statements, salt 1001 | op_58ce242fd5884a52 | 10.357 s | -243031298 |
| 24 statements, salt 1000 | op_1d18e547ffb144bd | 0.130 s | 474905574 |

The 24-statement control places its second await at the end; the 192-statement cases preserve midpoint placement. The live bodies use completed Task.FromResult(41) values, have no external I/O and return the arithmetic result. This reproduces seconds of latency independently of the benchmark's timing code. The short control also explains why ordinary small tool scripts can feel fast.

The live tool loads compiler DLLs from ScriptMcp.Server/bin/Debug/net11.0/win-x64 and CoreLib MVID 350d78c0-e0ec-4283-9f8d-e64f8b068550. The isolated benchmark loads existing MerkleCompiler Release DLLs and CoreLib MVID 36d8bd66-9b46-4a81-8b3e-69002748af16. They are different environments; do not equate their phase timings. Live assembly paths/MVIDs, exact submitted bodies, operation timestamps and results are preserved in [live-script-check.json](live-script-check.json).

## Retained managed memory

Median incremental managed bytes after forced GC, converted to KiB/script. These are live managed objects only, not total runtime/native method or assembly size.

| Statements | Merkle, delegate retained | Roslyn ALC, delegate retained | Merkle, only pending execution | Roslyn ALC, only pending execution |
|---:|---:|---:|---:|---:|
| 24 | 146.11 | 1.42 | 10.06 | 1.62 |
| 96 | 548.63 | 2.06 | 31.88 | 2.11 |
| 192 | 1090.59 | 5.88 | 63.09 | 6.07 |

The compiler retains source/body provenance with generated artifacts; its retained graph is part of the production behavior measured here. A heap profile would be needed to attribute those bytes precisely.

Private-byte measurements are in the raw data but are too sensitive to GC heap commitment/decommitment to assign an exact per-assembly size in these small compiler batches. For example, the 24-statement Merkle live delta ranges from -27.38 to 84.88 KiB/script, including a negative value. Natural compilation garbage collections and later forced collections can release warm-up commitments. LOH snapshots likewise report live post-GC state, not temporary allocation volume.

## Separate ALC controls

Controls use 1,000 contexts per repetition. The loaded control compiles one 24-statement PE before measurement, then loads it independently into each collectible ALC.

| Control | Create/load generation, ms/context | First execution, ms/context | Live managed, KiB/context | Private delta, KiB/context |
|---|---:|---:|---:|---:|
| Empty collectible ALC | 0.013 | 0.000 | 0.358 | 9.59 |
| Load precompiled PE + bind | 0.134 | 1.999 | 0.677 | 24.87 |

Empty ALC private deltas range 8.78–9.59 KiB/context; loaded/jitted deltas range 24.80–25.47 KiB/context. These workload averages do not support attributing hundreds of KiB or 1.2 MiB to an empty ALC itself. Old BenchmarkDotNet Allocated numbers measure cumulative managed allocation for the timed operation, not retained ALC size.

## Methodology and limits

- 33 successful worker runs: three repetitions of two compiler paths × three sizes, three diagnostics sizes, and two ALC controls. All final delegate/owner/context weak references were dead after release.
- Input-dependent integer arithmetic, shifts and branches, two await sites, one int local, unique salt. Both awaits use the same Task; the lifetime check forces the first to suspend and the second then observes completion.
- Warm-up verifies five integer inputs. Measured first execution and suspended/resumed execution verify an independent arithmetic oracle. Delegates are dropped and ALC unloading is requested while tasks remain pending, then execution completes correctly and all tracked references clear.
- Roslyn produces two types per script: script wrapper plus classic async state machine. Merkle produces an async DynamicMethod without a declaring type. No named private shapes, records or additional DTOs are included.
- Both paths include Roslyn parsing/semantic diagnostics and share cached references. The harness calls the public Merkle bridge/backend directly; it does not include the entire live ScriptMcp hosting/package-resolution pipeline.
- Workstation GC; tiered compilation, ReadyToRun and GCStress disabled consistently. Existing compiler DLLs report JIT optimizations enabled. Source HEAD is context only; payload hashes identify the actual tested binaries.
- Generation allocation uses process-wide GC.GetTotalAllocatedBytes; individual phases use current-thread counters. Private bytes include native heaps and GC commitments. Thread counts are recorded (usually 10; one load-control stage reached 11).
- Forced full collections/finalizer waits and 20 ms cleanup waits occur outside generation and first-invocation timers. Natural GC during compilation is included. This is not a concurrent or sustained-churn throughput benchmark.
- An optional Roslyn runtime-async PE pilot crashed with AccessViolation during warm-up on this Core_Root. It is excluded from the completed matrix. Its cause was not diagnosed; these results compare against Roslyn's classic async state machine only.
- No claim of a fixed production speedup follows from earlier hand-IL tests. Hosting overhead and current compiler behavior are distinct measurements.

Reproduce with the README command. [pipeline-results.json](pipeline-results.json) contains the complete 33-run dataset, source hashes, runtime/payload SHA-256 hashes, timings, GC counts and lifecycle snapshots.
