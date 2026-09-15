// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

public static class MemoryExperiment
{
    private static readonly MethodInfo s_await = FindAwait();
    private static readonly Task<int> s_completed = Task.FromResult(41);
    private static readonly Process s_process = Process.GetCurrentProcess();
    private static int s_assemblyNumber;

    private sealed class State
    {
        public readonly Func<Task<int>, Task<int>>?[] Delegates;
        public readonly Task<int>?[] Pending;
        public readonly WeakReference[] DelegateReferences;
        public readonly WeakReference[] OwnerReferences;
        public TaskCompletionSource<int>? Source;

        public State(int count)
        {
            Delegates = new Func<Task<int>, Task<int>>?[count];
            Pending = new Task<int>?[count];
            DelegateReferences = new WeakReference[count];
            OwnerReferences = new WeakReference[count];
            for (int i = 0; i < count; i++)
            {
                DelegateReferences[i] = new WeakReference(null);
                OwnerReferences[i] = new WeakReference(null);
            }
        }
    }

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length != 3)
                throw new ArgumentException("Expected: dynamic|assembly|assembly-shape count batchSize");
            string mode = args[0];
            int count = int.Parse(args[1], CultureInfo.InvariantCulture);
            int batch = int.Parse(args[2], CultureInfo.InvariantCulture);
            if ((mode != "dynamic" && mode != "assembly" && mode != "assembly-shape") ||
                count < 1 || count > 4096 || batch < 1 || batch > count)
                throw new ArgumentException("Invalid experiment parameters.");

            if (!ThreadPool.SetMinThreads(1, 1) || !ThreadPool.SetMaxThreads(1, 1))
                throw new InvalidOperationException("Could not fix the experiment thread-pool limits.");

            Console.Error.WriteLine($"Runtime={RuntimeInformation.FrameworkDescription}; Architecture={RuntimeInformation.ProcessArchitecture}; CoreLibMvid={typeof(object).Module.ModuleVersionId}; CoreLib={typeof(object).Assembly.Location}; ServerGC={System.Runtime.GCSettings.IsServerGC}; MaxWorkerThreads=1");
            // Use every measured path before baseline, including real suspension and resumption.
            WarmUp(mode, Math.Min(batch, 16));
            var state = new State(count);
            Snapshot("warmup", state, 0, 0); // Warm measurement, formatting, and console paths.
            Collect();
            Snapshot("baseline", state, 0, 0);

            long allocated = GC.GetTotalAllocatedBytes(true);
            var watch = Stopwatch.StartNew();
            Generate(state, mode, batch);
            double emitMs = watch.Elapsed.TotalMilliseconds;
            long emitAllocated = GC.GetTotalAllocatedBytes(true) - allocated;
            Collect();
            Snapshot("emitted", state, emitMs, emitAllocated);

            watch.Restart();
            InvokeCompleted(state);
            double jitMs = watch.Elapsed.TotalMilliseconds;
            Collect();
            Snapshot("jitted", state, jitMs, 0);

            watch.Restart();
            Suspend(state);
            double suspendMs = watch.Elapsed.TotalMilliseconds;
            Collect();
            Snapshot("suspended", state, suspendMs, 0);

            Array.Clear(state.Delegates);
            Collect();
            Snapshot("delegates_released_pending", state, 0, 0);

            CompleteAndRelease(state);
            Collect();
            Snapshot("released", state, 0, 0);
            if (Alive(state.DelegateReferences) != 0 || Alive(state.OwnerReferences) != 0)
                throw new InvalidOperationException("Generated delegates or owners remain alive after release.");
            GC.KeepAlive(state);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WarmUp(string mode, int batch)
    {
        var state = new State(16);
        Generate(state, mode, batch);
        InvokeCompleted(state);
        Suspend(state);
        Array.Clear(state.Delegates);
        Collect();
        CompleteAndRelease(state);
        Collect();
        if (Alive(state.OwnerReferences) != 0)
            throw new InvalidOperationException("Warm-up owners did not become collectible.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Generate(State state, string mode, int batch)
    {
        if (mode == "dynamic")
        {
            for (int i = 0; i < state.Delegates.Length; i++)
            {
                // Shared anonymous host; no per-method assembly or collectible allocator.
                var method = new DynamicMethod("M" + i, typeof(Task<int>), new[] { typeof(Task<int>) });
                method.SetImplementationFlags(MethodImplAttributes.Async | MethodImplAttributes.NoInlining);
                Emit(method.GetILGenerator(), null, null, null);
                Store(state, i, method.CreateDelegate<Func<Task<int>, Task<int>>>(), method);
            }
            return;
        }

        for (int start = 0; start < state.Delegates.Length; start += batch)
        {
            int end = Math.Min(start + batch, state.Delegates.Length);
            var assembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("MemoryAssembly" + Interlocked.Increment(ref s_assemblyNumber)),
                AssemblyBuilderAccess.RunAndCollect);
            var module = assembly.DefineDynamicModule("MemoryModule");
            var type = module.DefineType("Container", TypeAttributes.NotPublic | TypeAttributes.Sealed);
            ConstructorBuilder? constructor = null;
            FieldBuilder? number = null;
            FieldBuilder? text = null;
            if (mode == "assembly-shape")
            {
                constructor = type.DefineDefaultConstructor(MethodAttributes.Public);
                number = type.DefineField("Number", typeof(int), FieldAttributes.Public);
                text = type.DefineField("Text", typeof(string), FieldAttributes.Public);
                type.DefineField("Reference", typeof(object), FieldAttributes.Public);
            }
            for (int i = start; i < end; i++)
            {
                var method = type.DefineMethod("M" + i, MethodAttributes.Public | MethodAttributes.Static,
                    typeof(Task<int>), new[] { typeof(Task<int>) });
                method.SetImplementationFlags(MethodImplAttributes.Async | MethodImplAttributes.NoInlining);
                Emit(method.GetILGenerator(), constructor, number, text);
            }
            Type created = type.CreateType()!;
            if (!created.IsCollectible || AssemblyLoadContext.GetLoadContext(created.Assembly) != AssemblyLoadContext.Default)
                throw new InvalidOperationException("Expected a collectible assembly in the default ALC.");
            for (int i = start; i < end; i++)
            {
                var method = created.GetMethod("M" + i)!;
                Store(state, i, method.CreateDelegate<Func<Task<int>, Task<int>>>(), created);
            }
        }
    }

    private static void Store(State state, int index, Func<Task<int>, Task<int>> method, object owner)
    {
        state.Delegates[index] = method;
        state.DelegateReferences[index].Target = method;
        state.OwnerReferences[index].Target = owner;
    }

    private static void Emit(ILGenerator il, ConstructorBuilder? constructor, FieldBuilder? number, FieldBuilder? text)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, s_await);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        if (constructor != null)
        {
            il.Emit(OpCodes.Newobj, constructor);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4_7);
            il.Emit(OpCodes.Stfld, number!);
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldstr, "shape");
            il.Emit(OpCodes.Stfld, text!);
            il.Emit(OpCodes.Call, typeof(MemoryExperiment).GetMethod(nameof(InspectShape))!);
        }
        il.Emit(OpCodes.Ret);
    }

    // An ordinary callee outside the generated assembly observes the actual generated type.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void InspectShape(object value)
    {
        Type type = value.GetType();
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
        if (fields.Length != 3 || (int)type.GetField("Number")!.GetValue(value)! != 7 ||
            (string?)type.GetField("Text")!.GetValue(value) != "shape")
            throw new InvalidOperationException("Generated shape reflection failed.");
        type.GetField("Number")!.SetValue(value, 8);
        if ((int)type.GetField("Number")!.GetValue(value)! != 8)
            throw new InvalidOperationException("Generated shape reflection write failed.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void InvokeCompleted(State state)
    {
        foreach (var method in state.Delegates)
            if (method!(s_completed).GetAwaiter().GetResult() != 42)
                throw new InvalidOperationException("Completed await result mismatch.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Suspend(State state)
    {
        state.Source = new TaskCompletionSource<int>();
        for (int i = 0; i < state.Delegates.Length; i++)
        {
            state.Pending[i] = state.Delegates[i]!(state.Source.Task);
            if (state.Pending[i]!.IsCompleted)
                throw new InvalidOperationException("Expected an actually suspended async call.");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CompleteAndRelease(State state)
    {
        state.Source!.SetResult(41);
        foreach (var pending in state.Pending)
            if (pending!.GetAwaiter().GetResult() != 42)
                throw new InvalidOperationException("Resumed await result mismatch.");
        Array.Clear(state.Pending);
        Array.Clear(state.Delegates);
        state.Source = null;
    }

    private static MethodInfo FindAwait()
    {
        Type helpers = typeof(object).Assembly.GetType("System.Runtime.CompilerServices.AsyncHelpers", true)!;
        foreach (var method in helpers.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            var parameters = method.GetParameters();
            if (method.Name == "Await" && method.IsGenericMethodDefinition &&
                parameters.Length == 1 && parameters[0].ParameterType.IsGenericType &&
                parameters[0].ParameterType.GetGenericTypeDefinition() == typeof(Task<>))
                return method.MakeGenericMethod(typeof(int));
        }
        throw new NotSupportedException("The selected Core_Root lacks runtime async.");
    }

    private static void Collect()
    {
        for (int i = 0; i < 8; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            Thread.Sleep(20);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Alive(WeakReference[] references)
    {
        int count = 0;
        foreach (var reference in references)
            if (reference.IsAlive)
                count++;
        return count;
    }

    private static void Snapshot(string stage, State state, double phaseMs, long emitAllocated)
    {
        long managed = GC.GetTotalMemory(false);
        var gc = GC.GetGCMemoryInfo();
        s_process.Refresh();
        long privateBytes = s_process.PrivateMemorySize64;
        long virtualBytes = s_process.VirtualMemorySize64;
        long workingSet = s_process.WorkingSet64;
        int threads = s_process.Threads.Count;
        if (stage == "warmup")
            Console.WriteLine("stage\tprivateBytes\tvirtualBytes\tworkingSetBytes\tmanagedLiveBytes\tgcCommittedBytes\tgcHeapBytes\tgcFragmentedBytes\taliveDelegates\taliveOwnerReferences\tphaseMs\temitAllocatedBytes\tthreadCount");
        Console.WriteLine(string.Join("\t", stage, privateBytes, virtualBytes, workingSet, managed,
            gc.TotalCommittedBytes, gc.HeapSizeBytes, gc.FragmentedBytes,
            Alive(state.DelegateReferences), Alive(state.OwnerReferences),
            phaseMs.ToString("F3", CultureInfo.InvariantCulture), emitAllocated, threads));
        GC.KeepAlive(state.Delegates);
        GC.KeepAlive(state.Pending);
    }
}
