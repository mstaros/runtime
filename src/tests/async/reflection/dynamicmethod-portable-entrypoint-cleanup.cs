// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Xunit;

public static class DynamicMethodPortableEntrypointCleanup
{
    [Fact]
    public static void RuntimeAsyncPairClearsPendingPortableEntrypointStateBeforeReuse()
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
                Func<Task<int>> replacement = CreateRuntimeAsyncDynamicMethod("ReusedPortableEntrypointPair", 2);
                Assert.Equal(2, replacement().GetAwaiter().GetResult());
                Assert.False(method.IsAlive);
                Assert.False(@delegate.IsAlive);
                GC.KeepAlive(replacement);
                return;
            }
            catch (OutOfMemoryException)
            {
            }
        }

        Assert.Fail("The runtime-async DynamicMethod pair was not recycled with clean portable-entrypoint state.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Method, WeakReference Delegate)
        CreateExecuteBlockSecondPairAndReleaseRuntimeAsyncMethod()
    {
        DynamicMethod dynamicMethod = CreateRuntimeAsyncDynamicMethodCore("InitialPortableEntrypointPair", 1);
        Func<Task<int>> @delegate = dynamicMethod.CreateDelegate<Func<Task<int>>>();
        Assert.Equal(1, @delegate().GetAwaiter().GetResult());

        Assert.Throws<OutOfMemoryException>(() =>
            CreateRuntimeAsyncDynamicMethod("BlockedWhilePortableEntrypointPairIsLive", 2));

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
            typeof(DynamicMethodPortableEntrypointCleanup));
        dynamicMethod.SetImplementationFlags(MethodImplAttributes.Async);

        ILGenerator ilGenerator = dynamicMethod.GetILGenerator();
        ilGenerator.Emit(OpCodes.Ldc_I4, value);
        ilGenerator.Emit(OpCodes.Ret);
        return dynamicMethod;
    }
}
