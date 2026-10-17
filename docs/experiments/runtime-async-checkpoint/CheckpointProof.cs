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
            else if (args[0] == "restore")
                Resume(args[1]);
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
        s_prefixCalls++;
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
        il.Emit(OpCodes.Add);
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

        Write(path, records);
        Console.WriteLine($"READY producer={Environment.ProcessId} frames={records.Length} bytes={new FileInfo(path).Length}");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite); // The runner forcibly kills this process.
        GC.KeepAlive(pending);
        GC.KeepAlive(source);
        GC.KeepAlive(methods);
    }

    private static void Resume(string path)
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

        object[][] changed = records.Select(r => (object[])r.Clone()).ToArray();
        changed[0][2] = ((int[])changed[0][2]).Clone();
        ((int[])changed[0][2])[0]++;
        ExpectNotSupported(() => Restore.Invoke(null, new object[] { changed, methods, 41 }),
            "changed schema");
        changed = records.Select(r => (object[])r.Clone()).ToArray();
        changed[0][3] = ((object?[])changed[0][3]).Clone();
        ((object?[])changed[0][3])[0] = new object();
        ExpectNotSupported(() => Restore.Invoke(null, new object[] { changed, methods, 41 }),
            "changed value type");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        Task<int> resumed = (Task<int>)Restore.Invoke(null, new object[] { records, methods, 41 })!;
        Require(resumed.IsCompleted, "Proof should finish synchronously after result injection.");
        int actual = resumed.GetAwaiter().GetResult();
        Require(actual == 84, $"Expected 84, got {actual}.");
        Require(s_prefixCalls == 0, "Pre-await code was reexecuted.");
        Console.WriteLine($"PASS producer={producer} restorer={Environment.ProcessId} frames={records.Length} result={actual} preAwaitCalls={s_prefixCalls}");
    }

    private static void ExpectNotSupported(Action action, string name)
    {
        try { action(); }
        catch (TargetInvocationException ex) when (ex.InnerException is NotSupportedException)
        {
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
            writer.Write("RuntimeAsyncCheckpointProof-v1");
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
        Require(reader.ReadString() == "RuntimeAsyncCheckpointProof-v1", "Unknown snapshot format.");
        Require(reader.ReadString() == typeof(CheckpointProof).Module.ModuleVersionId.ToString(), "Program version mismatch.");
        Require(reader.ReadString() == typeof(object).Module.ModuleVersionId.ToString(), "CoreLib version mismatch.");
        Require(reader.ReadString() == Fingerprint(), "Runtime binary fingerprint mismatch.");
        producer = reader.ReadInt32();
        int count = Bounded(reader.ReadInt32(), 1, 16);
        var records = new object[count][];
        for (int f = 0; f < count; f++)
        {
            int method = reader.ReadInt32(), state = reader.ReadInt32();
            var schema = new int[Bounded(reader.ReadInt32(), 7, 3079)];
            for (int i = 0; i < schema.Length; i++) schema[i] = reader.ReadInt32();
            var values = new object?[Bounded(reader.ReadInt32(), 0, 1024)];
            for (int i = 0; i < values.Length; i++)
                values[i] = reader.ReadByte() switch
                {
                    0 => null,
                    1 => reader.ReadInt32(),
                    2 => reader.ReadString(),
                    _ => throw new InvalidDataException("Unknown value kind.")
                };
            records[f] = new object[] { method, state, schema, values };
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
