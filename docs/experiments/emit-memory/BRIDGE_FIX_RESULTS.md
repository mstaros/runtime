# Pipeline measurements after the bridge fix

Measured 2026-09-15T18:32:55.0041748Z. Same 192-statement / 199-source-line async script, three fresh-process repetitions, two warm-up compilations per process, profiler enabled. All figures below are per script. Project builds, warm-up and forced lifecycle collections are outside generation timing.

**The bridge now allocates 3.35 MiB per script, down from 279.99 MiB. Full-pipeline allocation is 50.51 MiB, down from 327.16 MiB.**

## Before and after

Both columns are medians of three profiled runs. Before is the preceding measurement after the cursor-index fix.

| Phase or metric | Before bridge fix | After bridge fix |
| --- | ---: | ---: |
| Full generation, ms | 2481.23 | 221.10 |
| Full generation allocated, MiB | 327.16 | 50.51 |
| Frontend, ms | 11.17 | 18.42 |
| Frontend allocated, MiB | 2.61 | 2.60 |
| Bridge, ms | 2358.48 | 41.39 |
| Bridge allocated, MiB | 279.99 | 3.35 |
| Backend, ms | 111.53 | 161.22 |
| Backend allocated, MiB | 44.55 | 44.55 |
| First invocation, ms | 8.37 | 7.49 |

Bridge allocation fell 98.80%; total allocation fell 84.56%. The measured bridge-time ratio is 57.0 times, and total-time ratio is 11.2 times. Timing variability means these ratios are descriptive, not production throughput guarantees.

## Individual runs

| Run | Total ms | Frontend ms | Bridge ms | Backend ms | Total allocated MiB | Generation Gen0 / Gen1 / Gen2 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| 1 | 147.85 | 11.58 | 24.58 | 111.63 | 50.51 | 3 / 1 / 0 |
| 2 | 221.10 | 18.42 | 41.39 | 161.22 | 50.51 | 3 / 1 / 0 |
| 3 | 865.07 | 170.75 | 115.70 | 578.51 | 50.51 | 3 / 1 / 0 |

All three allocation totals are within 2,408 bytes. Backend allocation is exactly 46,717,248 bytes in every run, effectively unchanged from the preceding measurement. The third run is slower in every generation phase and in first invocation; this sample does not establish a backend regression. Elapsed times ranged from 147.85 to 865.07 ms.

Backend work now dominates cumulative allocation. The preceding method-level profile identified completion analysis, statement-body preparation, provenance validation and occurrence collection as the main remaining backend allocation paths. New .nettrace files were captured for this run and can be analyzed with the existing analyzer; do not assume old CPU sample percentages apply unchanged.

All arithmetic, real suspension/resumption, and final delegate/owner/context weak-reference collection checks passed. ScriptMcp was neither restarted nor used.

## Build identity and consistency

The bridge Release build succeeded at source commit eabb25f07e1a9cf6aa84f54f54decd847340ad4b. The compiler's first check reported up to date before the bridge finished refreshing shared dependencies. The first benchmark attempt therefore stopped during staging with conflicting CRoaring.Net.dll hashes, before any script timing. That attempt is excluded.

A subsequent compiler Release build, after the bridge completed, succeeded at the same source commit. The successful benchmark retained the dependency hash consistency check.

Operations:
- Bridge build: op_a1af12f2e7ec431f
- Sequential compiler refresh: op_fe1a847ac45748e6
- Successful benchmark: ff0bc6aef78fcca37779837b8b51f267

All runtime binary hashes, benchmark source hashes, and runtime settings match the preceding measurement. Both measurements use profiling mode. The refreshed payload has changed DLL hashes, recorded in the raw results. No compiler or bridge implementation edits were made during this experiment.

| Measured DLL | SHA-256 |
| --- | --- |
| MpsAgent.Merkle.Compiler.dll | b91544cebe720dd663551bfc2d37e813b6c174a1d7bc967f71a5526d6fbccc94 |
| MpsAgent.Merkle.Bridge.dll | 9f6327ef9493cd5606423cae89b12635d40e80a9fb7bcf5c70ac5ce706c617e4 |
| MerkleV2.dll | 3745ee1c36f347d005bc2960f2849bbaf819e00f6a687980136ed0be1d22437b |

## Evidence

Raw staged binaries, results and three traces:
D:\repos\runtime-async-dynamicmethod\artifacts\experiments\pipeline-memory\20260915-213121

Analyzer output location when processed:
D:\repos\runtime-async-dynamicmethod\artifacts\experiments\pipeline-profile\20260915-213121

The complete measured dataset is [bridge-fix-results.json](bridge-fix-results.json). Prior results are in [REMEASURE_RESULTS.md](REMEASURE_RESULTS.md), and the original multi-gigabyte profile is in [PROFILE_RESULTS.md](PROFILE_RESULTS.md).

Reproduce:

```powershell
.\run-pipeline.cmd -RuntimeRoot D:\repos\runtime-async-dynamicmethod -CompilerRoot D:\repos\MerkleCompiler -Count 1 -Repetitions 3 -Profile
```

These allocation values measure cumulative managed allocation, not live memory or peak process footprint. The profiler adds overhead. The previous Roslyn/ALC measurements were unprofiled and should not be treated as a controlled direct timing comparison with these runs. This is not a sustained concurrent churn test.
