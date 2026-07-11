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
        ModuleBuilder ownerModule = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("DynamicMethodConstructionFailureCleanupOwner"),
            AssemblyBuilderAccess.Run).DefineDynamicModule("OwnerModule");

        for (int failure = 0; failure < 4; failure++)
        {
            DynamicMethod dynamicMethod = CreateRuntimeAsyncDynamicMethod(
                ownerModule,
                $"InjectedFailure{failure + 1}",
                failure + 1);

            Assert.Throws<OutOfMemoryException>(() =>
                dynamicMethod.CreateDelegate<Func<Task<int>>>());
        }

        Func<Task<int>> success = CreateRuntimeAsyncDynamicMethod(
            ownerModule,
            "SuccessfulAfterInjectedFailures",
            5).CreateDelegate<Func<Task<int>>>();

        Assert.Equal(5, success().GetAwaiter().GetResult());
    }

    private static DynamicMethod CreateRuntimeAsyncDynamicMethod(
        ModuleBuilder ownerModule,
        string name,
        int value)
    {
        DynamicMethod dynamicMethod = new DynamicMethod(
            name,
            typeof(Task<int>),
            Type.EmptyTypes,
            ownerModule);
        dynamicMethod.SetImplementationFlags(MethodImplAttributes.Async);

        ILGenerator ilGenerator = dynamicMethod.GetILGenerator();
        ilGenerator.Emit(OpCodes.Ldc_I4, value);
        ilGenerator.Emit(OpCodes.Ret);
        return dynamicMethod;
    }
}
