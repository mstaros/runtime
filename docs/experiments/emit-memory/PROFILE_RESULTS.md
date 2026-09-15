# Compiler profile and prioritized findings

Measured 2026-09-15. Analysis only: no Merkle compiler implementation was changed. The user reports that the statement-occurrence cursor-index fix is already WIP. This report describes the captured pre-fix Release binaries; it does not assess or benchmark that WIP.

## Priorities

| Priority | Finding | Evidence | Status |
| --- | --- | --- | --- |
| P1 | Rebuild the body cursor snapshot for every source-map entry | 96.14% of compiler-thread samples; 98.55% of compiler-phase weighted allocation | Confirmed; fix already WIP |
| P1 | Repeated semantic-model acquisition in the bridge defeats reuse of Roslyn semantic/operation work | Bridge 1,350 ms and 280.0 MiB; GetOperationWorker contains 99.43% of bridge weighted allocation | Measured hotspot; source identifies repeated acquisition |
| P1 | The cycle-coverage loop also rebuilds the whole snapshot for each coverage site | A second production call to the same expensive resolver, inside a loop | Source-confirmed remaining variant; not measured by this acyclic workload |
| P2 | Repeated GetOperation calls for the same expression in BuildCore | Multiple explicit calls on the same SemanticModel and syntax node | Redundant queries; independent impact not isolated |
| P2 | Snapshot construction allocates two cursors per occurrence and discards its path dictionary | Generator source and allocation types agree | Reassess after eliminating repeated snapshots |

P1 means address promptly for repeated script compilation; it is not a claim of miscompilation or data loss. The profile supports two dominant measured performance defects, not an arbitrary list of independent multi-gigabyte bugs.

## 1. Statement occurrence construction: already WIP

Source: D:\repos\MerkleCompiler\MpsAgent.Merkle.Compiler\Compiler\Backend\FinalLoweredBodyControlFlow.cs, TryBuildStatementOccurrences, around lines 270–329.

The method iterates sorted SourceMap entries and calls MerkleSlotModel.TryResolve(preparedBody.Body, path, ...) for each retained entry. The generated resolver calls MerkleTraversalSnapshot.CollectCursors(root) on every invocation. That walks the body and constructs its complete cursor snapshot.

OccurrenceSnapshotGenerator.cs, around lines 325–344, emits the resolver. Its CollectCursors implementation, around lines 166–172, walks the entire root. BuildCursors, around lines 76–120, merges typed occurrence lists, sorts them, allocates seed and final cursor objects, copies parent paths, and builds a path dictionary.

A dictionary built once for the final body removes the repeated traversal/construction. Keep it temporary, retain exact MerklePath equality, sorted source-map order, statement ordinals, nested-callable exclusions, and coverage validation. A shared node can have multiple occurrence paths; indexing by node identity or subtree hash would change semantics.

Important scope refinement: finding 3 identifies another resolver loop in the same analyzer. A temporary index local to the body analysis can serve both loops. It need not become part of PreparedStatementBody or the returned control-flow object.

## 2. Bridge semantic-model churn: next measured priority

Source: D:\repos\MerkleCompiler\MpsAgent.Merkle.Bridge\RoslynScriptBridgeService.cs:

- BuildTopLevelExpressionOrDeconstructionStatement, around lines 8224–8249, gets a SemanticModel and queries the enclosing symbol. On the ordinary-expression path it calls BuildTopLevelExpressionStatement with the compilation.
- BuildTopLevelExpressionStatement, around lines 8252–8267, obtains a SemanticModel again before building that expression.
- BuildTopLevelIfStatement, around lines 7701–7732, obtains a model for the condition and recursively builds branches with the compilation, allowing the same pattern to recur.
- Similar model acquisition occurs in local declarations, loops, switch sections, returns, and other statement builders. The file has 28 GetSemanticModel call sites; this is a static count, not a runtime call count.

The bridge took 1,350.07 ms and allocated 293,592,528 bytes on its compiling thread. Weighted allocation stacks place 292,216,752 bytes (99.43%) under MemberSemanticModel.GetOperationWorker and 215,705,552 bytes (73.40%) under GetRootOperation. These are nested inclusive totals, not additive costs. 80 of 89 bridge-thread samples include GetOperationWorker.

