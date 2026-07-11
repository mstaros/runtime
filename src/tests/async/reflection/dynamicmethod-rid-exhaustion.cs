// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Xunit;

public static class DynamicMethodRidExhaustion
{
    [Fact]
    public static void RuntimeAsyncDynamicMethodRidExhaustionIsGuarded()
    {
        Func<Task<int>> first = CreateRuntimeAsyncDynamicMethod("FirstRuntimeAsync");
        Assert.Equal(1, first().GetAwaiter().GetResult());

        DynamicMethod second = CreateRuntimeAsyncDynamicMethodCore("SecondRuntimeAsync");
        Assert.Throws<OverflowException>(() => second.CreateDelegate<Func<Task<int>>>());

        DynamicMethod ordinary = new DynamicMethod(
            "OrdinaryAfterRuntimeAsyncRidExhaustion",
            typeof(int),
            Type.EmptyTypes,
            typeof(DynamicMethodRidExhaustion));
        ILGenerator ordinaryIl = ordinary.GetILGenerator();
        ordinaryIl.Emit(OpCodes.Ldc_I4_7);
        ordinaryIl.Emit(OpCodes.Ret);

        Assert.Equal(7, ordinary.CreateDelegate<Func<int>>()());
    }

    private static Func<Task<int>> CreateRuntimeAsyncDynamicMethod(string name) =>
        CreateRuntimeAsyncDynamicMethodCore(name).CreateDelegate<Func<Task<int>>>();

    private static DynamicMethod CreateRuntimeAsyncDynamicMethodCore(string name)
    {
        DynamicMethod dynamicMethod = new DynamicMethod(
            name,
            typeof(Task<int>),
            Type.EmptyTypes,
            typeof(DynamicMethodRidExhaustion));
        dynamicMethod.SetImplementationFlags(MethodImplAttributes.Async);

        ILGenerator ilGenerator = dynamicMethod.GetILGenerator();
        ilGenerator.Emit(OpCodes.Ldc_I4_1);
        ilGenerator.Emit(OpCodes.Ret);
        return dynamicMethod;
    }
}
