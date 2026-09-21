# Backend Optimization Series

Measurements use the same profiled workload: one 192-statement, 199-line async script with two awaits, three fresh workers, and two warmup compilations per worker.

## Series

| Compiler head | Median generation | Total allocation | Median backend | Backend allocation | Notes |
|---|---:|---:|---:|---:|---|
| `a40ecaa4` | 63.240 ms | 30.350 MiB | 43.087 ms | 24.389 MiB | stable timing baseline |
| `1093cbb4` | 168.723 ms | 23.559 MiB | 115.029 ms | 17.596 MiB | globally noisy timing |
| `02a9f2a1` | 50.984 ms | 19.939 MiB | 29.431 ms | 13.979 MiB | stable timing |
| `f5e34027` | 71.200 ms | 18.635 MiB | 34.710 ms | 12.672 MiB | third run globally noisy |

From `a40ecaa4` to `f5e34027`, total allocation fell 38.6% and backend allocation fell 48.0%. Bridge allocation remained approximately 3.353 MiB throughout.

## Latest run: f5e34027

Raw capture: `artifacts/experiments/pipeline-memory/20260916-062347`

Profile output: `artifacts/experiments/pipeline-profile/20260916-062347`

| Run | Generation | Allocated | Frontend | Bridge | Backend | First invoke |
|---|---:|---:|---:|---:|---:|---:|
| 1 | 71.200 ms | 18.644 MiB | 9.830 ms | 26.630 ms | 34.710 ms | 5.996 ms |
| 2 | 63.021 ms | 18.632 MiB | 7.503 ms | 21.425 ms | 34.059 ms | 3.874 ms |
| 3 | 388.660 ms | 18.635 MiB | 100.547 ms | 174.534 ms | 113.510 ms | 6.575 ms |
| Median | **71.200 ms** | **18.635 MiB** | **9.830 ms** | **26.630 ms** | **34.710 ms** | **5.996 ms** |

Each run caused one Gen0 collection and no Gen1 or Gen2 collections. Delegate, owner and context weak references were dead after release. Allocation varied by only 12 KiB while timing varied across every phase, so allocation is the reliable comparison for this batch.

Compared with `02a9f2a1`, total allocation fell 6.5% and backend allocation fell 9.3%. The first two backend times were 34.710 and 34.059 ms versus the prior stable median of 29.431 ms, so this run does not establish a speed improvement.

## Latest backend allocation profile

The three traces contain 12 backend CPU samples and no lost events. CPU ranks are directional. Allocation values are inclusive weighted allocation-tick estimates; rows overlap.

| Method | Mean inclusive allocation |
|---|---:|
| `DelegateCompiler.CompileStatementBody` | 5.00 MiB |
| `FinalLoweredBodyControlFlowAnalyzer.TryAnalyze` | 4.76 MiB |
| `MerkleControlFlowValidator.Validate` | 2.75 MiB |
| `MerkleCanonicalCompletionAnalysis.CanCompleteNormallyCore` | 2.35 MiB |
| `PreparedBodyControlFlowGraphBuilder.TryBuild` | 2.33 MiB |
| `ValidateExactRoot` | 2.13 MiB |
| `CompilerEntryLowerer.Lower` | 2.04 MiB |
| `MerkleOccurrenceSourceMap.CompleteIdentity` | 1.75 MiB |
| `MerkleScriptUnit.ComputeHash` | 1.69 MiB |
| `TryPrepareStatementBodyForEmission` | 1.64 MiB |
| `StatementBodyRewriteProvenance.TryValidate` | 0.11 MiB |
| `StatementBodyRewriteProvenance.TryProjectRewrite` | 0.11 MiB |

Provenance validation is no longer a major allocator. The largest remaining overlapping chain is statement-body compilation through final control-flow analysis, followed by control-flow validation/completion analysis.
