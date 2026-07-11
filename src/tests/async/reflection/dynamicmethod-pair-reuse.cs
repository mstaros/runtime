// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

public static class DynamicMethodPairReuse
{
    [Fact]
    public static void RuntimeAsyncPairIsRecycledAfterForcedFinalization()
    {
        (WeakReference method, WeakReference @delegate) =
            CreateExecuteBlockSecondPairAndReleaseRuntimeAsyncMethod();

        for (int attempt = 0; attempt < 20; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            try
            {
                Func<Task<int>> replacement = CreateRuntimeAsyncDynamicMethod("ReusedRuntimeAsyncPair", 2);
                Assert.Equal(2, replacement().GetAwaiter().GetResult());
                Assert.False(method.IsAlive);
                Assert.False(@delegate.IsAlive);
                GC.KeepAlive(replacement);
                return;
            }
            catch (OutOfMemoryException)
            {
                Thread.Sleep(10);
            }
        }

        Assert.True(false, "The runtime-async DynamicMethod descriptor pair was not recycled after forced finalization.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Method, WeakReference Delegate)
        CreateExecuteBlockSecondPairAndReleaseRuntimeAsyncMethod()
    {
        DynamicMethod dynamicMethod = CreateRuntimeAsyncDynamicMethodCore("InitialRuntimeAsyncPair", 1);
        Func<Task<int>> @delegate = dynamicMethod.CreateDelegate<Func<Task<int>>>();
        Assert.Equal(1, @delegate().GetAwaiter().GetResult());

        Assert.Throws<OutOfMemoryException>(() =>
            CreateRuntimeAsyncDynamicMethod("BlockedWhileFirstPairIsLive", 2));

        return (new WeakReference(dynamicMethod), new WeakReference(@delegate));
    }

    private static Func<Task<int>> CreateRuntimeAsyncDynamicMethod(string name, int value) =>
        CreateRuntimeAsyncDynamicMethodCore(name, value).CreateDelegate<Func<Task<int>>>();

    private static DynamicMethod CreateRuntimeAsyncDynamicMethodCore(string name, int value)
    {
        DynamicMethod dynamicMethod = new DynamicMethod(
            name,
            typeof(Task<int>),
            Type.EmptyTypes,
            typeof(DynamicMethodPairReuse));
        dynamicMethod.SetImplementationFlags(MethodImplAttributes.Async);

        ILGenerator ilGenerator = dynamicMethod.GetILGenerator();
        ilGenerator.Emit(OpCodes.Ldc_I4, value);
        ilGenerator.Emit(OpCodes.Ret);
        return dynamicMethod;
    }
}
