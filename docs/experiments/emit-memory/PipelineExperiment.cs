// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MpsAgent.Merkle.Bridge.Symbols;
using MerkleV2.Compiler;
using MerkleV2.Nodes.Declarations;
using MerkleV2.Symbols;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MpsAgent.Merkle.Bridge;

public sealed class PipelineGlobals
{
    public Task<int> Input = Task.FromResult(41);
}

public static class PipelineExperiment
{
    private static readonly Process CurrentProcess = Process.GetCurrentProcess();
    private static MetadataReference[] References = Array.Empty<MetadataReference>();
    private static string Mode = "";
    private static int Statements;
    private static int Serial;
    private static byte[]? PrebuiltPe;
    private static string? PrebuiltScriptType;
    private static readonly Dictionary<string, Phase> Phases = new();
    private static readonly List<object> Snapshots = new();
    private static long PeBytes;
    private static int LoadedTypes;
    private static int RuntimeAsyncMethods;

    private sealed class Phase
    {
        public double Milliseconds { get; set; }
        public long ThreadAllocatedBytes { get; set; }
    }

    private sealed class State
    {
        public readonly Func<PipelineGlobals, Task<int>>?[] Delegates;
        public readonly Task<int>?[] Pending;
        public readonly AssemblyLoadContext?[] Contexts;
        public readonly WeakReference[] DelegateRefs;
        public readonly WeakReference[] OwnerRefs;
        public readonly WeakReference[] ContextRefs;
        public readonly string[] Sources;
        public readonly int SaltStart;
        public TaskCompletionSource<int>? Completion;

        public State(int count, int saltStart)
        {
            SaltStart = saltStart;
            Delegates = new Func<PipelineGlobals, Task<int>>?[count];
            Pending = new Task<int>?[count];
            Contexts = new AssemblyLoadContext?[count];
            DelegateRefs = new WeakReference[count];
            OwnerRefs = new WeakReference[count];
            ContextRefs = new WeakReference[count];
            Sources = new string[count];
            for (int i = 0; i < count; i++)
            {
                DelegateRefs[i] = new WeakReference(null);
                OwnerRefs[i] = new WeakReference(null);
                ContextRefs[i] = new WeakReference(null);
                Sources[i] = Source(Mode == "alc-load" ? 0 : saltStart + i);
            }
        }
    }

