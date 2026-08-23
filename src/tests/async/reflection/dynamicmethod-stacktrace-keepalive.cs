// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Xunit;

// Regression test: materializing a stack trace over a runtime-async DynamicMethod frame
// must never crash the process.
//
// A suspended runtime-async LCG (DynamicMethod) method used to be rooted only by its
// Continuation, which held native resume state but no GC reference to the managed
// DynamicResolver. Once the caller dropped the delegate during suspension, the resolver
// was collected, DynamicMethodDesc::TryDestroy reclaimed the descriptor pair and its
// code, and both resumption and stack-trace materialization dereferenced freed memory
// (a fatal, uncatchable AccessViolation). The fix surfaces an is-LCG bit to the JIT
// (CORINFO_LCG_METHOD) so the continuation allocates a keepalive slot populated with the
// managed resolver, which blocks destruction for as long as the continuation - and,
// after the throw, the captured stack-trace data - is reachable.
public static class DynamicMethodStackTraceKeepAlive
{
    private const int GcLadderCycles = 10;

    private static readonly MethodInfo s_awaitTask = GetAwaitMethod(genericOverTaskOfT: false);
    private static readonly MethodInfo s_awaitTaskOfInt = GetAwaitMethod(genericOverTaskOfT: true).MakeGenericMethod(typeof(int));

    [Fact]
    public static void SuspendedContinuationKeepsResolverAliveAndTraceMaterializes()
    {
        (Task<int> task, WeakReference resolver, WeakReference del) = InvokeAndDropRoots("RadmSuspendKeep", nested: false);

        RunGcLadder();
        // The delegate is genuinely gone: nothing outside the runtime roots the method anymore.
        Assert.False(del.IsAlive);
        // Pre-fix the resolver died after a single GC cycle here, TryDestroy reclaimed the
        // descriptor pair while the method was suspended, and the resume was a use-after-free.
        Assert.True(resolver.IsAlive);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
        Assert.NotNull(ex.StackTrace);
        Assert.Contains("RadmSuspendKeep", ex.StackTrace);
        Assert.Contains("RadmSuspendKeep", new StackTrace(ex, fNeedFileInfo: true).ToString());
    }

    [Fact]
    public static void TraceMaterializesAfterPostCatchCollection()
    {
        (Task<int> task, WeakReference resolver, WeakReference del) = InvokeAndDropRoots("RadmTraceUaf", nested: false);

        RunGcLadder();
        Assert.True(resolver.IsAlive);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());

