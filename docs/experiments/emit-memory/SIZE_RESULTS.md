# Larger async methods: size scaling

Measured 2026-09-15 on the same patched Windows x64 Core_Root as the original experiment.

**Memory and DynamicMethod startup showed approximately linear growth plus a fixed overhead across this workload. Assembly startup timings were too variable to establish a precise linear relationship. None of the costs were proportional to source statement count.**

The DynamicMethod advantage narrowed with body size. At 24 statements, generation plus first invocation was 1.417 ms versus 2.226 ms, and additional private memory was 8.53 versus 42.22 MiB for 1,000 retained methods. At 192 statements, those values were 2.985 versus 3.554 ms and 26.23 versus 56.54 MiB. The latter is about 16% less startup time and 2.16x less additional private memory with DynamicMethod, for this synthetic body.

## Workload and protocol

- 1,000 independently generated methods per child process, three fresh processes per case: 30 full runs.
- Exactly one RunAndCollect assembly and one declaring type per assembly-backed method. No assembly batching or cache reuse.
- Larger bodies contain 24, 48, 96 or 192 unrolled arithmetic/conditional statements, two await statements, one int local and a return. One of each three body statements is a conditional; the others mix multiply/add and XOR/shift operations.
- The same computation is emitted through DynamicMethod and MethodBuilder. Input-dependent results prevent unused-work elimination. Constants vary across statements.
- Both await sites use the same input task. The lifetime check forces the first await to suspend; after resumption the second sees the completed task. This isolates body-size growth while holding await-site count at two. It does not measure growth in the number of actual suspensions.
- The tiny 9-byte control retains the original await/add-one method, with one await site. Fits below exclude this control.
- Each size/backend warms the full path before baseline. Generated outputs are checked against an independent arithmetic evaluator using positive, zero, negative and overflow-edge inputs.
- All methods use Async | NoInlining. Workstation GC, disabled tiering/ReadyToRun/GC stress and fixed thread-pool limits match the original experiment.
- Eight full compacting GC/finalizer rounds precede each snapshot. These rounds and final cleanup are outside the measured emission/first-invocation phases.
- The full 24-statement C# equivalent and runner instructions are in [README.md](README.md). These are operation counts, not a general mapping from arbitrary script lines to cost.

## Timings, consistently per method

Every entry is the median of three batch measurements divided by 1,000. Emission includes defining the method/type and creating the delegate. Startup includes emission plus first invocation with a completed input task, including the work triggered by first-use JIT compilation. Startup medians are computed from each run's combined phases, not by adding separately computed medians. Steady-state invocation throughput is not measured.

| Body statements | Emitted IL bytes | DynamicMethod emission, µs/method | Assembly emission, µs/method | DynamicMethod startup, ms/method | Assembly startup, ms/method |
| --- | ---: | ---: | ---: | ---: | ---: |
| Tiny control | 9 | 3.2 | 127.9 | 1.134 | 1.843 |
| 24 | 316 | 5.7 | 146.3 | 1.417 | 2.226 |
| 48 | 633 | 9.5 | 192.5 | 1.727 | 2.989 |
| 96 | 1279 | 13.6 | 176.1 | 1.905 | 2.461 |
| 192 | 2653 | 30.1 | 181.2 | 2.985 | 3.554 |

An 8x increase from 24 to 192 body statements increased DynamicMethod emission by 5.30x and startup by 2.11x. Assembly emission increased by 1.24x and startup by 1.60x at the medians. Large fixed costs and measurement variation make these unlike simple proportional scaling. At 192 statements the emission advantage remains about 6.0x, while the full startup advantage is about 1.19x.

## Retained memory

Deltas from each process's warmed baseline, after first invocation and full GC, with all 1,000 delegates rooted. MiB is 1,048,576 bytes. Private memory includes GC heap commitment plus native allocations; live managed memory is a different measurement.

| Body statements | DynamicMethod private delta, MiB | Assembly private delta, MiB | Assembly / DynamicMethod private ratio | DynamicMethod live managed delta, MiB | Assembly live managed delta, MiB |
| --- | ---: | ---: | ---: | ---: | ---: |
| Tiny control | 3.98 | 40.09 | 10.06x | 1.04 | 0.31 |
| 24 | 8.53 | 42.22 | 4.95x | 2.13 | 0.31 |
| 48 | 13.12 | 43.91 | 3.35x | 3.23 | 0.31 |
| 96 | 16.64 | 47.21 | 2.84x | 5.43 | 0.31 |
| 192 | 26.23 | 56.54 | 2.16x | 9.91 | 0.31 |

For 24–192 statements, private memory rose 3.08x for DynamicMethod and 1.34x for assemblies while statement count rose 8x. The additional private-memory gap remained about 30–34 MiB per 1,000 methods. The ratio shrank because the body-dependent cost became a larger share of DynamicMethod's total.

The assembly path's much smaller live managed total does not make it cheaper overall: much of its cost is native. DynamicMethod's live managed memory grew with method size.

## Is the dependency linear?

A descriptive least-squares fit of the form `cost = intercept + slope * bodyStatements` to the four larger-case medians gives:

