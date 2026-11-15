# Backend Fix Remeasurement

Measured on 2026-09-15 after compiler commit `58e8ce03eeaa92edbad4bf027d21e761875f309a`.

## Workload

- 192 generated statements, 199 source lines, two awaits
- one script per fresh worker
- three repetitions
- two warmup compilations per worker
- EventPipe profiling enabled
- runtime and experiment harness unchanged from the bridge-fix measurement

Raw capture: `artifacts/experiments/pipeline-memory/20260915-231309`

Profile output: `artifacts/experiments/pipeline-profile/20260915-231309`

## Results

| Run | Generation | Allocated | Frontend | Bridge | Backend | First invoke |
|---|---:|---:|---:|---:|---:|---:|
| 1 | 101.325 ms | 39.932 MiB | 7.838 ms | 17.977 ms | 75.482 ms | 3.720 ms |
| 2 | 294.424 ms | 39.932 MiB | 50.051 ms | 63.696 ms | 180.580 ms | 194.758 ms |
| 3 | 87.878 ms | 39.932 MiB | 10.023 ms | 18.922 ms | 58.904 ms | 4.170 ms |
| Median | **101.325 ms** | **39.932 MiB** | **10.023 ms** | **18.922 ms** | **75.482 ms** | **4.170 ms** |

Every run allocated 2.606 MiB in the frontend, 3.353 MiB in the bridge, and 33.972 MiB in the backend. Each generation caused two Gen0 collections, one Gen1 collection, and no Gen2 collections. Delegate, owner, and context weak references were all dead after release.

Run 2 was globally slow, including first invocation. Its allocation is identical to the other runs, so the median is used for timing comparison.

## Change from bridge-fix baseline

| Median metric | Before | After | Change |
|---|---:|---:|---:|
| Generation | 221.098 ms | 101.325 ms | 54.2% lower |
| Generation allocation | 50.512 MiB | 39.932 MiB | 20.9% lower |
| Backend | 161.222 ms | 75.482 ms | 53.2% lower |
| Backend allocation | 44.553 MiB | 33.972 MiB | 23.7% lower |
| Bridge allocation | 3.353 MiB | 3.353 MiB | unchanged |

## Remaining backend hotspots

The three traces contain 28 backend CPU samples and no lost events. CPU percentages are inclusive and directional. Allocation values are inclusive weighted allocation-tick estimates; rows overlap.

| Method | CPU samples | Share | Mean inclusive allocation |
|---|---:|---:|---:|
| `MerkleCompilationValidator.ValidateForDelegate` | 20 | 71.4% | 20.84 MiB |
| `MerkleControlFlowValidator.Validate` | 15 | 53.6% | 16.27 MiB |
| `MerkleCanonicalCompletionAnalysis.CanCompleteNormallyCore` | 15 | 53.6% | 15.56 MiB |
| `StatementBodyRewriteProvenance.TryValidate` | 12 | 42.9% | 6.43 MiB |
| `TryPrepareStatementBodyForEmission` | 11 | 39.3% | 5.22 MiB |
| `TryProjectRewrite` | 6 | 21.4% | 1.86 MiB |
| `PreparedBodyControlFlowGraphBuilder.TryBuild` | 5 | 17.9% | 10.95 MiB |
| `MerkleOccurrenceSnapshotProjection.BuildCursors` | 4 | 14.3% | 6.56 MiB |
| `CollectOccurrences` | 4 | 14.3% | 5.27 MiB |

The landed fix reduced the preparation/provenance allocation substantially:

| Method | Before | After |
|---|---:|---:|
| `TryPrepareStatementBodyForEmission` | 15.76 MiB | 5.22 MiB |
| `StatementBodyRewriteProvenance.TryValidate` | 13.76 MiB | 6.43 MiB |
| `TryProjectRewrite` | 5.56 MiB | 1.86 MiB |
| `CollectOccurrences` | 11.24 MiB | 5.27 MiB |

The largest remaining chain is delegate validation through control-flow validation and canonical completion analysis. Prepared CFG construction and cursor projection also remain large allocation sources.
