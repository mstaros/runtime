# Async emission memory experiment

This experiment compares the existing patched CoreCLR runtime-async DynamicMethod path with metadata-backed MethodBuilder methods. It does not modify the runtime implementation or implement assemblyless types.

## Run

From this directory, on Windows with the patched Release Core_Root already built:

```powershell
.\run.cmd -RuntimeRoot D:\repos\runtime-async-dynamicmethod -Count 1000 -Repetitions 3
```

The runner compiles directly with the repository SDK compiler against the selected Core_Root assemblies. No restore or runtime rebuild is performed. Each case/repetition runs in a fresh corerun process. Output goes under the experiment checkout's ignored artifacts/experiments/emit-memory directory unless OutputDirectory is supplied.

## Cases

| Case | Hosting | Generated data |
| --- | --- | --- |
| dynamic, batch 1 | Patched DynamicMethod; shared anonymous host | None |
| assembly, batch 1 | One RunAndCollect assembly per method/delegate | Declaring type only |
| assembly, batch 100 | Up to 100 methods per RunAndCollect assembly | One declaring type per assembly |
| assembly-shape, batch 1 | One RunAndCollect assembly per method/delegate | Three instance fields on the declaring type, inspected by an ordinary external callee |

Every method takes Task<int>, awaits it using the runtime-async IL contract, adds one, and returns Task<int>. All method bodies carry MethodImplAttributes.Async | MethodImplAttributes.NoInlining; the latter matches the mandatory DynamicMethod behavior. The shape case additionally constructs its data object and exercises GetType, GetFields, FieldInfo.GetValue and SetValue. Its execution time is not an equal-work comparison with the other cases.

## Measurement protocol

- Warm up the selected emission path with 16 methods and complete real suspending calls.
- Preallocate the arrays retaining delegates/tasks and the weak-reference observers before baseline.
- Capture six stages: baseline, emitted, jitted, suspended, delegates_released_pending, released.
- Force eight full compacting GC/finalizer cycles before each measured stage, with short pauses for completion.
- Invoke every delegate once with an already completed task to materialize its generated code.
- Suspend every delegate on one incomplete Task<int>, confirm suspension, and release all delegate references.
- Complete the input task without RunContinuationsAsynchronously, verify every result is 42, release task references and collect again. Thread-pool worker and IO limits are fixed at one before warm-up so runtime-async dispatch cannot expand the pool and contaminate release measurements.
- Fail if any tracked generated delegate or owner remains alive after final collection.
- For DynamicMethod, the observed owner is the managed DynamicMethod. For assembly cases, it is the created RuntimeType; references repeat for batched methods. These weak-reference counts are not distinct native allocation counts.
- Verify metadata-backed types are collectible and associated with the default AssemblyLoadContext.

Snapshots include process private bytes, virtual bytes, working set, managed live bytes, GC committed/heap/fragmented bytes, thread counts and weak-reference counts. Emission records process-wide managed allocation bytes and elapsed time separately. Each run records the actual CoreLib MVID, runtime identity and binary hashes. The current source checkout HEAD is context only, not proof of which source produced the prebuilt binaries.

Workstation GC is selected. Tiered compilation, ReadyToRun and GC stress are disabled consistently in each child. Each repetition rotates case order. Source, configuration and runtime binaries should be held fixed when comparing results.

## Interpretation limits

Private bytes include managed heap commitment and native allocations; they are not a direct count of native live objects. Virtual bytes are reserved address space, not committed RAM. Working set is sensitive to paging. Managed allocation counters alone omit native loader/JIT costs.

Forced collection establishes reachability and retained-memory snapshots, not natural GC cadence. Runtime pools and GC segments can retain memory after individual methods die. First-use costs are excluded by warm-up. This is a small synthetic workload, not a general performance guarantee.

RunAndCollect amortizes fixed costs when methods are batched, but all types/methods in an assembly share its collection boundary. The shape case reuses the otherwise-required declaring type as the shape; it does not measure an additional independent type per method. Ordinary data instance memory is transient here.

Measured results and conclusions are in [RESULTS.md](RESULTS.md); the complete final dataset is [results.json](results.json).

## Larger async bodies

Run the size sweep with the same runtime binaries:

```powershell
.\run.cmd -RuntimeRoot D:\repos\runtime-async-dynamicmethod -Count 1000 -Repetitions 3 -SizeSweep
```

This compares only DynamicMethod and one RunAndCollect assembly per method. The original tiny body is a control. Larger bodies have 24, 48, 96 or 192 unrolled arithmetic/conditional statements, plus two await statements and a return. Each group of three statements contains multiplication/addition, XOR/shift and one conditional. Constants vary with statement index. The input-dependent computation is returned, so it cannot be discarded as unused work. No source compiler runs inside the measurement.

The 24-statement body is equivalent to:

```csharp
async Task<int> Run(Task<int> input)
{
    int value = await input;
    value = unchecked(value * 1664525 + 1013904223);
    value ^= (int)((uint)value >> 2);
    if ((value & 1) != 0) value = unchecked(value + 19); else value ^= 99;
    value = unchecked(value * 1664525 + 1013904226);
    value ^= (int)((uint)value >> 5);
    if ((value & 1) != 0) value = unchecked(value + 22); else value ^= 102;
    value = unchecked(value * 1664525 + 1013904229);
    value ^= (int)((uint)value >> 8);
    if ((value & 1) != 0) value = unchecked(value + 25); else value ^= 105;
    value = unchecked(value * 1664525 + 1013904232);
    value ^= (int)((uint)value >> 11);
    if ((value & 1) != 0) value = unchecked(value + 28); else value ^= 108;
    value ^= await input;
    value = unchecked(value * 1664525 + 1013904235);
    value ^= (int)((uint)value >> 1);
    if ((value & 1) != 0) value = unchecked(value + 31); else value ^= 111;
    value = unchecked(value * 1664525 + 1013904238);
    value ^= (int)((uint)value >> 4);
    if ((value & 1) != 0) value = unchecked(value + 34); else value ^= 114;
    value = unchecked(value * 1664525 + 1013904241);
    value ^= (int)((uint)value >> 7);
    if ((value & 1) != 0) value = unchecked(value + 37); else value ^= 117;
    value = unchecked(value * 1664525 + 1013904244);
    value ^= (int)((uint)value >> 10);
    if ((value & 1) != 0) value = unchecked(value + 40); else value ^= 120;
    return value;
}
```

The two awaits use the same input task: the lifecycle check forces the first to suspend, then the second observes the completed task. Await-site count is held at two across the larger cases; this sweep does not measure scaling with the number of suspensions. Each method uses one int local. During warm-up, results are checked against a separate C# arithmetic evaluator for 41, 0, -7, int.MaxValue and int.MinValue. The larger-body results replace the fixed expected value 42.

Each snapshot now also records emitted IL bytes, body statement count, await-site count, and LOH size/fragmentation after GC. Source hashes are recorded in the output JSON. These LOH snapshots cannot detect temporary LOH allocations that died before measurement. As before, forced collections and cleanup are outside the emission/first-execution timers; this is a retained-memory and startup-size experiment, not a sustained churn throughput benchmark.

The original results remain in RESULTS.md and results.json. The size-sweep results are in [SIZE_RESULTS.md](SIZE_RESULTS.md) and [size-results.json](size-results.json).