        // After the catch, only the exception's captured trace data may root the method.
        // Collect hard, then materialize; pre-fix this dereferenced destroyed descriptors.
        RunGcLadder();
        Assert.NotNull(ex.StackTrace);
        Assert.Contains("RadmTraceUaf", ex.StackTrace);
        Assert.Contains("RadmTraceUaf", ex.ToString());
        Assert.Contains("RadmTraceUaf", new StackTrace(ex, fNeedFileInfo: true).ToString());
    }

    [Fact]
    public static void ThrowBeforeFirstAwait()
    {
        ThrowAndAssertMaterializes("RadmSyncThrow", awaitFirst: false, edi: false, nested: false);
    }

    [Fact]
    public static void ThrowAfterAwaitResume()
    {
        ThrowAndAssertMaterializes("RadmAwaitThrow", awaitFirst: true, edi: false, nested: false);
    }

    [Fact]
    public static void EdiRethrowPreservesMaterializableTrace()
    {
        ThrowAndAssertMaterializes("RadmEdi", awaitFirst: true, edi: true, nested: false);
    }

    [Fact]
    public static void NestedAsyncDynamicMethods()
    {
        ThrowAndAssertMaterializes("RadmNestedInner", awaitFirst: true, edi: false, nested: true);
    }

    [Fact]
    public static void StressThrowCollectMaterialize()
    {
        for (int i = 0; i < 1000; i++)
        {
            (Task<int> task, WeakReference resolver, WeakReference del) = InvokeAndDropRoots("RadmStress", nested: false);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
            if ((i & 1) != 0)
            {
                try
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }
                catch (InvalidOperationException rethrown)
                {
                    ex = rethrown;
                }
            }

            Assert.NotNull(ex.StackTrace);
            Assert.Contains("RadmStress", ex.StackTrace);
            GC.KeepAlive(resolver);
            GC.KeepAlive(del);
        }
    }

    private static void ThrowAndAssertMaterializes(string name, bool awaitFirst, bool edi, bool nested)
    {
        Exception caught;
        WeakReference resolver;
        WeakReference del;

        if (!awaitFirst)
        {
            (Exception c, WeakReference r, WeakReference d) = InvokeSyncThrowAndDropRoots(name);
            caught = c;
            resolver = r;
            del = d;
        }
        else
        {
            (Task<int> task, WeakReference r, WeakReference d) = InvokeAndDropRoots(name, nested);
            resolver = r;
            del = d;
            caught = Assert.Throws<InvalidOperationException>(() => task.GetAwaiter().GetResult());
        }

        Assert.NotNull(caught);
        RunGcLadder();
        // External roots are really dropped...
        Assert.False(del.IsAlive);
        // ...yet the captured trace (and, for suspensions, the continuation) keeps the
        // resolver alive so materialization cannot touch a destroyed method.
        Assert.True(resolver.IsAlive);

        if (edi)
        {
            try
            {
                ExceptionDispatchInfo.Capture(caught).Throw();
            }
            catch (InvalidOperationException rethrown)
            {
                caught = rethrown;
            }

            RunGcLadder();
            Assert.True(resolver.IsAlive);
        }

        Assert.NotNull(caught.StackTrace);
        Assert.Contains(name, caught.StackTrace);
        Assert.Contains(name, caught.ToString());
        Assert.Contains(name, new StackTrace(caught, fNeedFileInfo: true).ToString());
        if (nested)
        {
            Assert.Contains(name + "Outer", caught.ToString());
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<int> Task, WeakReference Resolver, WeakReference Delegate) InvokeAndDropRoots(string name, bool nested)
    {
        DynamicMethod inner = CreateThrowingAsyncDynamicMethod(name, awaitFirst: true);
        Func<Task<int>> innerInvoke = inner.CreateDelegate<Func<Task<int>>>();
        WeakReference resolver = GetResolverWeakRef(inner);

        if (nested)
        {
            DynamicMethod outer = CreateNestedOuter(name + "Outer");
            Func<Func<Task<int>>, Task<int>> outerInvoke = outer.CreateDelegate<Func<Func<Task<int>>, Task<int>>>();
            Task<int> nestedTask = outerInvoke(innerInvoke);
            return (nestedTask, resolver, new WeakReference(innerInvoke));
        }

        Task<int> task = innerInvoke();
        return (task, resolver, new WeakReference(innerInvoke));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Exception Caught, WeakReference Resolver, WeakReference Delegate) InvokeSyncThrowAndDropRoots(string name)
    {
        DynamicMethod dm = CreateThrowingAsyncDynamicMethod(name, awaitFirst: false);
        Func<Task<int>> invoke = dm.CreateDelegate<Func<Task<int>>>();
        WeakReference resolver = GetResolverWeakRef(dm);
        Exception caught = null;
        try
        {
            invoke().GetAwaiter().GetResult();
        }
        catch (InvalidOperationException ex)
        {
            caught = ex;
        }
        return (caught, resolver, new WeakReference(invoke));
    }

    private static DynamicMethod CreateThrowingAsyncDynamicMethod(string name, bool awaitFirst)
    {
        DynamicMethod dm = new DynamicMethod(
            name,
            typeof(Task<int>),
            Type.EmptyTypes,
            typeof(DynamicMethodStackTraceKeepAlive));
        dm.SetImplementationFlags(MethodImplAttributes.Async);

        ILGenerator il = dm.GetILGenerator();
        if (awaitFirst)
        {
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Call, typeof(Task).GetMethod("Delay", new Type[] { typeof(int) }));
            il.Emit(OpCodes.Call, s_awaitTask);
        }

        il.Emit(OpCodes.Ldstr, "boom from " + name);
        il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor(new Type[] { typeof(string) }));
        il.Emit(OpCodes.Throw);
        return dm;
    }

    private static DynamicMethod CreateNestedOuter(string name)
    {
        DynamicMethod dm = new DynamicMethod(
            name,
            typeof(Task<int>),
            new Type[] { typeof(Func<Task<int>>) },
            typeof(DynamicMethodStackTraceKeepAlive));
        dm.SetImplementationFlags(MethodImplAttributes.Async);

        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, typeof(Func<Task<int>>).GetMethod("Invoke"));
        il.Emit(OpCodes.Call, s_awaitTaskOfInt);
        il.Emit(OpCodes.Ret);
        return dm;
    }

    // Resolved reflectively (instead of typeof(AsyncHelpers)) so the identical source also
    // compiles standalone against a stock SDK and runs directly on a Core_Root.
    private static MethodInfo GetAwaitMethod(bool genericOverTaskOfT)
    {
        Type helpers = typeof(RuntimeHelpers).Assembly.GetType("System.Runtime.CompilerServices.AsyncHelpers", throwOnError: true);
        foreach (MethodInfo m in helpers.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "Await")
            {
                continue;
            }

            ParameterInfo[] parameters = m.GetParameters();
            if (parameters.Length != 1)
            {
                continue;
            }

            if (!genericOverTaskOfT && !m.IsGenericMethod && parameters[0].ParameterType == typeof(Task))
            {
                return m;
            }

            if (genericOverTaskOfT && m.IsGenericMethod &&
                parameters[0].ParameterType.IsGenericType &&
                parameters[0].ParameterType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                return m;
            }
        }

        throw new InvalidOperationException("AsyncHelpers.Await overload not found");
    }

    private static WeakReference GetResolverWeakRef(DynamicMethod dm)
    {
        foreach (FieldInfo f in typeof(DynamicMethod).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            object v = f.GetValue(dm);
            if (v != null && v.GetType().Name.Contains("Resolver"))
            {
                return new WeakReference(v);
            }
        }

        foreach (FieldInfo f in typeof(DynamicMethod).GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
        {
            if (f.GetValue(dm) is ILGenerator gen)
            {
                foreach (FieldInfo gf in gen.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    object gv = gf.GetValue(gen);
                    if (gv != null && gv.GetType().Name.Contains("Resolver"))
                    {
                        return new WeakReference(gv);
                    }
                }
            }
        }

        throw new InvalidOperationException("Managed DynamicResolver instance not found via reflection; update the test.");
    }

    private static void RunGcLadder()
    {
        for (int i = 0; i < GcLadderCycles; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
