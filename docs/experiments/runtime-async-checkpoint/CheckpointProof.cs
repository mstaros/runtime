// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

// The emitted workers and the metadata initializer fixture suspend with runtime-async IL.
public static class CheckpointProof
{
    private static readonly Type Helpers = typeof(RuntimeHelpers).Assembly.GetType(
        "System.Runtime.CompilerServices.AsyncHelpers", throwOnError: true)!;
    private static readonly MethodInfo Capture = Helpers.GetMethod(
        "CaptureCheckpointForTesting", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo Restore = Helpers.GetMethod(
        "RestoreCheckpointForTesting", BindingFlags.NonPublic | BindingFlags.Static)!;
    // Standalone file-format bounds, shared by its writer and reader. Native
    // restore independently validates frame semantics and its own layout limits.
    private const int MaxProofFrames = 16;
    private const int MaxProofSlots = 1024;
    private const int MaxProofSchemaIntegers = 11 + 7 * MaxProofSlots;
    private const int MaxProofFileBytes = 1024 * 1024;
    private static bool s_restoring;
    private static int s_prefixCalls;

    public static int Main(string[] args)
    {
        try
        {
            Require(args.Length == 2, "Expected mode and snapshot path.");
            string actualRoot = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            Console.WriteLine("Loaded CoreLib: " + typeof(object).Assembly.Location);
            Require(string.Equals(actualRoot, Environment.GetEnvironmentVariable("CHECKPOINT_PROOF_CORE_ROOT"),
                StringComparison.OrdinalIgnoreCase), "Proof did not load the isolated candidate CoreLib.");
            Require(Capture != null && Restore != null, "Candidate CoreLib hooks are missing.");
            if (args[0] == "produce")
                Produce(args[1]);
            else if (args[0] is "restore" or "config")
                Resume(args[1], args[0] == "config");
            else if (args[0] == "metadata-initializer-produce") ProduceMetadataInitializer(args[1]);
            else if (args[0] == "metadata-initializer-restore") RestoreMetadataInitializer(args[1]);
            else if (args[0].StartsWith("statics-", StringComparison.Ordinal))
                ClassInitializationProof(args[1], args[0].Contains("produce"), args[0].EndsWith("-hot", StringComparison.Ordinal));
            else if (args[0] == "format-limits") FormatLimitsProof(args[1]);
            else if (args[0] is "publication" or "disabled")
                PublicationProof(args[0] == "disabled");
            else
                throw new ArgumentException("Unknown proof mode.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    public static void Prefix(string frame)
    {
        if (s_restoring)
            throw new InvalidOperationException("REPLAY DETECTED before await in " + frame);
        Interlocked.Increment(ref s_prefixCalls);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int ReadLabel(object value) => ((string)value).Length;

    private static DynamicMethod[] CreateProgram()
    {
        var inner = CreateMethod("CheckpointInner");
        EmitBody(inner, null, 2, multiply: true);
        var outer = CreateMethod("CheckpointOuter");
        EmitBody(outer, inner, 3, multiply: false);
        return new[] { inner, outer };
    }

    private static DynamicMethod CreateMethod(string name)
    {
        var method = new DynamicMethod(name, typeof(Task<int>),
            new[] { typeof(ValueTask<int>), typeof(int), typeof(object) },
            typeof(CheckpointProof).Module, skipVisibility: true);
        typeof(DynamicMethod).GetMethod("SetImplementationFlags",
            new[] { typeof(MethodImplAttributes) })!.Invoke(method,
                new object[] { Enum.Parse<MethodImplAttributes>("Async") | MethodImplAttributes.NoInlining });
        return method;
    }

    private static MethodInfo Await(Type taskType) => Helpers.GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == "Await" && m.IsGenericMethodDefinition &&
            m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsGenericType &&
            m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == taskType)
        .MakeGenericMethod(typeof(int));

    private static void EmitBody(DynamicMethod method, DynamicMethod? callee, int constant, bool multiply)
    {
        ILGenerator il = method.GetILGenerator();
        LocalBuilder saved = il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Ldstr, method.Name);
        il.Emit(OpCodes.Call, typeof(CheckpointProof).GetMethod(nameof(Prefix))!);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4, constant);
        il.Emit(multiply ? OpCodes.Mul : OpCodes.Add);
        il.Emit(OpCodes.Stloc, saved);
        il.Emit(OpCodes.Ldarg_0);
        if (callee != null)
        {
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Call, callee);
            il.Emit(OpCodes.Call, Await(typeof(Task<>)));
        }
        else
        {
            il.Emit(OpCodes.Call, Await(typeof(ValueTask<>)));
        }
        il.Emit(OpCodes.Ldloc, saved);
        il.Emit(multiply ? OpCodes.Sub : OpCodes.Mul);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, typeof(CheckpointProof).GetMethod(nameof(ReadLabel))!);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ret);
    }

    private sealed class PendingSource : IValueTaskSource<int>
    {
        public object? CallbackState;
        public Action? Registered;
        public ValueTaskSourceStatus GetStatus(short token) => ValueTaskSourceStatus.Pending;
        public int GetResult(short token) => throw new InvalidOperationException("Source must never complete.");
        public void OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            Require(CallbackState == null, "Source subscribed more than once.");
            CallbackState = state;
            Registered?.Invoke();
        }
    }

    private static Task<int> Start(DynamicMethod[] methods, PendingSource source, object label)
    {
        var invoke = methods[1].CreateDelegate<Func<ValueTask<int>, int, object, Task<int>>>();
        Task<int> result = invoke(new ValueTask<int>(source, 0), 10, label);
        Require(!result.IsCompleted && ReferenceEquals(source.CallbackState, result),
            "Did not reach the controlled, quiescent await.");
        return result;
    }

    private static void Produce(string path)
    {
        DynamicMethod[] methods = CreateProgram();
        var source = new PendingSource();
        Task<int> pending = Start(methods, source, new string("hello".ToCharArray()));
        Require(s_prefixCalls == 2, "Expected two pre-await calls.");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        object[][] records = (object[][])Capture.Invoke(null, new object[] { pending, methods })!;
        Require(records.Length == 2, "Expected exactly two generated continuation frames.");
        Require(records.All(r => ((object?[])r[3]).Any(v => v is int)),
            "Each frame must preserve an integer.");
        Require(records.All(r => ((object?[])r[3]).Any(v => v is string)),
            "Each frame must preserve the label reference.");

        // A real heap object in a live slot must fail explicitly rather than disappear.
        Task<int> unsupported = Start(methods, new PendingSource(), new object());
        ExpectNotSupported(() => Capture.Invoke(null, new object[] { unsupported, methods }),
            "unsupported live object");

        var concurrentMethods = CreateProgram();
        Task.WaitAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            var waiting = Start(concurrentMethods, new PendingSource(), "parallel");
            var captured = (object[][])Capture.Invoke(null, new object[] { waiting, concurrentMethods })!;
            Require(captured.Length == 2, "Concurrent publication lost checkpoint metadata.");
        })).ToArray());
        ProduceReuse(path + ".reuse");
        ProduceScheduling(path + ".pool");
        if (Environment.GetEnvironmentVariable("DOTNET_JitAsyncCheckpointProof") == "2")
            Require(records.All(r => ((int[])r[2])[10] == 1), "Forced JIT retry did not reach the installed metadata.");
        var hostCapture = Helpers.GetMethod("CaptureMethodCheckpointForTesting", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(int));
        var hostRestore = Helpers.GetMethod("RestoreMethodCheckpointWithOutcomeForTesting", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(int));
        int policyCalls = 0, codecCalls = 0;
        Func<object, object> codec = value => { codecCalls++; return value; };
        Action<int[]> rejectSecond = _ => { if (++policyCalls == 2) throw new NotSupportedException("second frame"); };
        ExpectNotSupported(() => hostCapture.Invoke(null, new object?[] { pending, methods, codec, rejectSecond }),
            "late capture rejection", "second frame");
        Require(codecCalls == 0, "The encoder ran before the second frame's policy.");
        object[][] hostRecords = (object[][])hostCapture.Invoke(null, new object?[] { pending, methods, codec, null })!;
        Require(codecCalls > 0, "The negative test needs an encodable first frame.");
        codecCalls = 0;
        policyCalls = 0;
        ExpectNotSupported(() => hostRestore.Invoke(null, new object?[] { hostRecords, methods, Task.FromResult(41), codec, rejectSecond }),
            "late restore rejection", "second frame");
        Require(codecCalls == 0, "The decoder ran before the second frame's policy.");
        ProduceFrameLimit(path + ".frames16");
        Write(path, records);
        Console.WriteLine($"READY producer={Environment.ProcessId} frames={records.Length} bytes={new FileInfo(path).Length}");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite); // The runner forcibly kills this process.
        GC.KeepAlive(pending);
        GC.KeepAlive(source);
        GC.KeepAlive(methods);
    }

    private static void Resume(string path, bool changedConfig)
    {
        object[][] records = Read(path, out int producer);
        Require(producer != Environment.ProcessId, "Restore must run in a different process.");
        // Deliberately perturb runtime descriptor/code allocation before rebuilding the program.
        for (int i = 0; i < 64; i++)
        {
            var noise = new DynamicMethod("Unrelated" + i, typeof(int), Type.EmptyTypes);
            var il = noise.GetILGenerator();
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Ret);
            Require(noise.CreateDelegate<Func<int>>()() == i, "Noise method failed.");
        }
        DynamicMethod[] methods = CreateProgram();
        s_restoring = true;
        if (changedConfig)
        {
            ExpectNotSupported(() => Restore.Invoke(null, new object?[] { records, methods, 41, null }), "effective JIT configuration", "Checkpoint JIT configuration differs.");
            return;
        }
        ExpectNotSupported(() => Restore.Invoke(null, new object?[] { records.Reverse().ToArray(), methods, 41, null }),
            "same-signature frames reversed", "Checkpoint frame order, count or suspension state differs from capture.");
        ExpectNotSupported(() => Restore.Invoke(null, new object?[] { records.Skip(1).ToArray(), methods, 41, null }),
            "same-signature chain truncated", "Checkpoint frame order, count or suspension state differs from capture.");
        var sharedLabels = records.SelectMany(record => ((object?[])record[3]).OfType<string>()).Where(label => label == "hello").ToArray();
        Require(sharedLabels.Length >= 2 && sharedLabels.All(label => ReferenceEquals(label, sharedLabels[0])),
            "The file round trip split the shared label.");
        var swapped = records.Select(r => (object[])r.Clone()).ToArray();
        swapped[0][0] = 1 - (int)swapped[0][0];
        ExpectNotSupported(() => Restore.Invoke(null, new object?[] { swapped, methods, 41, null }), "swapped method index");
        ExpectNotSupported(() => Restore.Invoke(null, new object?[] { records, methods.Reverse().ToArray(), 41, null }), "swapped method table");
        var altered = CreateProgram();
        altered[0] = CreateMethod("CheckpointInner");
        EmitBody(altered[0], null, 7, multiply: true);
        ExpectNotSupported(() => Restore.Invoke(null, new object?[] { records, altered, 41, null }), "changed method IL");

        object[][] changed = records.Select(r => (object[])r.Clone()).ToArray();
        changed[0][2] = ((int[])changed[0][2]).Clone();
        ((int[])changed[0][2])[0]++;
        ExpectNotSupported(() => Restore.Invoke(null, new object?[] { changed, methods, 41, null }),
            "changed schema");
        changed = records.Select(r => (object[])r.Clone()).ToArray();
        changed[0][3] = ((object?[])changed[0][3]).Clone();
        ((object?[])changed[0][3])[0] = new object();
        ExpectNotSupported(() => Restore.Invoke(null, new object?[] { changed, methods, 41, null }),
            "changed value type");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        Task<int> resumed = (Task<int>)Restore.Invoke(null, new object?[] { records, methods, 41, null })!;
        Require(resumed.IsCompleted, "Proof should finish synchronously after result injection.");
        int actual = resumed.GetAwaiter().GetResult();
        Require(actual == 343, $"Expected 343, got {actual}.");
        Require(s_prefixCalls == 0, "Pre-await code was reexecuted.");
        ResumeReuse(path + ".reuse");
        ResumeScheduling(path + ".pool");
        ResumeFrameLimit(path + ".frames16");
        Console.WriteLine($"PASS producer={producer} restorer={Environment.ProcessId} frames={records.Length} result={actual} preAwaitCalls={s_prefixCalls}");
    }


    private static DynamicMethod[] CreateFrameLimitProgram(int count)
    {
        var methods = new DynamicMethod[count];
        for (int i = 0; i < count; i++)
        {
            methods[i] = CreateMethod("CheckpointFrameLimit" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture));
            EmitBody(methods[i], i == 0 ? null : methods[i - 1], 1, multiply: true);
        }
        return methods;
    }

    private static Task<int> StartFrameLimit(DynamicMethod[] methods, PendingSource source)
    {
        int prefixCalls = s_prefixCalls;
        var invoke = methods[^1].CreateDelegate<Func<ValueTask<int>, int, object, Task<int>>>();
        Task<int> pending = invoke(new ValueTask<int>(source, 0), 10, new string("hello".ToCharArray()));
        Require(!pending.IsCompleted && ReferenceEquals(source.CallbackState, pending) &&
            s_prefixCalls - prefixCalls == methods.Length, "Frame-limit program did not park every caller.");
        return pending;
    }

    private static void ProduceFrameLimit(string path)
    {
        var methods = CreateFrameLimitProgram(16);
        var source = new PendingSource();
        Task<int> pending = StartFrameLimit(methods, source);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var records = (object[][])Capture.Invoke(null, new object[] { pending, methods })!;
        Require(records.Length == 16 && records.Select((r, i) => (int)r[0] == i).All(v => v),
            "The exact-limit chain was not captured in order.");
        var hostCapture = Helpers.GetMethod("CaptureMethodCheckpointForTesting", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(int));
        int codecCalls = 0, policyCalls = 0;
        Func<object, object> codec = value => { codecCalls++; return value; };
        Action<int[]> policy = _ => policyCalls++;
        var hostRecords = (object[][])hostCapture.Invoke(null, new object?[] { pending, methods, codec, policy })!;
        Require(hostRecords.Length == 16 && policyCalls == 16 && codecCalls > 0 &&
            hostRecords.All(r => r[4] == null), "Host capture did not admit the context-free exact-limit chain.");

        var tooManyMethods = CreateFrameLimitProgram(17);
        var tooManySource = new PendingSource();
        Task<int> tooMany = StartFrameLimit(tooManyMethods, tooManySource);
        ExpectNotSupported(() => Capture.Invoke(null, new object[] { tooMany, tooManyMethods }),
            "17-frame capture", "Checkpoint proof supports at most 16 frames.");
        codecCalls = 0;
        policyCalls = 0;
        ExpectNotSupported(() => hostCapture.Invoke(null, new object?[] { tooMany, tooManyMethods, codec, policy }),
            "17-frame host capture", "Checkpoint proof supports at most 16 frames.");
        Require(codecCalls == 0, "The encoder ran for an over-limit task node.");
        Write(path, records);
        Console.WriteLine("PASS runtime frame capture limit: accepted=16 rejected=17 rejectedEncoderCalls=0");
        GC.KeepAlive(pending);
        GC.KeepAlive(source);
        GC.KeepAlive(tooMany);
        GC.KeepAlive(tooManySource);
    }

    private static void ResumeFrameLimit(string path)
    {
        var records = Read(path, out int producer);
        Require(producer != Environment.ProcessId && records.Length == 16,
            "The exact-limit chain must come from the killed producer.");
        var methods = CreateFrameLimitProgram(16);
        var hostRestore = Helpers.GetMethod("RestoreMethodCheckpointWithOutcomeForTesting", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(int));
        var hostValidate = Helpers.GetMethod("ValidateMethodCheckpointForTesting", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(int));
        int observed = 0, codecCalls = 0, policyCalls = 0;
        Action<object> observer = first =>
        {
            // The hook supplies the chain head once, before dispatch.
            for (object? current = first; current != null;)
            {
                Require(++observed <= 16, "The restored frame chain is too long or cyclic.");
                FieldInfo? next = null;
                for (Type? type = current.GetType(); type != null && next == null; type = type.BaseType)
                    next = type.GetField("Next", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                Require(next != null, "Restored frame linkage was not found.");
                current = next!.GetValue(current);
            }
        };
        Func<object, object> codec = value => { codecCalls++; return value; };
        Action<int[]> policy = _ => policyCalls++;
        // This fixture captured no ExecutionContext and its codec is identity.
        // Add the host record's context field to the saved standalone records.
        object[][] HostRecords(object[][] frames) => frames.Select(r =>
            new object[] { r[0], r[1], r[2], r[3], null!, r[4], r[5] }).ToArray();
        foreach (object[][] bad in new[] { Array.Empty<object[]>(), records.Concat(new[] { records[^1] }).ToArray() })
        {
            ExpectNotSupported(() => RestoreFrames(bad, methods, 41, observer),
                bad.Length + "-frame restore", "Invalid checkpoint frame count.");
            object[][] hostBad = HostRecords(bad);
            ExpectNotSupported(() => hostRestore.Invoke(null, new object?[] { hostBad, methods, Task.FromResult(41), codec, policy }),
                bad.Length + "-frame host restore", "Invalid checkpoint frame count.");
            ExpectNotSupported(() => hostValidate.Invoke(null, new object?[] { hostBad, methods, Task.FromResult(41), true, policy }),
                bad.Length + "-frame host preflight", "Invalid checkpoint frame count.");
            Require(observed == 0 && codecCalls == 0 && policyCalls == 0,
                "An invalid frame count reached frame allocation, policy or decoding.");
        }

        Task<int> restored = RestoreFrames(records, methods, 41, observer);
        // Each of the 16 callers subtracts 10, then adds the five-character label.
        Require(restored.IsCompleted, "The exact-limit chain did not complete synchronously.");
        int actual = restored.GetAwaiter().GetResult();
        Require(actual == -39 && observed == 16,
            $"The exact-limit chain restored {observed} frames with result {actual}.");
        var hostRecords = HostRecords(records);
        hostValidate.Invoke(null, new object?[] { hostRecords, methods, Task.FromResult(41), true, policy });
        Require(policyCalls == 16 && codecCalls == 0, "Exact-limit host preflight did not visit each frame.");
        policyCalls = 0;
        var hostRestored = (Task<int>)hostRestore.Invoke(null, new object?[] { hostRecords, methods, Task.FromResult(41), codec, policy })!;
        Require(hostRestored.IsCompleted && hostRestored.GetAwaiter().GetResult() == -39 &&
            policyCalls == 16 && codecCalls > 0 && s_prefixCalls == 0,
            "Exact-limit host restore failed, skipped bindings or replayed the prefix.");
        Console.WriteLine($"PASS runtime frame restore limit: producer={producer} restorer={Environment.ProcessId} accepted=16 rejected=0,17 rejectedDecoderCalls=0 rejectedPolicyCalls=0 result=-39 observedFrames={observed} preAwaitCalls={s_prefixCalls}");
    }

    private static PendingSource? s_nextWait;
    public static ValueTask<int> NextWait() => new(s_nextWait ?? throw new Exception("Missing wait"), 0);

    private static DynamicMethod[] CreateReuseProgram()
    {
        DynamicMethod method = CreateMethod("CheckpointReuse");
        var il = method.GetILGenerator();
        var saved = il.DeclareLocal(typeof(int));
        var unchanged = il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Ldstr, "reuse");
        il.Emit(OpCodes.Call, typeof(CheckpointProof).GetMethod(nameof(Prefix))!);
        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stloc, saved);
        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_I4, 7); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, unchanged);
        for (int i = 0; i < 3; i++)
        {
            il.Emit(OpCodes.Call, typeof(CheckpointProof).GetMethod(nameof(NextWait))!);
            il.Emit(OpCodes.Call, Await(typeof(ValueTask<>)));
            il.Emit(OpCodes.Ldloc, saved);
            il.Emit(OpCodes.Ldc_I4, i == 0 ? 100 : i == 1 ? 10 : 3);
            il.Emit(OpCodes.Mul);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, saved);
        }
        il.Emit(OpCodes.Ldloc, saved);
        il.Emit(OpCodes.Ldloc, unchanged); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, typeof(CheckpointProof).GetMethod(nameof(ReadLabel))!);
        il.Emit(OpCodes.Add); il.Emit(OpCodes.Ret);
        // The decoy has only state 0; probing it with state 1 must not throw.
        return new[] { CreateProgram()[0], method };
    }

    private static Task<int> RestoreFrames(object[][] records, DynamicMethod[] methods, int result, Action<object>? observer = null) =>
        (Task<int>)Restore.Invoke(null, new object?[] { records, methods, result, observer })!;

    private static void ProduceReuse(string path)
    {
        var methods = CreateReuseProgram();
        s_nextWait = new PendingSource();
        Task<int> pending = Start(methods, s_nextWait, "hello");
        var first = (object[][])Capture.Invoke(null, new object[] { pending, methods })!;
        using var registered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var next = new PendingSource();
        int registrationTimedOut = 0;
        next.Registered = () =>
        {
            registered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(20))) Interlocked.Exchange(ref registrationTimedOut, 1);
        };
        s_nextWait = next;
        var resuming = Task.Factory.StartNew(() => RestoreFrames(first, methods, 1),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        object[][] records;
        try
        {
            Require(registered.Wait(TimeSpan.FromSeconds(20)), "Second park was never registered.");
            var parked = (Task<int>)next.CallbackState!;
            Require(!parked.IsCompleted, "Expected a quiescent second suspension.");
            records = (object[][])Capture.Invoke(null, new object[] { parked, methods })!;
        }
        finally { release.Set(); }
        Require(ReferenceEquals(resuming.GetAwaiter().GetResult(), next.CallbackState) && registrationTimedOut == 0,
            "The handoff did not capture the registered task while the notifier was held.");
        Require((int)records[0][1] > 0, "Reuse fixture must checkpoint a nonzero state.");
        Require(((object?[])records[0][3]).Any(value => value is int number && number == 17), "Unchanged local was not saved.");
        Write(path, records);
    }

    private static void ResumeReuse(string path)
    {
        object[][] records = Read(path, out int producer);
        Require(producer != Environment.ProcessId, "Reuse restore must be in a fresh process.");
        var methods = CreateReuseProgram();
        var changed = records.Select(r => (object[])r.Clone()).ToArray();
        changed[0][1] = 0;
        ExpectNotSupported(() => RestoreFrames(changed, methods, 2), "swapped await state");
        s_nextWait = new PendingSource();
        object? restoredFrame = null;
        Task<int> third = RestoreFrames(records, methods, 2, frame => restoredFrame = frame);
        Require(!third.IsCompleted, "Nonzero restore must suspend again.");
        object? cursor = typeof(Task).GetField("m_stateObject", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(third);
        bool reused = false;
        while (cursor != null)
        {
            if (ReferenceEquals(cursor, restoredFrame)) { reused = true; break; }
            FieldInfo? next = null;
            for (Type? type = cursor.GetType(); type != null && next == null; type = type.BaseType)
                next = type.GetField("Next", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            cursor = next?.GetValue(cursor);
        }
        Require(reused, "The restored continuation object was not reused.");
        var final = (object[][])Capture.Invoke(null, new object[] { third, methods })!;
        Require((int)final[0][1] > (int)records[0][1], "Third await did not advance state.");
        int result = RestoreFrames(final, methods, 3).GetAwaiter().GetResult();
        Require(result == 30061, "Reuse lost changed or unchanged locals: " + result);
        Console.WriteLine("PASS nonzero-state restore/resuspend reuse=true result=" + result);
    }


    public static void AssertDefaultScheduler()
    {
        Require(TaskScheduler.Current == TaskScheduler.Default, "Restore ignored ContinueOnThreadPool.");
    }

    private static DynamicMethod[] CreateSchedulingProgram()
    {
        DynamicMethod inner = CreateMethod("CheckpointThreadPoolInner");
        var body = inner.GetILGenerator();
        body.Emit(OpCodes.Ldarg_0);
        body.Emit(OpCodes.Call, Await(typeof(ValueTask<>)));
        body.Emit(OpCodes.Ldc_I4_1); body.Emit(OpCodes.Add); body.Emit(OpCodes.Ret);

        DynamicMethod method = CreateMethod("CheckpointThreadPool");
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2);
        // Only a runtime-async call followed by configured Task await gets the
        // compile-time ContinueOnThreadPool flag; an ordinary task wait does not.
        il.Emit(OpCodes.Call, inner);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Call, typeof(Task<int>).GetMethod("ConfigureAwait", new[] { typeof(bool) })!);
        il.Emit(OpCodes.Call, Await(typeof(ConfiguredTaskAwaitable<>)));
        il.Emit(OpCodes.Call, typeof(CheckpointProof).GetMethod(nameof(AssertDefaultScheduler))!);
        il.Emit(OpCodes.Ret);
        return new[] { inner, method };
    }

    private static void ProduceScheduling(string path)
    {
        var methods = CreateSchedulingProgram();
        var source = new PendingSource();
        Task<int> pending = Start(methods, source, "unused");
        var records = (object[][])Capture.Invoke(null, new object[] { pending, methods })!;
        Require(records.Any(record => (((int[])record[2])[2] & 1) != 0), "Fixture did not set ContinueOnThreadPool.");
        Write(path, records);
    }

    private static void ResumeScheduling(string path)
    {
        var records = Read(path, out _);
        var methods = CreateSchedulingProgram();
        var schedulers = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, maxConcurrencyLevel: 1);
        try
        {
            int result = Task.Factory.StartNew(() =>
            {
                Require(TaskScheduler.Current != TaskScheduler.Default, "Scheduling fixture did not enter a custom scheduler.");
                return RestoreFrames(records, methods, 41);
            }, CancellationToken.None, TaskCreationOptions.None, schedulers.ExclusiveScheduler).Unwrap().GetAwaiter().GetResult();
            Require(result == 42, "Scheduled restore changed the result.");
            Console.WriteLine("PASS ContinueOnThreadPool under custom scheduler");
        }
        finally { schedulers.Complete(); }
    }


    private static int s_classInitializations;
    public static class CheckpointStatics
    {
        public static readonly int A;
        public static readonly int B;
        public static readonly object Label;
        static CheckpointStatics()
        {
            A = 17; B = 31; Label = new string("statics".ToCharArray());
            Interlocked.Increment(ref s_classInitializations);
        }
    }

    private static int s_metadataInitializations;
    private static class MetadataInitializer
    {
        internal static readonly int Value;
        static MetadataInitializer()
        {
            Value = 17;
            Interlocked.Increment(ref s_metadataInitializations);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static async Task<int> Run(ValueTask<int> gate)
        {
            Prefix("metadata-initializer");
            return await gate + Value;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<int> HoldWeak(object value, ValueTask<int> gate)
    {
        int result = await gate;
        GC.KeepAlive(value);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task<int> HoldNullable((string Text, int Count)? value, ValueTask<int> gate)
    {
        int result = await gate;
        return result + (value is { } item ? item.Text.Length + item.Count : 0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async Task HoldVoidTask(ValueTask<int> gate) { _ = await gate; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static async ValueTask HoldVoidValueTask(ValueTask<int> gate) { _ = await gate; }

    private static void CheckVoidRoots()
    {
        var capture = Helpers.GetMethod("CaptureVoidMethodCheckpointForTesting", BindingFlags.Static | BindingFlags.NonPublic)!;
        var restore = Helpers.GetMethod("RestoreVoidMethodCheckpointForTesting", BindingFlags.Static | BindingFlags.NonPublic)!;
        Func<object, object> codec = v => v;
        foreach (string name in new[] { nameof(HoldVoidTask), nameof(HoldVoidValueTask) })
        {
            MethodInfo[] methods = { typeof(CheckpointProof).GetMethod(name)! };
            var gate = new ValueTask<int>(new PendingSource(), 0);
            Task pending = name == nameof(HoldVoidTask) ? HoldVoidTask(gate) : HoldVoidValueTask(gate).AsTask();
            var records = (object[][])capture.Invoke(null, new object?[] { pending, methods, codec, null })!;
            var timeout = new TimeoutException();
            foreach (Task<int> outcome in new[] { Task.FromResult(1), Task.FromException<int>(timeout),
                Task.FromCanceled<int>(new CancellationToken(true)) })
            {
                var result = (Task)restore.Invoke(null, new object?[] { records, methods, outcome, codec, null })!;
                try { result.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) when (outcome.IsCanceled) { }
                catch (TimeoutException error) when (ReferenceEquals(error, timeout)) { }
                Require(outcome.Status == result.Status, "Void root changed a committed outcome.");
            }
        }
        Console.WriteLine("PASS Task and ValueTask roots preserve success, timeout and cancellation");
    }

    private static void CheckWeakAndNullableBindings()
    {
        var capture = Helpers.GetMethod("CaptureMethodCheckpointForTesting", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(int));
        var restore = Helpers.GetMethod("RestoreMethodCheckpointWithOutcomeForTesting", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(int));
        var a = new AssemblyLoadContext("checkpoint-a", true);
        var b = new AssemblyLoadContext("checkpoint-b", true);
        try
        {
            Type Payload(AssemblyLoadContext alc) => alc.LoadFromAssemblyPath(typeof(CheckpointProof).Assembly.Location)
                .GetType(typeof(PendingSource).FullName!, true)!;
            Type left = Payload(a), right = Payload(b);
            object saved = RuntimeHelpers.GetUninitializedObject(left);
            object wrong = RuntimeHelpers.GetUninitializedObject(right);
            MethodInfo[] methods = { typeof(CheckpointProof).GetMethod(nameof(HoldWeak))!,
                left.GetMethod(nameof(PendingSource.GetStatus))!, right.GetMethod(nameof(PendingSource.GetStatus))! };
            var pending = HoldWeak(saved, new ValueTask<int>(new PendingSource(), 0));
            int encodes = 0;
            Func<object, object> encode = v => ReferenceEquals(v, saved) ? ++encodes : throw new InvalidOperationException("Unexpected weak-slot binding.");
            var frames = (object[][])capture.Invoke(null, new object?[] { pending, methods, encode, null })!;
            Require(encodes == 1, "Weak slot must be present in the checkpoint.");
            Func<object, object> decode = _ => wrong;
            ExpectNotSupported(() => restore.Invoke(null, new object?[] { frames, methods, Task.FromResult(7), decode, null }),
                "identical weak type from another load", "Checkpoint reference type differs from capture.");
            decode = _ => saved;
            Require(((Task<int>)restore.Invoke(null, new object?[] { frames, methods, Task.FromResult(7), decode, null })!).Result == 7,
                "The registered load failed to restore.");
        }
        finally { a.Unload(); b.Unload(); }

        foreach (var value in new (string Text, int Count)?[] { null, ("abcd", 3) })
        {
            MethodInfo[] methods = { typeof(CheckpointProof).GetMethod(nameof(HoldNullable))! };
            var pending = HoldNullable(value, new ValueTask<int>(new PendingSource(), 0));
            Func<object, object> codec = v => v;
            var records = (object[][])capture.Invoke(null, new object?[] { pending, methods, codec, null })!;
            var result = (Task<int>)restore.Invoke(null, new object?[] { records, methods, Task.FromResult(10), codec, null })!;
            Require(result.GetAwaiter().GetResult() == (value.HasValue ? 17 : 10), "Nullable with references changed across restore.");
        }
        Console.WriteLine("PASS weak load-context identity and nullable values with references");
    }

    private static MethodInfo MetadataInitializerMethod() => typeof(MetadataInitializer).GetMethod(nameof(MetadataInitializer.Run))!;

    private static void ProduceMetadataInitializer(string path)
    {
        MethodInfo[] methods = { MetadataInitializerMethod() };
        var task = MetadataInitializer.Run(new ValueTask<int>(new PendingSource(), 0));
        var capture = Helpers.GetMethod("CaptureMethodCheckpointForTesting", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(int));
        Func<object, object> unexpected = value => throw new InvalidOperationException("Unexpected metadata initializer binding: " + value.GetType());
        var frames = (object[][])capture.Invoke(null, new object?[] { task, methods, unexpected, null })!;
        object[] f = frames.Single();
        Require(f[4] == null && s_metadataInitializations == 1, "Initializer capture must have no context binding.");
        Write(path, new[] { new[] { f[0], f[1], f[2], f[3], f[5], f[6] } });
        CheckWeakAndNullableBindings();
        CheckVoidRoots();
    }

    private static void RestoreMetadataInitializer(string path)
    {
        object[] f = Read(path, out int producer).Single();
        MethodInfo[] methods = { MetadataInitializerMethod() };
        Require(producer != Environment.ProcessId && s_metadataInitializations == 0, "Metadata restore must start cold.");
        var frames = new[] { new object[] { f[0], f[1], f[2], f[3], null!, f[4], f[5] } };
        var restore = Helpers.GetMethod("RestoreMethodCheckpointWithOutcomeForTesting", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(int));
        Func<object, object> unexpected = value => throw new InvalidOperationException("Unexpected metadata initializer payload: " + value);
        s_restoring = true;
        var task = (Task<int>)restore.Invoke(null, new object?[] { frames, methods, Task.FromResult(41), unexpected, null })!;
        Require(task.GetAwaiter().GetResult() == 58 && s_metadataInitializations == 1, "Metadata restore skipped its declaring type initializer.");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int InlineStaticRead() => CheckpointStatics.A * 100 + CheckpointStatics.B;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int CombineStaticTemps(int a, int b, int c, object label, int result) =>
        a - b * (c - result) + ((string)label).Length * 7;

    private static DynamicMethod CreateStaticProgram()
    {
        var method = CreateMethod("CheckpointClassInitialization");
        var il = method.GetILGenerator();
        // Deliberately leave distinct values on the IL evaluation stack over the await.
        // These become JIT temporaries, not named IL locals.
        il.Emit(OpCodes.Ldsfld, typeof(CheckpointStatics).GetField(nameof(CheckpointStatics.A))!);
        il.Emit(OpCodes.Ldc_I4, 1000); il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Ldsfld, typeof(CheckpointStatics).GetField(nameof(CheckpointStatics.B))!);
        il.Emit(OpCodes.Ldc_I4, 10); il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Call, typeof(CheckpointProof).GetMethod(nameof(InlineStaticRead))!);
        il.Emit(OpCodes.Ldsfld, typeof(CheckpointStatics).GetField(nameof(CheckpointStatics.Label))!);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, Await(typeof(ValueTask<>)));
        il.Emit(OpCodes.Call, typeof(CheckpointProof).GetMethod(nameof(CombineStaticTemps))!);
        il.Emit(OpCodes.Ret);
        return method;
    }

    private static void ClassInitializationProof(string path, bool produce, bool hot)
    {
        Require(s_classInitializations == 0, "Class-state fixture must start in a fresh process.");
        if (hot) RuntimeHelpers.RunClassConstructor(typeof(CheckpointStatics).TypeHandle);
        var method = CreateStaticProgram();
        var methods = new[] { method };
        if (produce)
        {
            var invoke = method.CreateDelegate<Func<ValueTask<int>, int, object, Task<int>>>();
            Require(s_classInitializations == (hot ? 1 : 0), "Compilation executed the static constructor.");
            var source = new PendingSource();
            Task<int> pending = invoke(new ValueTask<int>(source, 0), 0, "unused");
            Require(!pending.IsCompleted && s_classInitializations == 1, "Static fixture did not suspend.");
            var records = (object[][])Capture.Invoke(null, new object[] { pending, methods })!;
            var values = (object?[])records.Single()[3];
            Require(new[] { 17000, 310, 1731 }.All(expected => values.Contains(expected)),
                "Distinct evaluation-stack temporaries were not captured.");
            Write(path, records);
        }
        else
        {
            var records = Read(path, out int producer);
            Require(producer != Environment.ProcessId, "Static fixture must restore in a fresh process.");
            int actual = RestoreFrames(records, methods, 41).GetAwaiter().GetResult();
            Require(actual == -506851, "Static temporary meanings changed: " + actual);
            Require(s_classInitializations == (hot ? 1 : 0), "Restore replayed static initialization.");
        }
        Console.WriteLine($"PASS class initialization {(produce ? "capture" : "restore")} {(hot ? "hot" : "cold")}");
    }

    private static DynamicMethod[] WithNonTaskDecoys(DynamicMethod owner)
    {
        var integer = new DynamicMethod("CheckpointIntDecoy", typeof(int), Type.EmptyTypes);
        var il = integer.GetILGenerator(); il.Emit(OpCodes.Ldc_I4, 123); il.Emit(OpCodes.Ret);
        var empty = new DynamicMethod("CheckpointVoidDecoy", typeof(void), Type.EmptyTypes);
        empty.GetILGenerator().Emit(OpCodes.Ret);
        Require(integer.CreateDelegate<Func<int>>()() == 123, "Invalid integer decoy.");
        empty.CreateDelegate<Action>()();
        var noAwait = CreateMethod("CheckpointNoSuspensionDecoy");
        var noAwaitIl = noAwait.GetILGenerator(); noAwaitIl.Emit(OpCodes.Ldc_I4_0); noAwaitIl.Emit(OpCodes.Ret);
        Require(noAwait.CreateDelegate<Func<ValueTask<int>, int, object, Task<int>>>()(
            default, 0, "unused").GetAwaiter().GetResult() == 0, "Invalid no-suspension decoy.");
        return new[] { integer, empty, noAwait, owner };
    }

    private static void PublicationProof(bool disabled)
    {
        var method = CreateMethod("CheckpointPublicationGap");
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, Await(typeof(ValueTask<>)));
        il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ret);
        var methods = WithNonTaskDecoys(method);
        Task<int> StartOne() => method.CreateDelegate<Func<ValueTask<int>, int, object, Task<int>>>()(
            new ValueTask<int>(new PendingSource(), 0), 10, "unused");
        if (disabled)
        {
            var pending = StartOne();
            ExpectNotSupported(() => Capture.Invoke(null, new object[] { pending, methods }), "metadata disabled without publication wait");
            return;
        }
        var query = Helpers.GetMethod("CheckpointPublicationStateForTesting", BindingFlags.Static | BindingFlags.NonPublic)!;
        Require(query != null, "CoreLib publication test hook is missing (check ILLink roots).");
        int State() => (int)query!.Invoke(null, new object[] { method })!;
        var publisher = Task.Factory.StartNew(StartOne, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Require(SpinWait.SpinUntil(() => State() != 0, TimeSpan.FromSeconds(8)),
            "Publisher never reached the forced gap.");
        Require(State() == 1, "Publisher did not wait for a checkpoint reader.");
        // The second invocation sees installed native code while the winner is paused.
        var waiting = StartOne();
        var captured = (object[][])Capture.Invoke(null, new object[] { waiting, methods })!;
        Require(State() == 2, "Capture did not enter the metadata publication gap.");
        Require(publisher.Wait(TimeSpan.FromSeconds(5)), "Publisher failed to complete.");
        Require(!publisher.Result.IsCompleted && (int)captured.Single()[0] == 3,
            "Capture did not skip non-Task/no-schema decoys and select the owner.");
        Require(RestoreFrames(captured, methods, 41).GetAwaiter().GetResult() == 51,
            "Published checkpoint failed to resume.");
        Console.WriteLine("PASS forced publication gap: reader waited; int/void/no-schema decoys skipped");
    }

    private static void ExpectNotSupported(Action action, string name, string? expectedMessage = null)
    {
        try { action(); }
        catch (TargetInvocationException ex) when (ex.InnerException is NotSupportedException)
        {
            if (expectedMessage != null) Require(ex.InnerException.Message == expectedMessage, "Unexpected rejection: " + ex.InnerException.Message);
            Console.WriteLine("Rejected " + name);
            return;
        }
        throw new Exception("Expected rejection: " + name);
    }

    private static string Fingerprint() =>
        Environment.GetEnvironmentVariable("CHECKPOINT_PROOF_RUNTIME_SHA256")
            ?? throw new Exception("Runner must supply the exact runtime fingerprint.");

    // Exercise the standalone file codec independently of native restore. These
    // synthetic envelopes test serialized counts, not native schema semantics.
    private static void FormatLimitsProof(string path)
    {
        const int fileBytes = 1024 * 1024;
        const string invalidLength = "Invalid snapshot length.";
        const string oversized = "Snapshot exceeds proof limit.";
        int rejected = 0;
        object[] Frame(int schemaCount = 11, int referenceCount = 0, int valueCount = 0) =>
            new object[] { 0, 0, new int[schemaCount],
                Enumerable.Range(0, valueCount).Select(i => (object)i).ToArray(),
                string.Empty, new string?[referenceCount] };
        void Reject(Action action, string message)
        {
            try { action(); }
            catch (InvalidOperationException ex) when (ex.Message == message)
            {
                rejected++;
                return;
            }
            throw new Exception("Expected proof format rejection: " + message);
        }
        void RejectWrite(object[][] frames, string message)
        {
            byte[] published = File.ReadAllBytes(path);
            Reject(() => Write(path, frames), message);
            Require(File.ReadAllBytes(path).SequenceEqual(published),
                "Rejected write changed the published checkpoint.");
        }

        Write(path, new[] { Frame() });
        var minimal = Read(path, out int producer);
        Require(producer == Environment.ProcessId && minimal.Length == 1 &&
            ((int[])minimal[0][2]).Length == 11 && ((object[])minimal[0][3]).Length == 0 &&
            ((string?[])minimal[0][5]).Length == 0, "Minimum proof counts did not round-trip.");
        byte[] seed = File.ReadAllBytes(path);
        long frameOffset, schemaOffset, referenceOffset, valueOffset;
        using (var reader = new BinaryReader(new MemoryStream(seed)))
        {
            for (int i = 0; i < 4; i++) reader.ReadString();
            reader.ReadInt32(); // producer
            frameOffset = reader.BaseStream.Position;
            reader.ReadInt32(); // frames
            reader.ReadInt32(); // method
            reader.ReadInt32(); // state
            schemaOffset = reader.BaseStream.Position;
            int schemaCount = reader.ReadInt32();
            reader.BaseStream.Position += sizeof(int) * schemaCount;
            reader.ReadString(); // method identity
            referenceOffset = reader.BaseStream.Position;
            Require(reader.ReadInt32() == 0, "Expected empty seed reference types.");
            valueOffset = reader.BaseStream.Position;
            Require(reader.ReadInt32() == 0, "Expected empty seed values.");
        }
        void RejectReadCount(long offset, int count)
        {
            byte[] bytes = (byte[])seed.Clone();
            using (var writer = new BinaryWriter(new MemoryStream(bytes)))
            {
                writer.BaseStream.Position = offset;
                writer.Write(count);
            }
            File.WriteAllBytes(path, bytes);
            Reject(() => Read(path, out _), invalidLength);
        }
        foreach (int count in new[] { -1, 0, 17 }) RejectReadCount(frameOffset, count);
        foreach (int count in new[] { -1, 10, 7180 }) RejectReadCount(schemaOffset, count);
        foreach (int count in new[] { -1, 1025 }) RejectReadCount(referenceOffset, count);
        foreach (int count in new[] { -1, 1025 }) RejectReadCount(valueOffset, count);
        File.WriteAllBytes(path, seed);

        RejectWrite(Array.Empty<object[]>(), invalidLength);
        RejectWrite(Enumerable.Range(0, 17).Select(_ => Frame()).ToArray(), invalidLength);
        RejectWrite(new[] { Frame(schemaCount: 10) }, invalidLength);
        RejectWrite(new[] { Frame(schemaCount: 7180) }, invalidLength);
        RejectWrite(new[] { Frame(referenceCount: 1025) }, invalidLength);
        RejectWrite(new[] { Frame(valueCount: 1025) }, invalidLength);

        Write(path, Enumerable.Range(0, 16).Select(_ => Frame(7179, 1024, 1024)).ToArray());
        var maximum = Read(path, out _);
        Require(maximum.Length == 16 && maximum.All(frame =>
            ((int[])frame[2]).Length == 7179 && ((string?[])frame[5]).Length == 1024 &&
            ((object[])frame[3]).Length == 1024 && (int)((object[])frame[3])[1023] == 1023),
            "Maximum proof counts did not round-trip.");

        object[] sized = Frame(valueCount: 1);
        ((object[])sized[3])[0] = string.Empty;
        Write(path, new[] { sized });
        // This ASCII payload uses a three-byte 7-bit length prefix, two more
        // bytes than the empty string. Verify the resulting file size exactly.
        string payload = new string('x', fileBytes - checked((int)new FileInfo(path).Length) - 2);
        ((object[])sized[3])[0] = payload;
        Write(path, new[] { sized });
        Require(new FileInfo(path).Length == fileBytes, "Exact-limit file size was not exercised.");
        Require((string)((object[])Read(path, out _)[0][3])[0] == payload,
            "Exact-limit payload did not round-trip.");
        ((object[])sized[3])[0] = payload + "x";
        RejectWrite(new[] { sized }, oversized);
        using (var stream = new FileStream(path, FileMode.Append)) stream.WriteByte(0);
        Require(new FileInfo(path).Length == fileBytes + 1, "One-over file size was not exercised.");
        Reject(() => Read(path, out _), oversized);
        Require(rejected == 18, "Proof format rejection coverage changed.");
        File.Delete(path);
        File.Delete(path + ".tmp");
        Console.WriteLine("PASS proof format limits: frames=16 schemaIntegers=7179 referenceTypes=1024 values=1024 bytes=1048576 rejectionCases=18");
    }

    private static void Write(string path, object[][] records)
    {
        // Reject unreadable counts before opening the staging file. Otherwise a
        // successful publication could replace a readable checkpoint with one
        // that our reader always rejects.
        Bounded(records.Length, 1, MaxProofFrames);
        foreach (object[] record in records)
        {
            Bounded(((int[])record[2]).Length, 11, MaxProofSchemaIntegers);
            Bounded(((string?[])record[5]).Length, 0, MaxProofSlots);
            Bounded(((object?[])record[3]).Length, 0, MaxProofSlots);
        }
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write,
                   FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write("RuntimeAsyncCheckpointProof-v3");
            var strings = new Dictionary<string, int>(ReferenceEqualityComparer.Instance);
            writer.Write(typeof(CheckpointProof).Module.ModuleVersionId.ToString());
            writer.Write(typeof(object).Module.ModuleVersionId.ToString());
            writer.Write(Fingerprint());
            writer.Write(Environment.ProcessId);
            writer.Write(records.Length);
            foreach (object[] record in records)
            {
                writer.Write((int)record[0]);
                writer.Write((int)record[1]);
                int[] schema = (int[])record[2];
                writer.Write(schema.Length);
                foreach (int field in schema) writer.Write(field);
                writer.Write((string)record[4]);
                var referenceTypes = (string?[])record[5];
                writer.Write(referenceTypes.Length);
                foreach (string? type in referenceTypes) writer.Write(type ?? string.Empty);
                object?[] values = (object?[])record[3];
                writer.Write(values.Length);
                foreach (object? value in values)
                {
                    switch (value)
                    {
                        case null: writer.Write((byte)0); break;
                        case int number: writer.Write((byte)1); writer.Write(number); break;
                        case string text:
                            if (strings.TryGetValue(text, out int previous))
                            {
                                writer.Write((byte)3);
                                writer.Write(previous);
                            }
                            else
                            {
                                strings.Add(text, strings.Count);
                                writer.Write((byte)2);
                                writer.Write(text);
                            }
                            break;
                        default: throw new NotSupportedException();
                    }
                }
            }
            writer.Flush();
            Require(stream.Length <= MaxProofFileBytes, "Snapshot exceeds proof limit.");
            stream.Flush(flushToDisk: true);
        }
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static object[][] Read(string path, out int producer)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        Require(reader.BaseStream.Length <= MaxProofFileBytes, "Snapshot exceeds proof limit.");
        Require(reader.ReadString() == "RuntimeAsyncCheckpointProof-v3", "Unsupported proof file format; recapture with this build.");
        var strings = new List<string>();
        string ReadNewString()
        {
            string text = reader.ReadString();
            strings.Add(text);
            return text;
        }
        Require(reader.ReadString() == typeof(CheckpointProof).Module.ModuleVersionId.ToString(), "Program version mismatch.");
        Require(reader.ReadString() == typeof(object).Module.ModuleVersionId.ToString(), "CoreLib version mismatch.");
        Require(reader.ReadString() == Fingerprint(), "Runtime binary fingerprint mismatch.");
        producer = reader.ReadInt32();
        int count = Bounded(reader.ReadInt32(), 1, MaxProofFrames);
        var records = new object[count][];
        for (int f = 0; f < count; f++)
        {
            int method = reader.ReadInt32(), state = reader.ReadInt32();
            var schema = new int[Bounded(reader.ReadInt32(), 11, MaxProofSchemaIntegers)];
            for (int i = 0; i < schema.Length; i++) schema[i] = reader.ReadInt32();
            string identity = reader.ReadString();
            var referenceTypes = new string?[Bounded(reader.ReadInt32(), 0, MaxProofSlots)];
            for (int i = 0; i < referenceTypes.Length; i++) { string type = reader.ReadString(); referenceTypes[i] = type.Length == 0 ? null : type; }
            var values = new object?[Bounded(reader.ReadInt32(), 0, MaxProofSlots)];
            for (int i = 0; i < values.Length; i++)
                values[i] = reader.ReadByte() switch
                {
                    0 => null,
                    1 => reader.ReadInt32(),
                    2 => ReadNewString(),
                    3 => strings[Bounded(reader.ReadInt32(), 0, strings.Count - 1)],
                    _ => throw new InvalidDataException("Unknown value kind.")
                };
            records[f] = new object[] { method, state, schema, values, identity, referenceTypes };
        }
        Require(reader.BaseStream.Position == reader.BaseStream.Length, "Trailing snapshot bytes.");
        return records;
    }

    private static int Bounded(int value, int min, int max)
    {
        Require(value >= min && value <= max, "Invalid snapshot length.");
        return value;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
