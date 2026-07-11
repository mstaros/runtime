// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Xunit;

public static class DynamicMethodConstructionFailureCleanup
{
    [Fact]
    public static void LateConstructionFailuresReleasePairResources()
    {
        for (int failure = 0; failure < 4; failure++)
        {
            DynamicMethod dynamicMethod = CreateRuntimeAsyncDynamicMethod(
                $"InjectedFailure{failure + 1}",
                failure + 1);

            Assert.Throws<OutOfMemoryException>(() =>
                dynamicMethod.CreateDelegate<Func<Task<int>>>());
        }

        Func<Task<int>> success = CreateRuntimeAsyncDynamicMethod(
            "SuccessfulAfterInjectedFailures",
            5).CreateDelegate<Func<Task<int>>>();

        Assert.Equal(5, success().GetAwaiter().GetResult());
    }

    private static DynamicMethod CreateRuntimeAsyncDynamicMethod(string name, int value)
    {
        DynamicMethod dynamicMethod = new DynamicMethod(
            name,
            typeof(Task<int>),
            Type.EmptyTypes,
            typeof(DynamicMethodConstructionFailureCleanup));
        dynamicMethod.SetImplementationFlags(MethodImplAttributes.Async);

        ILGenerator ilGenerator = dynamicMethod.GetILGenerator();
        ilGenerator.Emit(OpCodes.Ldc_I4, value);
        ilGenerator.Emit(OpCodes.Ret);
        return dynamicMethod;
    }
}
