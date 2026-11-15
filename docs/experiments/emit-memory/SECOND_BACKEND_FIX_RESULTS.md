# Second Backend Optimization Remeasurement

Measured on 2026-09-15 after compiler commit `a40ecaa4bde292b1c0de1c7ec0614c4d08769cf9`.

## Workload

- 192 generated statements, 199 source lines, two awaits
- one script per fresh worker
- three repetitions
- two warmup compilations per worker
- EventPipe profiling enabled
- unchanged runtime and experiment harness

Raw capture: `artifacts/experiments/pipeline-memory/20260915-234452`

Profile output: `artifacts/experiments/pipeline-profile/20260915-234452`

## Results

| Run | Generation | Allocated | Frontend | Bridge | Backend | First invoke |
|---|---:|---:|---:|---:|---:|---:|
| 1 | 68.201 ms | 30.350 MiB | 8.470 ms | 14.711 ms | 44.991 ms | 3.198 ms |
| 2 | 63.240 ms | 30.350 MiB | 6.176 ms | 13.954 ms | 43.087 ms | 3.484 ms |
| 3 | 59.725 ms | 30.352 MiB | 6.642 ms | 14.597 ms | 38.455 ms | 4.961 ms |
| Median | **63.240 ms** | **30.350 MiB** | **6.642 ms** | **14.597 ms** | **43.087 ms** | **3.484 ms** |

Each run caused one Gen0 collection and no Gen1 or Gen2 collections. Delegate, owner, and context weak references were all dead after release.

## Change from previous backend-fix measurement

| Median metric | Previous | New | Change |
|---|---:|---:|---:|
| Generation | 101.325 ms | 63.240 ms | 37.6% lower |
| Generation allocation | 39.932 MiB | 30.350 MiB | 24.0% lower |
| Backend | 75.482 ms | 43.087 ms | 42.9% lower |
| Backend allocation | 33.972 MiB | 24.389 MiB | 28.2% lower |
| Bridge allocation | 3.353 MiB | 3.353 MiB | unchanged |

## Remaining backend allocations

The three traces contain only eight backend CPU samples and no lost events. CPU rankings are therefore too sparse for method timing conclusions. Allocation values below are inclusive weighted allocation-tick estimates; rows overlap.

| Method | Mean inclusive allocation | Previous |
|---|---:|---:|
| `MerkleControlFlowValidator.Validate` | 10.82 MiB | 16.27 MiB |
| `MerkleCanonicalCompletionAnalysis.CanCompleteNormallyCore` | 10.41 MiB | 15.56 MiB |
| `CompileStatementBody` | 7.50 MiB | 7.74 MiB |
| `PreparedBodyControlFlowGraphBuilder.TryBuild` | 7.38 MiB | 10.95 MiB |
| `FinalLoweredBodyControlFlowAnalyzer.TryAnalyze` | 7.29 MiB | 7.53 MiB |
| `StatementBodyRewriteProvenance.TryValidate` | 5.49 MiB | 6.43 MiB |
| `TryPrepareStatementBodyForEmission` | 5.22 MiB | 5.22 MiB |
| `CollectOccurrences` | 4.40 MiB | 5.27 MiB |
| `MerkleOccurrenceSnapshotProjection.BuildCursors` | 3.69 MiB | 6.56 MiB |
| `TryProjectRewrite` | 1.86 MiB | 1.86 MiB |

The optimization reduced the major control-flow-validation and CFG construction paths by roughly one third, and cursor construction by roughly 44%. Preparation and rewrite projection are now flat relative to the preceding measurement.
