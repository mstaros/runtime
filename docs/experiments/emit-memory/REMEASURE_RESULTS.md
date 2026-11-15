# Remeasurement after rebuilding the compiler

Measured 2026-09-15T17:48:14.1550047Z. One newly compiled 192-statement (199 source-line) async script per repetition; three fresh corerun processes, two warm-up compilations per process. The compiler and bridge were rebuilt in Release. ScriptMcp was neither refreshed nor used for these measurements. No compiler source edits were made during this remeasurement.

**The multi-gigabyte backend allocation is gone from this workload.** Median backend time is 111.53 ms, with 44.55 MiB allocated, versus the earlier profiled 7,177.86 ms and 2,952.77 MiB. The full pipeline is now 2,481.23 ms and 327.16 MiB; the bridge dominates what remains.

## Comparison

Before is the single earlier profiled run; after is the median of three profiled runs. All allocation numbers are cumulative per script, not retained or peak memory.

| Metric | Before | After |
| --- | ---: | ---: |
| Full generation, ms | 8537.09 | 2481.23 |
| Full generation allocated, MiB | 3235.47 | 327.16 |
| Frontend, ms | 9.11 | 11.17 |
| Frontend allocated, MiB | 2.61 | 2.61 |
| Bridge, ms | 1350.07 | 2358.48 |
| Bridge allocated, MiB | 279.99 | 279.99 |
| Backend compilation, ms | 7177.86 | 111.53 |
| Backend allocated, MiB | 2952.77 | 44.55 |
| First invocation, ms | 4.14 | 8.37 |

Relative to that earlier profile, backend median time is 64.4 times lower and backend allocation is 98.49% lower. Total allocation is 89.89% lower. These are descriptive ratios, not stable production speedup guarantees.

## Repetitions and remaining cost

| Repetition | Total ms | Bridge ms | Backend ms | Total allocated MiB | Gen0 / Gen1 / Gen2 collections during generation |
| --- | ---: | ---: | ---: | ---: | --- |
| 1 | 1388.34 | 1306.66 | 73.43 | 327.07 | 25 / 23 / 8 |
| 2 | 3497.76 | 3183.14 | 290.67 | 327.19 | 25 / 24 / 12 |
| 3 | 2481.23 | 2358.48 | 111.53 | 327.16 | 25 / 23 / 12 |

Elapsed time varied substantially: full generation 1.39–3.50 seconds and backend compilation 73–291 ms. Allocation was much more stable: total 327.07–327.19 MiB and backend approximately 44.55 MiB in all three runs.

The bridge still allocates approximately 280 MiB per script, effectively unchanged from the old run. Its measured elapsed time varied from 1.31 to 3.18 seconds. This remeasurement does not establish a bridge regression; timing varied while allocation remained stable. The prior profile identified repeated semantic-model acquisition and Roslyn operation construction as its dominant problem.

The result is a major reduction in backend work, with further optimization needed: 44.55 MiB is still allocated by the backend for this workload. No new stack-attribution claim is made for those remaining bytes here. The new EventPipe traces are available for a subsequent profile analysis.

Correct arithmetic, real suspension/resumption, and final delegate/owner/context weak-reference collection checks passed in all three repetitions.

## Build and comparability checks

The first attempted run was cancelled after discovering that the primary Release DLLs were still from 06:45–06:46 UTC, before the source fix. Those cancelled-run results are excluded.

The initial compiler build timed out after 180 seconds. Its retry succeeded in 176.719 seconds, and the bridge build succeeded in 51.270 seconds. Project build time, benchmark worker compilation, warm-up, and explicit lifecycle collections are outside measured generation.

Build operation identifiers:
- Compiler success: op_49f11c8b41724c8e
- Bridge success: op_960b93a82d6d447d
- Benchmark success: c41bf12673ccf37ad8ddaf3ac3378ece

The rebuilt source checkout was clean at commit 47928d72efa82eb083a6f38d36e4b322be8efe0f. This is the current rebuilt checkout, not an isolated one-line patch experiment. Eleven payload DLL hashes changed, so the before/after difference must not be attributed exclusively to one source edit.

The source inspected before building creates one temporary body projection and exact-path dictionary and shares them across occurrence construction and cycle-coverage projection.

All four runtime executable/library hashes are identical to the earlier profile; CoreLib MVID is 36d8bd66-9b46-4a81-8b3e-69002748af16. Runtime settings and profiling mode match. Tiered compilation, ReadyToRun, server GC and GC stress remain disabled.

The raw hashes of PipelineExperiment.cs and run-pipeline.ps1 differ only because Git checkout changed LF line endings to CRLF. Normalizing the newly read source to LF reproduces the exact earlier SHA-256 hashes:
- PipelineExperiment.cs: 6f895e95f0cadba9774df67723a976c01bc2baeed1c12634a4746c65e1740e58
- run-pipeline.ps1: 123302e106e92c2bd24dc7e890b279b3d7ff5776d95567078dc577c12c48f15b

There was no benchmark logic or workload change.

Selected measured payload hashes:
| DLL | Before SHA-256 | After SHA-256 |
| --- | --- | --- |
| MpsAgent.Merkle.Compiler.dll | 5d457d23fdd426b33969b021e999bce8c4e3f26f754b7507c3062944c1860784 | ce007c01fe830d44ac6761d578b8a6ec1a24cca8a30d1990703dec8827a3b095 |
| MpsAgent.Merkle.Bridge.dll | edf4bea5836b250a81730714a62e83b749afcde9160b85dfaf239c898b861ac9 | d4f9b1880bedcb23a20ea28ebd6ef9be5237914e13f1e3ef2973caa0b41b7458 |
| MerkleV2.dll | 4bab1a161ff016b4cad4f9c6069018ca26e1af2cb7fc9b14e9eca1038eccf147 | 1ffeedb272e58f484c16694bc2ffde7c13acf1e416f002762678c89b00549f73 |
| MpsAgent.Merkle.ControlFlow.dll | 2a90aea59c68b43864f37f1294fe63ad848087b00cac0d009fcd3d83a3678022 | 2512c181598b7459460146d0da9c142b4f2ec1dfc588a66756fa8a0f2a37107a |

## Evidence and reproduction

Raw results, staged payload and three .nettrace files:
D:\repos\runtime-async-dynamicmethod\artifacts\experiments\pipeline-memory\20260915-204717

Run the existing command from docs/experiments/emit-memory:

```powershell
.\run-pipeline.cmd -RuntimeRoot D:\repos\runtime-async-dynamicmethod -CompilerRoot D:\repos\MerkleCompiler -Count 1 -Repetitions 3 -Profile
```

[remeasure-results.json](remeasure-results.json) preserves the complete three-run dataset, runtime/payload hashes and settings. [PROFILE_RESULTS.md](PROFILE_RESULTS.md) documents the earlier profile. Profiling affects elapsed time; these figures should not be directly treated as an unprofiled comparison with the previous Roslyn/ALC benchmark.