| Backend | Metric | Intercept | Slope per statement | R² |
| --- | --- | ---: | ---: | ---: |
| dynamic | Emission µs / method | 1.770 | 0.1439 | 0.984 |
| dynamic | Startup µs / method | 1193.640 | 9.0524 | 0.973 |
| dynamic | Private MiB / 1,000 methods | 7.100 | 0.1003 | 0.986 |
| assembly | Emission µs / method | 164.247 | 0.1086 | 0.168 |
| assembly | Startup µs / method | 2237.662 | 6.3298 | 0.633 |
| assembly | Private MiB / 1,000 methods | 39.757 | 0.0857 | 0.993 |

The memory fits and DynamicMethod timing fits are close to straight lines over these four sizes. Assembly timing fits are weaker, with visible non-monotonic samples. These fits are descriptions of this run, not complexity proofs, estimates of physical allocator constants, or predictions outside the tested interval. The fitted intercept is not an independently measured zero-size cost.

Allocation chunking, GC activity, JIT optimization choices, branch structure, locals and additional await sites can change the curve. The experiment supports a fixed-overhead-plus-size-cost interpretation for this workload, not a universal linear rule for async scripts.

## Variation and validation

Observed minimum–maximum across the three fresh processes:

| Body statements | Backend | Emission, µs/method | Startup, ms/method | Private delta, MiB / 1,000 methods |
| --- | --- | ---: | ---: | ---: |
| Tiny control | dynamic | 2.8–3.7 | 1.005–1.216 | 3.98–4.72 |
| Tiny control | assembly | 122.5–189.9 | 1.581–2.167 | 39.95–40.09 |
| 24 | dynamic | 5.4–6.3 | 1.288–1.442 | 8.45–9.19 |
| 24 | assembly | 117.8–182.7 | 1.983–2.291 | 42.02–42.46 |
| 48 | dynamic | 7.4–50.9 | 1.585–2.074 | 13.04–13.19 |
| 48 | assembly | 130.9–266.4 | 2.434–3.656 | 43.78–43.91 |
| 96 | dynamic | 12.8–20.6 | 1.585–1.947 | 16.60–16.66 |
| 96 | assembly | 128.2–235.2 | 2.459–3.005 | 47.05–47.39 |
| 192 | dynamic | 25.9–33.4 | 2.829–3.033 | 25.48–26.27 |
| 192 | assembly | 150.8–184.4 | 3.205–3.625 | 56.51–57.30 |

Some timing ranges are broad, particularly the 48-statement assembly case. Medians should not be read as precise latency guarantees or used to infer that 48 statements inherently compile more slowly than 96.

All 30 full runs passed result and lifetime checks. After clearing delegate roots while calls were pending, all delegate weak references were dead and all 1,000 owner-reference slots remained alive. Calls then resumed correctly. After releasing tasks and collecting, every tracked delegate and owner was dead. Assembly types were collectible in the default ALC. Every recorded process thread count was nine.

All recorded LOH sizes and fragmentation values were zero. This is evidence about the post-GC snapshots only: temporary LOH allocations before collection were not traced, and the largest method here is still only 2,653 bytes of emitted IL.

Managed allocation and GC-commitment context:

| Body statements | Backend | Emission managed allocation, KiB/method | Retained GC commitment delta, MiB / 1,000 | Residual private delta after release, MiB |
| --- | --- | ---: | ---: | ---: |
| Tiny control | dynamic | 1.500 | 2.563 | 2.180 |
| Tiny control | assembly | 5.211 | 4.000 | 12.965 |
| 24 | dynamic | 3.422 | 6.563 | 5.430 |
| 24 | assembly | 7.812 | 6.125 | 14.102 |
| 48 | dynamic | 5.429 | 10.750 | 7.586 |
| 48 | assembly | 9.820 | 7.813 | 17.336 |
| 96 | dynamic | 9.375 | 13.570 | 9.969 |
| 96 | assembly | 13.765 | 11.063 | 16.672 |
| 192 | dynamic | 17.289 | 20.566 | 8.641 |
| 192 | assembly | 21.680 | 15.371 | 14.344 |

Dead weak references do not imply immediate return of every page to the OS. The residual private values can include GC segments and native pools; a repeated-cycle leak rate was not measured.

## Reproduction and provenance

```powershell
.\run.cmd -RuntimeRoot D:\repos\runtime-async-dynamicmethod -Count 1000 -Repetitions 3 -SizeSweep
```

Complete measurements, source hashes and runtime fingerprints are in [size-results.json](size-results.json). The original tiny-method results remain in [RESULTS.md](RESULTS.md) and [results.json](results.json).

- Full run operation: `69e097ff3fb72432fe0ac08329247ec5`; exit 0, all 30 cases complete.
- Source checkout HEAD at measurement: `8865490091da4b1d0af49028db60896c540da7f0` (context only; existing runtime binaries were not rebuilt).
- Runtime: .NET 11.0.0-dev, Windows x64; CoreLib MVID `36d8bd66-9b46-4a81-8b3e-69002748af16`.
- CoreCLR SHA-256: `921df467cd5dfe1eee633cfabe4da09ca8db09aeb1d7ecd5d1b0e1be8f8b6530`.
- Compiler input source hashes identify the measured files' exact bytes; Git checkout newline conversion may change file-byte hashes without changing source semantics.