The strongest explanation is rebuilding Roslyn semantic/operation state after repeatedly acquiring models for the same compilation/tree. SemanticModel owns reusable local semantic information; Microsoft recommends reusing one model for multiple questions about a tree. [SemanticModel remarks](https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.semanticmodel?view=roslyn-dotnet-4.13.0).

Suggested fix boundary: one model per exact compilation/tree/options combination for one bridge invocation, passed through statement/expression builders. If a pass replaces the compilation or syntax tree, its model must change too. This requires no global cache and must not extend Roslyn object lifetime beyond compilation.

The hotspot and repeated-acquisition source pattern are confirmed. The exact savings from model reuse have not been measured; this review does not claim all 1.35 seconds are removable.

## 3. Cycle-coverage projection repeats the same expensive resolution

Source: D:\repos\MerkleCompiler\MpsAgent.Merkle.Compiler\Compiler\Backend\FinalLoweredBodyControlFlow.cs, around lines 165–235; specifically the TryResolve call at line 189.

Before TryBuildStatementOccurrences, the analyzer loops over canonicalSites. Each coverage site resolves its final-body path through MerkleSlotModel.TryResolve(preparedBody.Body, ...), which rebuilds the entire body snapshot.

For K coverage sites and N body occurrences, this adds K complete snapshot constructions. With many independent cycles, K can grow with body size. A fix confined to the later source-map loop leaves this variant intact.

The captured script has unrolled arithmetic/conditionals and awaits, but no source loops or gotos, so it does not establish the timing impact of this path. Review the WIP scope against this caller. Reuse the same temporary body index while preserving body-ID checks, occurrence decoding/round-trip validation, statement-type checks, and source-map coverage checks.

A useful post-fix workload is a large body with many independent loops, plus nested local functions/lambdas to exercise occurrence exclusions. No such new benchmark or product change was applied in this analysis.

## Secondary findings, not yet another P1

### Repeated operation lookup

D:\repos\MerkleCompiler\MpsAgent.Merkle.Bridge\Symbols\MerkleConstructorDeclarationBuilder.cs contains MerkleInitializerExpressionBuilder.BuildCore. Around lines 879–993 it repeatedly calls semanticModel.GetOperation(expressionSyntax) for conditional-access, dynamic, field, property, delegate/method and local-reference checks. Earlier branches also ask for the same operation.

A local lazy operation value could avoid repeated queries while preserving early syntax-only paths and semantic dispatch ordering. However, queries on the same model can reuse cached work: these calls are not evidence that every query rebuilds an entire operation tree. Measure this after fixing model ownership; do not count the bridge's 280 MiB twice.

### Shared resolver and snapshot overhead

RewriterBaseGenerator.cs, around lines 793–816, emits TryResolveProjected. Even with an existing projection, it scans the cursor list for each path segment. Snapshot reuse alone therefore does not provide indexed resolution: a path of depth D can require D scans of N cursors. The proposed exact-path dictionary avoids this work at the occurrence caller.

BuildCursors creates seed cursors and then final cursors, sorts an intermediate list, allocates copied parent paths, and discards the dictionary it just built. That increases the cost of one snapshot. Most measured expense is amplified by repeated construction, so the current profile cannot establish this as a separate post-fix priority.

## What the profile does not support

- Provenance preparation is not the dominant path here: TryPrepareStatementBodyForEmission appears in 13/544 compiler-thread samples (2.39%) and contains 0.54% of weighted allocation.
- TryProjectRewrite appears in 6/544 samples (1.10%) and contains 0.19% of weighted allocation. Its correspondence loop is inactive in the production calls under review because correspondence is omitted.
- Do not disable release validation from this evidence. Existing failure behavior and coverage/provenance invariants matter.
- The benchmark counts 192 generated arithmetic/conditional statements, not 192 IR nodes. It does not count total occurrence paths.
- GetDiagnostics is not a seconds-scale explanation: the preceding benchmark measured roughly 3 ms for initial diagnostics at this size.
- Cumulative allocation is not retained memory. The roughly 3.16 GiB generation allocation does not mean each delegate retains 3.16 GiB.
- No post-fix speedup, new correctness failure, or leak is established by this trace.