    public static int Main(string[] args)
    {
        try
        {
            Mode = args[0];
            int count = int.Parse(args[1]);
            Statements = int.Parse(args[2]);
            if (!new[] { "merkle", "roslyn-classic", "roslyn-runtime", "alc-load", "alc-empty", "diagnostics" }.Contains(Mode)
                || count < 1 || count > 2000 || Statements < 0 || Statements > 384 || Statements % 6 != 0)
                throw new ArgumentException("Invalid mode, count, or statement count.");
            ThreadPool.SetMinThreads(1, 1);
            ThreadPool.SetMaxThreads(1, 1);
            if (Mode != "alc-empty") SetupReferences();
            if (Mode == "diagnostics") { MeasureDiagnostics(count); return 0; }
            if (Mode == "alc-load")
            {
                var compilation = Frontend(Source(0));
                PrebuiltScriptType = compilation.ScriptClass!.MetadataName;
                PrebuiltPe = EmitPe(compilation);
            }
            WarmUp();
            var state = new State(count, 1000);
            Snapshot("measurement-warmup", state);
            Snapshots.Clear();
            Phases.Clear();
            PeBytes = 0;
            LoadedTypes = 0;
            RuntimeAsyncMethods = 0;
            Collect();
            Snapshot("baseline", state);
            int[] gcBefore = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
            long allocated = GC.GetTotalAllocatedBytes(true);
            long start = Stopwatch.GetTimestamp();
            Generate(state);
            double generationMs = Elapsed(start);
            long generationAllocated = GC.GetTotalAllocatedBytes(true) - allocated;
            int[] generationCollections = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - gcBefore[i]).ToArray();
            Collect();
            Snapshot("generated", state);
            double firstInvokeMs = 0;
            if (Mode != "alc-empty")
            {
                start = Stopwatch.GetTimestamp();
                Invoke(state, 41);
                firstInvokeMs = Elapsed(start);
                Collect();
                Snapshot("jitted", state);
                Suspend(state);
                Collect();
                Snapshot("suspended", state);
                DropDelegatesAndUnload(state);
                Collect();
                Snapshot("delegates-released-pending", state);
                Finish(state);
            }
            else DropDelegatesAndUnload(state);
            Collect();
            Snapshot("released", state);
            int delegateAlive = Alive(state.DelegateRefs);
            int ownerAlive = Alive(state.OwnerRefs);
            int contextAlive = Alive(state.ContextRefs);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                mode = Mode, count, statements = Statements,
                sourceLines = state.Sources[0].Split('\n').Length,
                generationMs, generationAllocatedBytes = generationAllocated, firstInvokeMs, generationCollections,
                phases = Phases, snapshots = Snapshots, peBytes = PeBytes,
                loadedTypeCount = LoadedTypes, runtimeAsyncMethodCount = RuntimeAsyncMethods,
                delegateAlive, ownerAlive, contextAlive,
                runtime = RuntimeInformation.FrameworkDescription,
                coreLibMvid = typeof(object).Module.ModuleVersionId,
                roslyn = typeof(CSharpCompilation).Assembly.GetName().Version!.ToString(),
                serverGC = System.Runtime.GCSettings.IsServerGC,
                compilerOptimizationsDisabled = typeof(MerkleDelegateCompiler).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled
            }));
            if (delegateAlive != 0 || ownerAlive != 0 || contextAlive != 0)
                throw new InvalidOperationException($"Objects remain reachable after release: delegates={delegateAlive}, owners={ownerAlive}, contexts={contextAlive}.");
            GC.KeepAlive(state);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
    }

    private static void SetupReferences()
    {
        string root = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        References = new[] { "System.Private.CoreLib", "System.Runtime", "System.Threading.Tasks", "System.Collections" }
            .Select(name => MetadataReference.CreateFromFile(Path.Combine(root, name + ".dll")))
            .Append(MetadataReference.CreateFromFile(typeof(PipelineGlobals).Assembly.Location)).ToArray();
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Preview, kind: SourceCodeKind.Script);
        if (Mode == "roslyn-runtime")
            parse = parse.WithFeatures(new[] { new KeyValuePair<string, string>("runtime-async", "on") });
        var tree = CSharpSyntaxTree.ParseText(source, parse);
        var compilation = CSharpCompilation.CreateScriptCompilation("PipelineScript" + ++Serial, tree, References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release, concurrentBuild: false),
            returnType: typeof(int), globalsType: typeof(PipelineGlobals));
        return compilation;
    }

    private static CSharpCompilation Frontend(string source)
    {
        var compilation = CreateCompilation(source);
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length != 0) throw new InvalidOperationException(string.Join("\n", errors.Select(d => d.ToString())));
        return compilation;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Generate(State state)
    {
        for (int i = 0; i < state.Delegates.Length; i++) GenerateOne(state, i);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void GenerateOne(State state, int index)
    {
        if (Mode == "alc-empty")
        {
            var context = new AssemblyLoadContext("Empty" + ++Serial, isCollectible: true);
            state.Contexts[index] = context;
            state.ContextRefs[index].Target = context;
            return;
        }
        Func<PipelineGlobals, Task<int>> method;
        object owner;
        if (Mode == "alc-load")
            (method, owner) = LoadPe(state, index, PrebuiltPe!, PrebuiltScriptType!);
        else
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            var compilation = Frontend(state.Sources[index]);
            AddPhase("frontend", start, bytes);
            if (Mode == "merkle")
            {
                bytes = GC.GetAllocatedBytesForCurrentThread();
                start = Stopwatch.GetTimestamp();
                var root = RoslynScriptBridgeService.BuildRoot(compilation, compilation.SyntaxTrees.Single(),
                    new MerkleTypeBuilder(AssemblyLoadContext.Default, compilation, typeof(PipelineGlobals)), MerkleTypeRuntime.From(typeof(int)));
                var unit = root.Value as MerkleScriptUnit ?? throw new InvalidOperationException("Expected script IR.");
                AddPhase("bridge", start, bytes);
                bytes = GC.GetAllocatedBytesForCurrentThread();
                start = Stopwatch.GetTimestamp();
                var result = MerkleDelegateCompiler.CompileInstanceScriptUnit(unit, Array.Empty<string?>(),
                    typeof(Func<PipelineGlobals, Task<int>>), isAsyncBody: true);
                if (result.CompiledDelegate is not Func<PipelineGlobals, Task<int>> compiled)
                    throw new InvalidOperationException(string.Join("\n", result.Diagnostics.Select(d => d.ToString())));
                method = compiled;
                AddPhase("merkleCompile", start, bytes);
                if (method.Method.DeclaringType != null) throw new InvalidOperationException("Expected DynamicMethod.");
                owner = method.Method;
                if ((method.Method.GetMethodImplementationFlags() & MethodImplAttributes.Async) != 0) RuntimeAsyncMethods++;
            }
            else
            {
                bytes = GC.GetAllocatedBytesForCurrentThread();
                start = Stopwatch.GetTimestamp();
                byte[] pe = EmitPe(compilation);
                AddPhase("roslynEmit", start, bytes);
                PeBytes += pe.Length;
                (method, owner) = LoadPe(state, index, pe, compilation.ScriptClass!.MetadataName);
            }
        }
        state.Delegates[index] = method;
        state.DelegateRefs[index].Target = method;
        state.OwnerRefs[index].Target = owner;
    }

    private static byte[] EmitPe(CSharpCompilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success) throw new InvalidOperationException(string.Join("\n", result.Diagnostics));
        return stream.ToArray();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Func<PipelineGlobals, Task<int>>, object) LoadPe(State state, int index, byte[] pe, string typeName)
    {
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        var context = new AssemblyLoadContext("Loaded" + ++Serial, isCollectible: true);
        state.Contexts[index] = context;
        state.ContextRefs[index].Target = context;
        using var stream = new MemoryStream(pe, writable: false);
        var assembly = context.LoadFromStream(stream);
        var type = assembly.GetType(typeName, throwOnError: true)!;
        var entry = type.GetMethod("<Factory>", BindingFlags.Public | BindingFlags.Static)!;
        var factory = entry.CreateDelegate<Func<object[], Task<int>>>();
        Func<PipelineGlobals, Task<int>> adapter = globals => factory(new object[] { globals, null! });
        AddPhase("loadAndBind", start, bytes);
        // Diagnostic enumeration is outside load timing, but included in total generation time.
        var types = assembly.GetTypes();
        LoadedTypes += types.Length;
        foreach (var t in types)
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                if ((m.GetMethodImplementationFlags() & MethodImplAttributes.Async) != 0) RuntimeAsyncMethods++;
        if (!assembly.IsCollectible) throw new InvalidOperationException("Expected collectible assembly.");
        return (adapter, type);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WarmUp()
    {
        var state = new State(Statements >= 192 ? 2 : 8, -100);
        Generate(state);
        if (Mode != "alc-empty")
        {
            foreach (int input in new[] { 41, 0, -7, int.MaxValue, int.MinValue }) Invoke(state, input);
            Suspend(state);
            DropDelegatesAndUnload(state);
            Collect();
            Finish(state);
        }
        else DropDelegatesAndUnload(state);
        Collect();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Invoke(State state, int input)
    {
        var globals = new PipelineGlobals { Input = Task.FromResult(input) };
        for (int i = 0; i < state.Delegates.Length; i++)
            if (state.Delegates[i]!(globals).GetAwaiter().GetResult() != Expected(input, Mode == "alc-load" ? 0 : state.SaltStart + i))
                throw new InvalidOperationException("Wrong arithmetic result.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Suspend(State state)
    {
        state.Completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var globals = new PipelineGlobals { Input = state.Completion.Task };
        for (int i = 0; i < state.Pending.Length; i++)
        {
            var pending = state.Delegates[i]!(globals);
            if (pending.IsCompleted) throw new InvalidOperationException("Expected actual suspension.");
            state.Pending[i] = pending;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DropDelegatesAndUnload(State state)
    {
        Array.Clear(state.Delegates);
        foreach (var context in state.Contexts) context?.Unload();
        Array.Clear(state.Contexts);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Finish(State state)
    {
        state.Completion!.SetResult(41);
        for (int i = 0; i < state.Pending.Length; i++)
            if (state.Pending[i]!.GetAwaiter().GetResult() != Expected(41, Mode == "alc-load" ? 0 : state.SaltStart + i))
                throw new InvalidOperationException("Wrong resumed result.");
        Array.Clear(state.Pending);
        state.Completion = null;
    }

    private static string Source(int salt)
    {
        var text = new StringBuilder("{\nint value = await Input;\n");
        text.AppendLine($"value = unchecked(value + ({salt}));");
        for (int i = 0; i < Statements; i++)
        {
            if (i == Statements / 2) text.AppendLine("value ^= await Input;");
            text.AppendLine((i % 3) switch
            {
                0 => $"value = unchecked(value * 1664525 + {1013904223 + i});",
                1 => $"value ^= (int)((uint)value >> {i % 13 + 1});",
                _ => $"if ((value & 1) != 0) value = unchecked(value + {17 + i}); else value ^= {97 + i};"
            });
        }
        text.AppendLine("return value;");
        text.AppendLine("}");
        return text.ToString();
    }

    private static int Expected(int input, int salt)
    {
        int value = unchecked(input + salt);
        for (int i = 0; i < Statements; i++)
        {
            if (i == Statements / 2) value ^= input;
            unchecked
            {
                if (i % 3 == 0) value = value * 1664525 + 1013904223 + i;
                else if (i % 3 == 1) value ^= (int)((uint)value >> (i % 13 + 1));
                else if ((value & 1) != 0) value += 17 + i;
                else value ^= 97 + i;
            }
        }
        return value;
    }


    private static void MeasureDiagnostics(int count)
    {
        for (int i = 0; i < 8; i++) Frontend(Source(-100 - i)).GetDiagnostics();
        Phases.Clear();
        var sources = Enumerable.Range(1000, count).Select(Source).ToArray();
        Collect();
        for (int i = 0; i < count; i++)
        {
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            var compilation = CreateCompilation(sources[i]);
            AddPhase("parseAndCreate", start, bytes);
            bytes = GC.GetAllocatedBytesForCurrentThread();
            start = Stopwatch.GetTimestamp();
            var first = compilation.GetDiagnostics();
            AddPhase("firstDiagnostics", start, bytes);
            bytes = GC.GetAllocatedBytesForCurrentThread();
            start = Stopwatch.GetTimestamp();
            var second = compilation.GetDiagnostics();
            AddPhase("repeatDiagnostics", start, bytes);
            if (first.Any(d => d.Severity == DiagnosticSeverity.Error) || second.Any(d => d.Severity == DiagnosticSeverity.Error))
                throw new InvalidOperationException("Diagnostics probe source was invalid.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new { mode = Mode, count, statements = Statements, phases = Phases }));
    }

    private static double Elapsed(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
    private static void AddPhase(string name, long start, long allocated)
    {
        double elapsed = Elapsed(start);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (!Phases.TryGetValue(name, out var phase)) Phases[name] = phase = new Phase();
        phase.Milliseconds += elapsed;
        phase.ThreadAllocatedBytes += bytes;
    }
    private static int Alive(WeakReference[] references) => references.Count(reference => reference.IsAlive);
    private static void Collect()
    {
        for (int i = 0; i < 8; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            Thread.Sleep(20);
        }
    }
    private static void Snapshot(string stage, State state)
    {
        long live = GC.GetTotalMemory(false);
        var info = GC.GetGCMemoryInfo();
        CurrentProcess.Refresh();
        Snapshots.Add(new
        {
            stage, managedLiveBytes = live, privateBytes = CurrentProcess.PrivateMemorySize64,
            workingSetBytes = CurrentProcess.WorkingSet64, threads = CurrentProcess.Threads.Count,
            heapBytes = info.HeapSizeBytes, committedBytes = info.TotalCommittedBytes,
            lohBytes = info.GenerationInfo[3].SizeAfterBytes,
            delegates = Alive(state.DelegateRefs), owners = Alive(state.OwnerRefs), contexts = Alive(state.ContextRefs)
        });
    }
}
