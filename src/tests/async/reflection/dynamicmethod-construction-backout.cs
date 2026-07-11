// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Xunit;

public static class DynamicMethodConstructionBackout
{
    [Fact]
    public static void SecondDescriptorCheckoutFailureReturnsTheFirstDescriptor()
    {
        DynamicMethod runtimeAsync = CreateRuntimeAsyncDynamicMethod("RuntimeAsyncConstructionFailure");
        Assert.Throws<OutOfMemoryException>(() => runtimeAsync.CreateDelegate<Func<Task<int>>>());

        DynamicMethod ordinary = new DynamicMethod(
            "OrdinaryAfterRuntimeAsyncConstructionFailure",
            typeof(int),
            Type.EmptyTypes,
            typeof(DynamicMethodConstructionBackout));
        ILGenerator ordinaryIl = ordinary.GetILGenerator();
        ordinaryIl.Emit(OpCodes.Ldc_I4_7);
        ordinaryIl.Emit(OpCodes.Ret);

        Assert.Equal(7, ordinary.CreateDelegate<Func<int>>()());
    }

    private static DynamicMethod CreateRuntimeAsyncDynamicMethod(string name)
    {
        DynamicMethod dynamicMethod = new DynamicMethod(
            name,
            typeof(Task<int>),
            Type.EmptyTypes,
            typeof(DynamicMethodConstructionBackout));
        dynamicMethod.SetImplementationFlags(MethodImplAttributes.Async);

        ILGenerator ilGenerator = dynamicMethod.GetILGenerator();
        ilGenerator.Emit(OpCodes.Ldc_I4_1);
        ilGenerator.Emit(OpCodes.Ret);
        return dynamicMethod;
    }
}