## Measured data

One measured 192-statement script after two warm-up compilations, in one corerun process. Existing compiler payload hashes match the prior pipeline benchmark.

| Phase | Elapsed ms | Current-thread allocated bytes |
| --- | ---: | ---: |
| Frontend | 9.1085 | 2,731,760 |
| Roslyn to Merkle bridge | 1,350.0683 | 293,592,528 |
| CompileInstanceScriptUnit | 7,177.8577 | 3,096,203,704 |

Full generation: 8,537.0922 ms and 3,392,632,248 process-wide allocated bytes. First invocation: 4.1389 ms. Natural generation GC counts: Gen0 242, Gen1 189, Gen2 106. Correctness, real suspension and final delegate/owner collection checks passed.

| Compiler inclusive stack | Samples / 544 | Sample share | Weighted allocation bytes | Allocation share |
| --- | ---: | ---: | ---: | ---: |
| TryBuildStatementOccurrences | 523 | 96.14% | 3,053,661,520 | 98.55% |
| TryPrepareStatementBodyForEmission | 13 | 2.39% | 16,598,336 | 0.54% |
| StatementBodyRewriteProvenance.TryValidate | 9 | 1.65% | 14,406,328 | 0.46% |
| StatementBodyRewriteProvenance.TryProjectRewrite | 6 | 1.10% | 5,815,792 | 0.19% |

The three preparation/provenance rows overlap. Do not add them.

Top compiler allocation types: MerklePathSegment[] 824,414,208 bytes; (int, MerkleCursor)[] 509,943,224; MerkleCursor 497,511,024; path-to-cursor dictionary entry arrays 428,664,504; MerklePath 252,909,904; path Skip/Take iterator objects 248,096,976. These are weighted allocation estimates.

## Reproducibility and limits

The harness adds EventSource phase markers, not compiler implementation instrumentation. Warm-up and forced collection/lifetime checks are outside measured generation intervals. Tiered compilation, ReadyToRun, server GC and GC stress are disabled consistently with the prior experiment.

EventPipe providers:
Microsoft-Windows-DotNETRuntime:40000019:5,Microsoft-DotNETCore-SampleProfiler:0:5,Pipeline-Profile:ffffffffffffffff:5

DOTNET_EventPipeCircularMB=100 uses hexadecimal notation (256 MiB buffer limit). [EventPipe configuration](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/eventpipe).

TraceEvent 3.1.28 analysis recorded zero lost events. All 544 compiler-thread samples, 89 bridge-thread samples, 27,896 compiler allocation ticks and 2,449 bridge allocation ticks have resolved stacks. Weighted compiler allocation was 3,098,535,968 bytes, close to the exact current-thread counter. Allocation ticks are samples weighted by AllocationAmount64, not an event for every object or a retained-heap profile.

Sample counts include managed stacks observed at GC polling/wait points. They are not exact exclusive CPU milliseconds. Background-thread stacks are excluded from these main-thread percentages; inclusive percentages overlap. Only one frontend sample was collected, so no detailed frontend attribution is justified. Profiling perturbs timings: do not compare this one 7.18-second compiler phase directly with the prior unprofiled 5.22-second median to infer a regression.

Raw trace and full stack aggregates are preserved at:
D:\repos\runtime-async-dynamicmethod\artifacts\experiments\pipeline-profile\20260915-193444

Trace: merkle-s192-n1-r1.nettrace
Bytes: 32,670,090
SHA-256: 166acd55dc253b375e9f64580bbba50e57d56fc30b92bbecf36e5f6dd1a680fe

The preserved folder contains full .profile.json and .profile.stacks.json reports, compact tables, original worker results, payload hashes and the trace manifest. The tracked [profile-results.json](profile-results.json) contains compact findings plus the complete capture metadata. Harness and analyzer commands are in [README.md](README.md).

Source line numbers are from inspection and can shift as the user's WIP advances. The captured binary hashes, not the current source HEAD, identify the measured build.
