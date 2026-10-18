// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

// Compiled as ordinary C#. Only the two emitted methods use runtime-async IL.
public static class CheckpointProof
{
    private static readonly Type Helpers = typeof(RuntimeHelpers).Assembly.GetType(
        "System.Runtime.CompilerServices.AsyncHelpers", throwOnError: true)!;
    private static readonly MethodInfo Capture = Helpers.GetMethod(
        "CaptureCheckpointForTesting", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo Restore = Helpers.GetMethod(
        "RestoreCheckpointForTesting", BindingFlags.NonPublic | BindingFlags.Static)!;
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
        public ValueTaskSourceStatus GetStatus(short token) => ValueTaskSourceStatus.Pending;
        public int GetResult(short token) => throw new InvalidOperationException("Source must never complete.");
        public void OnCompleted(Action<object?> continuation, object? state, short token,
            ValueTaskSourceOnCompletedFlags flags)
        {
            Require(CallbackState == null, "Source subscribed more than once.");
            CallbackState = state;
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
        Console.WriteLine($"PASS producer={producer} restorer={Environment.ProcessId} frames={records.Length} result={actual} preAwaitCalls={s_prefixCalls}");
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
        s_nextWait = new PendingSource();
        Task<int> second = RestoreFrames(first, methods, 1);
        Require(!second.IsCompleted, "Expected second suspension.");
        var records = (object[][])Capture.Invoke(null, new object[] { second, methods })!;
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

    private static void Write(string path, object[][] records)
    {
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write,
                   FileShare.None, 4096, FileOptions.WriteThrough))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write("RuntimeAsyncCheckpointProof-v2");
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
                        case string text: writer.Write((byte)2); writer.Write(text); break;
                        default: throw new NotSupportedException();
                    }
                }
            }
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static object[][] Read(string path, out int producer)
    {
        Require(new FileInfo(path).Length <= 1024 * 1024, "Snapshot exceeds proof limit.");
        using var reader = new BinaryReader(File.OpenRead(path));
        Require(reader.ReadString() == "RuntimeAsyncCheckpointProof-v2", "Unknown snapshot format.");
        Require(reader.ReadString() == typeof(CheckpointProof).Module.ModuleVersionId.ToString(), "Program version mismatch.");
        Require(reader.ReadString() == typeof(object).Module.ModuleVersionId.ToString(), "CoreLib version mismatch.");
        Require(reader.ReadString() == Fingerprint(), "Runtime binary fingerprint mismatch.");
        producer = reader.ReadInt32();
        int count = Bounded(reader.ReadInt32(), 1, 16);
        var records = new object[count][];
        for (int f = 0; f < count; f++)
        {
            int method = reader.ReadInt32(), state = reader.ReadInt32();
            var schema = new int[Bounded(reader.ReadInt32(), 11, 4107)];
            for (int i = 0; i < schema.Length; i++) schema[i] = reader.ReadInt32();
            string identity = reader.ReadString();
            var referenceTypes = new string?[Bounded(reader.ReadInt32(), 0, 1024)];
            for (int i = 0; i < referenceTypes.Length; i++) { string type = reader.ReadString(); referenceTypes[i] = type.Length == 0 ? null : type; }
            var values = new object?[Bounded(reader.ReadInt32(), 0, 1024)];
            for (int i = 0; i < values.Length; i++)
                values[i] = reader.ReadByte() switch
                {
                    0 => null,
                    1 => reader.ReadInt32(),
                    2 => reader.ReadString(),
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
