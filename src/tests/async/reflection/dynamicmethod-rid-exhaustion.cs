// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;

public static class DynamicMethodRidExhaustion
{
    public static int Main()
    {
        try
        {
            Func<Task<int>> first = CreateRuntimeAsyncDynamicMethod("FirstRuntimeAsync");
            if (first().GetAwaiter().GetResult() != 1)
            {
                Console.WriteLine("The first runtime-async DynamicMethod returned an unexpected value.");
                return -1;
            }

            DynamicMethod second = CreateRuntimeAsyncDynamicMethodCore("SecondRuntimeAsync");
            try
            {
                _ = second.CreateDelegate<Func<Task<int>>>();
                Console.WriteLine("Expected the second runtime-async DynamicMethod to exhaust the configured RID budget.");
                return -2;
            }
            catch (OverflowException)
            {
            }

            DynamicMethod ordinary = new DynamicMethod(
                "OrdinaryAfterRuntimeAsyncRidExhaustion",
                typeof(int),
                Type.EmptyTypes,
                typeof(DynamicMethodRidExhaustion));
            ILGenerator ordinaryIl = ordinary.GetILGenerator();
            ordinaryIl.Emit(OpCodes.Ldc_I4_7);
            ordinaryIl.Emit(OpCodes.Ret);

            if (ordinary.CreateDelegate<Func<int>>()() != 7)
            {
                Console.WriteLine("Ordinary DynamicMethod creation was affected by runtime-async RID exhaustion.");
                return -3;
            }

            Console.WriteLine("Test Passed");
            return 100;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
            return -4;
        }
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
