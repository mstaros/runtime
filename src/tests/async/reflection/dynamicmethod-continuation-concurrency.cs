// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

// Runtime-only discriminator for process-fatal failures in concurrent continuation dispatch.
// The compiler and workspace-script integration suites exercise emitter breadth separately.
public static class DynamicMethodContinuationConcurrency
{
    private const int BatchCount = 16;
    private const int FanOut = 128;
    private const int GcCycles = 3;

    private static readonly MethodInfo s_awaitTaskOfInt = GetAwaitTaskOfIntMethod();
    private static readonly MethodInfo s_payloadGetValue = typeof(Payload).GetMethod(nameof(Payload.GetValue));
    private static readonly MethodInfo s_setImplementationFlags =
        typeof(DynamicMethod).GetMethod(
            "SetImplementationFlags",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            new Type[] { typeof(MethodImplAttributes) },
            modifiers: null)
        ?? throw new PlatformNotSupportedException("DynamicMethod.SetImplementationFlags is unavailable.");

    public static int Main()
    {
        try
        {
            RunAsync().GetAwaiter().GetResult();
            Console.WriteLine("Concurrent runtime-async DynamicMethod continuation stress passed.");
            return 100;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        for (int batchIndex = 0; batchIndex < BatchCount; batchIndex++)
        {
            SuspendedBatch batch = CreateSuspendedBatch(batchIndex);

            for (int i = 0; i < batch.Results.Length; i++)
            {
                Require(!batch.Results[i].IsCompleted, $"Batch {batchIndex}, result {i} completed before gate release.");
            }

            RunGcLadder();

            // The test intentionally drops the DynamicMethod and delegate roots before release.
            // Pending continuations must independently keep both the LCG resolver and every
            // managed argument used after the await alive.
            Require(!batch.Delegate.IsAlive, $"Batch {batchIndex} retained the delegate root.");
            Require(batch.Resolver.IsAlive, $"Batch {batchIndex} lost the DynamicResolver while suspended.");
            for (int i = 0; i < batch.Payloads.Length; i++)
            {
                Require(batch.Payloads[i].IsAlive, $"Batch {batchIndex}, payload {i} was not captured across suspension.");
            }

            Task[] releases = new Task[FanOut];
            for (int i = 0; i < releases.Length; i++)
            {
                int capture = i;
                releases[i] = Task.Run(() => batch.Gates[capture].SetResult(batch.GateValues[capture]));
            }

            await Task.WhenAll(releases);
            int[] actual = await Task.WhenAll(batch.Results);

            for (int i = 0; i < actual.Length; i++)
            {
                Require(
                    actual[i] == batch.Expected[i],
                    $"Batch {batchIndex}, result {i}: expected {batch.Expected[i]}, actual {actual[i]}.");
            }

            GC.KeepAlive(batch.Resolver);
            GC.KeepAlive(batch.Delegate);
            GC.KeepAlive(batch.Payloads);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SuspendedBatch CreateSuspendedBatch(int batchIndex)
    {
        DynamicMethod method = new DynamicMethod(
            "RadmConcurrentContinuation" + batchIndex,
            MethodAttributes.Public | MethodAttributes.Static,
            CallingConventions.Standard,
            typeof(Task<int>),
            new Type[] { typeof(Task<int>), typeof(Payload), typeof(int) },
            typeof(DynamicMethodContinuationConcurrency).Module,
            skipVisibility: true);
        SetRuntimeAsyncImplementationFlags(method);

        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, s_awaitTaskOfInt);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, s_payloadGetValue);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ret);

        var invoke = method.CreateDelegate<Func<Task<int>, Payload, int, Task<int>>>();
        WeakReference resolver = GetResolverWeakReference(method);
        WeakReference delegateReference = new WeakReference(invoke);

        var gates = new TaskCompletionSource<int>[FanOut];
        var gateValues = new int[FanOut];
        var results = new Task<int>[FanOut];
        var expected = new int[FanOut];
        var payloads = new WeakReference[FanOut];
        int offset = batchIndex + 1;

        for (int i = 0; i < FanOut; i++)
        {
            gates[i] = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            gateValues[i] = (i * 3) + 7;

            var payload = new Payload(((batchIndex + 1) * 10_000) + i);
            payloads[i] = new WeakReference(payload);
            expected[i] = gateValues[i] + payload.GetValue() + offset;
            results[i] = invoke(gates[i].Task, payload, offset);
        }

        return new SuspendedBatch(
            gates,
            gateValues,
            results,
            expected,
            payloads,
            resolver,
            delegateReference);
    }

    private static void SetRuntimeAsyncImplementationFlags(DynamicMethod method)
    {
        object asyncFlag = Enum.Parse(typeof(MethodImplAttributes), "Async", ignoreCase: false);
        s_setImplementationFlags.Invoke(method, new object[] { asyncFlag });
    }

    private static MethodInfo GetAwaitTaskOfIntMethod()
    {
        Type helpers = typeof(RuntimeHelpers).Assembly.GetType(
            "System.Runtime.CompilerServices.AsyncHelpers",
            throwOnError: true);

        foreach (MethodInfo method in helpers.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.Name != "Await" || !method.IsGenericMethodDefinition)
            {
                continue;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 1 &&
                parameters[0].ParameterType.IsGenericType &&
                parameters[0].ParameterType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                return method.MakeGenericMethod(typeof(int));
            }
        }

        throw new InvalidOperationException("AsyncHelpers.Await<T>(Task<T>) overload not found.");
    }

    private static WeakReference GetResolverWeakReference(DynamicMethod method)
    {
        foreach (FieldInfo field in typeof(DynamicMethod).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            object value = field.GetValue(method);
            if (value != null && value.GetType().Name.Contains("Resolver"))
            {
                return new WeakReference(value);
            }
        }

        foreach (FieldInfo field in typeof(DynamicMethod).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (field.GetValue(method) is ILGenerator generator)
            {
                foreach (FieldInfo generatorField in generator.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    object value = generatorField.GetValue(generator);
                    if (value != null && value.GetType().Name.Contains("Resolver"))
                    {
                        return new WeakReference(value);
                    }
                }
            }
        }

        throw new InvalidOperationException("Managed DynamicResolver instance not found via reflection; update the test.");
    }

    private static void RunGcLadder()
    {
        for (int i = 0; i < GcCycles; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public sealed class Payload
    {
        private readonly int _value;

        public Payload(int value)
        {
            _value = value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int GetValue() => _value;
    }

    private sealed class SuspendedBatch
    {
        public SuspendedBatch(
            TaskCompletionSource<int>[] gates,
            int[] gateValues,
            Task<int>[] results,
            int[] expected,
            WeakReference[] payloads,
            WeakReference resolver,
            WeakReference @delegate)
        {
            Gates = gates;
            GateValues = gateValues;
            Results = results;
            Expected = expected;
            Payloads = payloads;
            Resolver = resolver;
            Delegate = @delegate;
        }

        public TaskCompletionSource<int>[] Gates { get; }
        public int[] GateValues { get; }
        public Task<int>[] Results { get; }
        public int[] Expected { get; }
        public WeakReference[] Payloads { get; }
        public WeakReference Resolver { get; }
        public WeakReference Delegate { get; }
    }
}
