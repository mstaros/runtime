// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Xunit;

public class Async2Reflection
{
    private static readonly MethodInfo s_taskAwaitMethod = GetAsyncHelpersAwaitMethod(typeof(Task));
    private static readonly MethodInfo s_valueTaskAwaitMethod = GetAsyncHelpersAwaitMethod(typeof(ValueTask));
    private static readonly MethodInfo s_taskIntAwaitMethod = GetAsyncHelpersAwaitGenericMethod(typeof(Task<>)).MakeGenericMethod(typeof(int));
    private static readonly MethodInfo s_valueTaskIntAwaitMethod = GetAsyncHelpersAwaitGenericMethod(typeof(ValueTask<>)).MakeGenericMethod(typeof(int));
    public static bool IsRuntimeAsyncDynamicMethodSupported =>
        TestLibrary.Utilities.IsReflectionEmitSupported && PlatformDetection.IsRuntimeAsyncSupported;

    [Fact]
    public static void MethodInfo_Invoke_TaskReturning()
    {
        var mi = typeof(Async2Reflection).GetMethod("Foo", BindingFlags.Static | BindingFlags.NonPublic)!;
        Task<int> r = (Task<int>)mi.Invoke(null, null)!;

        int barResult;
        if (TestLibrary.Utilities.IsNativeAot)
        {
            mi = typeof(Async2Reflection).GetMethod("Bar", BindingFlags.Instance | BindingFlags.NonPublic)!;
            barResult = ((Task<int>)mi.Invoke(new Async2Reflection(), null)!).Result;
        }
        else
        {
            dynamic d = new Async2Reflection();
            barResult = d.Bar().Result;
        }

        Assert.Equal(100, (int)(r.Result + barResult));
    }

    [Fact]
    public static void MethodInfo_Invoke_AsyncHelper()
    {
        var mi = typeof(System.Runtime.CompilerServices.AsyncHelpers).GetMethod("Await", BindingFlags.Static | BindingFlags.Public, new Type[] { typeof(Task) })!;
        Assert.NotNull(mi);
        Assert.Throws<NotSupportedException>(() => mi.Invoke(null, new object[] { FooTask() }));

        // Sadly the following does not throw and results in UB
        // We cannot completely prevent putting a token of an Async method into IL stream.
        // CONSIDER: perhaps JIT could throw?
        //
        // dynamic d = FooTask();
        // System.Runtime.CompilerServices.AsyncHelpers.Await(d);
    }

    private static async Task<int> Foo()
    {
        await Task.Yield();
        return 90;
    }

    private static async Task FooTask()
    {
        await Task.Yield();
    }

    private async Task<int> Bar()
    {
        await Task.Yield();
        return 10;
    }

    [Fact]
    public static void AwaitTaskReturningExpressionLambda()
    {
        var expr1 = (Expression<Func<Task<int>>>)(() => Task.FromResult(42));
        var del = expr1.Compile();
        Assert.Equal(42, del().Result);

        AwaitF(42, del).GetAwaiter().GetResult();
    }

    static async Task AwaitF<T>(T expected, Func<Task<T>> f)
    {
        var res = await f.Invoke();
        Assert.Equal(expected, res);
    }

    public interface IExample<T>
    {
        Task TaskReturning();
        T TReturning();
    }

    public class ExampleClass : IExample<Task>
    {
        public Task TaskReturning()
        {
            return null;
        }

        public Task TReturning()
        {
            return null;
        }
    }

    public struct ExampleStruct : IExample<Task>
    {
        public Task TaskReturning()
        {
            return null;
        }

        public Task TReturning()
        {
            return null;
        }
    }

    [Fact]
    [ActiveIssue("https://github.com/dotnet/runtime/issues/89157", typeof(TestLibrary.Utilities), nameof(TestLibrary.Utilities.IsNativeAot))]
    public static void GetInterfaceMap()
    {
        Type interfaceType = typeof(IExample<Task>);
        Type classType = typeof(ExampleClass);

        InterfaceMapping map = classType.GetInterfaceMap(interfaceType);

        Assert.Equal(2, map.InterfaceMethods.Length);
        Assert.Equal("System.Threading.Tasks.Task TaskReturning() --> System.Threading.Tasks.Task TaskReturning()",
            $"{map.InterfaceMethods[0]?.ToString()} --> {map.TargetMethods[0]?.ToString()}");

        Assert.Equal("System.Threading.Tasks.Task TReturning() --> System.Threading.Tasks.Task TReturning()",
            $"{map.InterfaceMethods[1]?.ToString()} --> {map.TargetMethods[1]?.ToString()}");

        Type structType = typeof(ExampleStruct);

        map = structType.GetInterfaceMap(interfaceType);
        Assert.Equal(2, map.InterfaceMethods.Length);
        Assert.Equal("System.Threading.Tasks.Task TaskReturning() --> System.Threading.Tasks.Task TaskReturning()",
            $"{map.InterfaceMethods[0]?.ToString()} --> {map.TargetMethods[0]?.ToString()}");

        Assert.Equal("System.Threading.Tasks.Task TReturning() --> System.Threading.Tasks.Task TReturning()",
            $"{map.InterfaceMethods[1]?.ToString()} --> {map.TargetMethods[1]?.ToString()}");
    }
    [ConditionalFact(typeof(TestLibrary.Utilities), nameof(TestLibrary.Utilities.IsReflectionEmitSupported))]
    public static void TypeBuilder_DefineMethod()
    {
        //  we will be compiling a dynamic vesion of this method
        //
        //  public async static Task StaticMethod(Task arg)
        //  {
        //    await arg;
        //  }

        // Define a dynamic assembly and module
        AssemblyName assemblyName = new AssemblyName("DynamicAssembly");
        AssemblyBuilder assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule("DynamicModule");

        // Define a type
        TypeBuilder typeBuilder = moduleBuilder.DefineType("DynamicType", TypeAttributes.Public);

        // Define a method
        MethodBuilder methodBuilder = typeBuilder.DefineMethod(
            "DynamicMethod",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(Task),
            new Type[] { typeof(Task) });

        // Set `MethodImpl.Async` flag
        methodBuilder.SetImplementationFlags(MethodImplAttributes.Async);

        // {
        //   Await(arg_0);
        //   ret;
        // }
        ILGenerator ilGenerator = methodBuilder.GetILGenerator();
        ilGenerator.Emit(OpCodes.Ldarg_0);
        var mi = typeof(System.Runtime.CompilerServices.AsyncHelpers).GetMethod("Await", BindingFlags.Static | BindingFlags.Public, new Type[] { typeof(Task) })!;
        ilGenerator.EmitCall(OpCodes.Call, mi, new Type[] { typeof(Task) });
        ilGenerator.Emit(OpCodes.Ret);

        // Create the type and invoke the method
        Type dynamicType = typeBuilder.CreateType();
        MethodInfo dynamicMethod = dynamicType.GetMethod("DynamicMethod");
        var del = dynamicMethod.CreateDelegate<Func<Task, Task>>();

        // the following should not crash
        del(Task.CompletedTask);
        del(FooTask());
    }



    [ConditionalFact(typeof(TestLibrary.Utilities), nameof(TestLibrary.Utilities.IsReflectionEmitSupported))]
    public static void DynamicMethod_SetImplementationFlags_NormalizesBeforeCreateDelegate()
    {
        DynamicMethod dynamicMethod = new DynamicMethod(
            "DynamicMethodImplementationFlagsNormalization",
            typeof(int),
            Type.EmptyTypes);

        const MethodImplAttributes expected =
            MethodImplAttributes.IL | MethodImplAttributes.NoInlining;
        Assert.Equal(expected, dynamicMethod.GetMethodImplementationFlags());

        dynamicMethod.SetImplementationFlags(MethodImplAttributes.IL);
        Assert.Equal(expected, dynamicMethod.GetMethodImplementationFlags());

        dynamicMethod.SetImplementationFlags(MethodImplAttributes.NoInlining);
        Assert.Equal(expected, dynamicMethod.GetMethodImplementationFlags());

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            dynamicMethod.SetImplementationFlags(MethodImplAttributes.NoOptimization));
        Assert.Equal("attributes", exception.ParamName);
        Assert.Equal(expected, dynamicMethod.GetMethodImplementationFlags());
    }

    [ConditionalFact(typeof(Async2Reflection), nameof(IsRuntimeAsyncDynamicMethodSupported))]
    public static void DynamicMethod_SetImplementationFlags_CustomAttributesReflectEffectiveFlags()
    {
        DynamicMethod dynamicMethod = new DynamicMethod(
            "DynamicMethodImplementationFlagsCustomAttributes",
            typeof(Task),
            Type.EmptyTypes);

        dynamicMethod.SetImplementationFlags(MethodImplAttributes.Async);
        const MethodImplAttributes expected =
            MethodImplAttributes.IL | MethodImplAttributes.NoInlining | MethodImplAttributes.Async;

        var typedAttribute = Assert.Single(dynamicMethod.GetCustomAttributes(typeof(MethodImplAttribute), inherit: false));
        Assert.Equal((MethodImplOptions)expected, Assert.IsType<MethodImplAttribute>(typedAttribute).Value);

        var attribute = Assert.Single(dynamicMethod.GetCustomAttributes(inherit: false));
        Assert.Equal((MethodImplOptions)expected, Assert.IsType<MethodImplAttribute>(attribute).Value);
        Assert.True(dynamicMethod.IsDefined(typeof(MethodImplAttribute), inherit: false));
    }

    [ConditionalFact(typeof(Async2Reflection), nameof(IsRuntimeAsyncDynamicMethodSupported))]
    public static async Task DynamicMethod_SetImplementationFlags_AsyncOnlyFlag()
    {
        DynamicMethod dynamicMethod = new DynamicMethod(
            "DynamicAsyncOnlyFlagMethod",
            typeof(Task<int>),
            new Type[] { typeof(Task<int>) });

        dynamicMethod.SetImplementationFlags(MethodImplAttributes.Async);
        EmitAwaitInt32Add(dynamicMethod.GetILGenerator(), typeof(Task<>), 1);

        Assert.Equal(
            MethodImplAttributes.IL | MethodImplAttributes.NoInlining | MethodImplAttributes.Async,
            dynamicMethod.GetMethodImplementationFlags());

        var del = dynamicMethod.CreateDelegate<Func<Task<int>, Task<int>>>();
        Assert.Equal(42, await del(Task.FromResult(41)));
    }

    [ConditionalFact(typeof(Async2Reflection), nameof(IsRuntimeAsyncDynamicMethodSupported))]
    public static async Task DynamicMethod_SetImplementationFlags_Async()
    {
        DynamicMethod dynamicMethod = CreateTaskIntAddOneAsyncDynamicMethod("DynamicAsyncMethod");

        var del = dynamicMethod.CreateDelegate<Func<Task<int>, Task<int>>>();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> result = del(tcs.Task);
        Assert.False(result.IsCompleted);

        tcs.SetResult(41);
        Assert.Equal(42, await result);
    }

    [ConditionalFact(typeof(Async2Reflection), nameof(IsRuntimeAsyncDynamicMethodSupported))]
    public static async Task DynamicMethod_SetImplementationFlags_Async_CreateDelegateByType()
    {
        DynamicMethod dynamicMethod = CreateTaskIntAddOneAsyncDynamicMethod("DynamicAsyncCreateDelegateByTypeMethod");

        Delegate del = dynamicMethod.CreateDelegate(typeof(Func<Task<int>, Task<int>>));
        var typedDelegate = Assert.IsType<Func<Task<int>, Task<int>>>(del);

        Assert.Equal(42, await typedDelegate(Task.FromResult(41)));
    }

    [ConditionalFact(typeof(Async2Reflection), nameof(IsRuntimeAsyncDynamicMethodSupported))]
    public static async Task DynamicMethod_SetImplementationFlags_Async_Invoke()
    {
        DynamicMethod dynamicMethod = CreateTaskIntAddOneAsyncDynamicMethod("DynamicAsyncInvokeMethod");
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        object resultObject = dynamicMethod.Invoke(null, new object[] { tcs.Task });
        Task<int> result = Assert.IsAssignableFrom<Task<int>>(resultObject);
        Assert.False(result.IsCompleted);

        tcs.SetResult(41);
        Assert.Equal(42, await result);
    }

    [ConditionalFact(typeof(Async2Reflection), nameof(IsRuntimeAsyncDynamicMethodSupported))]
    public static async Task DynamicMethod_SetImplementationFlags_Async_ModuleAndOwnerConstructors()
    {
        DynamicMethod moduleMethod = CreateTaskIntAddOneAsyncDynamicMethod(
            "DynamicAsyncModuleBoundMethod",
            typeof(Async2Reflection).Module);
        DynamicMethod ownerMethod = CreateTaskIntAddOneAsyncDynamicMethod(
            "DynamicAsyncOwnerBoundMethod",
            typeof(Async2Reflection));

        var moduleDelegate = moduleMethod.CreateDelegate<Func<Task<int>, Task<int>>>();
        var ownerDelegate = ownerMethod.CreateDelegate<Func<Task<int>, Task<int>>>();

        Assert.Equal(42, await moduleDelegate(Task.FromResult(41)));
        Assert.Equal(42, await ownerDelegate(Task.FromResult(41)));
    }

    [ConditionalFact(typeof(Async2Reflection), nameof(IsRuntimeAsyncDynamicMethodSupported))]
    public static async Task DynamicMethod_SetImplementationFlags_Async_ValueTask()
    {
        DynamicMethod dynamicMethod = CreateAsyncDynamicMethod(
            "DynamicAsyncValueTaskOfInt32Method",
            typeof(ValueTask<int>),
            new Type[] { typeof(ValueTask<int>) });
        EmitAwaitInt32Add(dynamicMethod.GetILGenerator(), typeof(ValueTask<>), 1);

        var del = dynamicMethod.CreateDelegate<Func<ValueTask<int>, ValueTask<int>>>();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        ValueTask<int> result = del(new ValueTask<int>(tcs.Task));
        Assert.False(result.IsCompleted);

        tcs.SetResult(41);
        Assert.Equal(42, await result);
    }

    [ConditionalFact(typeof(Async2Reflection), nameof(IsRuntimeAsyncDynamicMethodSupported))]
    public static async Task DynamicMethod_SetImplementationFlags_Async_NonGenericTask()
    {
        DynamicMethod dynamicMethod = CreateAsyncDynamicMethod(
            "DynamicAsyncTaskMethod",
            typeof(Task),
            new Type[] { typeof(Task) });

        ILGenerator ilGenerator = dynamicMethod.GetILGenerator();
        ilGenerator.Emit(OpCodes.Ldarg_0);
        ilGenerator.Emit(OpCodes.Call, s_taskAwaitMethod);
        ilGenerator.Emit(OpCodes.Ret);

        var del = dynamicMethod.CreateDelegate<Func<Task, Task>>();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task result = del(tcs.Task);
        Assert.False(result.IsCompleted);

        tcs.SetResult(0);
        await result;
    }

    [ConditionalFact(typeof(Async2Reflection), nameof(IsRuntimeAsyncDynamicMethodSupported))]
    public static async Task DynamicMethod_SetImplementationFlags_Async_NonGenericValueTask()
    {
        DynamicMethod dynamicMethod = CreateAsyncDynamicMethod(
            "DynamicAsyncValueTaskMethod",
            typeof(ValueTask),
            new Type[] { typeof(ValueTask) });

        ILGenerator ilGenerator = dynamicMethod.GetILGenerator();
        ilGenerator.Emit(OpCodes.Ldarg_0);
        ilGenerator.Emit(OpCodes.Call, s_valueTaskAwaitMethod);
        ilGenerator.Emit(OpCodes.Ret);

        var del = dynamicMethod.CreateDelegate<Func<ValueTask, ValueTask>>();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        ValueTask result = del(new ValueTask(tcs.Task));
        Assert.False(result.IsCompleted);

        tcs.SetResult(0);
        await result;
    }

    [ConditionalFact(typeof(Async2Reflection), nameof(IsRuntimeAsyncDynamicMethodSupported))]
    public static async Task DynamicMethod_SetImplementationFlags_Async_DynamicILInfo()
    {
        DynamicMethod dynamicMethod = CreateAsyncDynamicMethod(
            "DynamicAsyncInfoMethod",
            typeof(Task<int>),
            new Type[] { typeof(Task<int>) });

        DynamicILInfo ilInfo = dynamicMethod.GetDynamicILInfo();
        MethodInfo awaitMethod = s_taskIntAwaitMethod;
        int awaitToken = ilInfo.GetTokenFor(awaitMethod.MethodHandle);

        byte[] code = new byte[]
        {
            (byte)OpCodes.Ldarg_0.Value,
            (byte)OpCodes.Call.Value,
            (byte)awaitToken,
            (byte)(awaitToken >> 8),
            (byte)(awaitToken >> 16),
            (byte)(awaitToken >> 24),
            (byte)OpCodes.Ldc_I4_1.Value,
            (byte)OpCodes.Add.Value,
            (byte)OpCodes.Ret.Value,
        };

        ilInfo.SetCode(code, 2);
        ilInfo.SetLocalSignature(new byte[] { 0x07, 0x00 });

        var del = dynamicMethod.CreateDelegate<Func<Task<int>, Task<int>>>();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> result = del(tcs.Task);
        Assert.False(result.IsCompleted);

        tcs.SetResult(41);
        Assert.Equal(42, await result);
    }

    [ConditionalFact(typeof(Async2Reflection), nameof(IsRuntimeAsyncDynamicMethodSupported))]
    public static void DynamicMethod_SetImplementationFlags_Async_CompletesSynchronouslyWhenAwaitCompletes()
    {
        DynamicMethod dynamicMethod = CreateTaskIntAddOneAsyncDynamicMethod("DynamicAsyncCompletedAwaitMethod");
        var del = dynamicMethod.CreateDelegate<Func<Task<int>, Task<int>>>();

        Task<int> result = del(Task.FromResult(41));

        Assert.True(result.IsCompletedSuccessfully);
        Assert.Equal(42, result.Result);
    }

    [ConditionalFact(typeof(Async2Reflection), nameof(IsRuntimeAsyncDynamicMethodSupported))]
    public static async Task DynamicMethod_SetImplementationFlags_Async_MultipleAwaits()
    {
        DynamicMethod dynamicMethod = CreateAsyncDynamicMethod(
            "DynamicAsyncMultipleAwaitsMethod",
            typeof(Task<int>),
            new Type[] { typeof(Task<int>), typeof(Task<int>) });

        MethodInfo awaitMethod = s_taskIntAwaitMethod;
        ILGenerator ilGenerator = dynamicMethod.GetILGenerator();
        ilGenerator.Emit(OpCodes.Ldarg_0);
        ilGenerator.Emit(OpCodes.Call, awaitMethod);
        ilGenerator.Emit(OpCodes.Ldarg_1);
        ilGenerator.Emit(OpCodes.Call, awaitMethod);
        ilGenerator.Emit(OpCodes.Add);
        ilGenerator.Emit(OpCodes.Ret);

        var del = dynamicMethod.CreateDelegate<Func<Task<int>, Task<int>, Task<int>>>();
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> result = del(first.Task, second.Task);
        Assert.False(result.IsCompleted);

        first.SetResult(20);
        await Task.Yield();
        Assert.False(result.IsCompleted);

        second.SetResult(22);
        Assert.Equal(42, await result);
    }

    [ConditionalFact(typeof(Async2Reflection), nameof(IsRuntimeAsyncDynamicMethodSupported))]
    public static async Task DynamicMethod_SetImplementationFlags_Async_AwaitsDynamicAsyncMethod()
    {
        DynamicMethod calleeMethod = CreateTaskIntAddOneAsyncDynamicMethod("DynamicAsyncCalleeMethod");
        var calleeDelegate = calleeMethod.CreateDelegate<Func<Task<int>, Task<int>>>();

        DynamicMethod callerMethod = CreateAsyncDynamicMethod(
            "DynamicAsyncCallerMethod",
            typeof(Task<int>),
            new Type[] { typeof(Task<int>) });

        ILGenerator ilGenerator = callerMethod.GetILGenerator();
        ilGenerator.Emit(OpCodes.Ldarg_0);
        ilGenerator.Emit(OpCodes.Call, calleeMethod);
        ilGenerator.Emit(OpCodes.Call, s_taskIntAwaitMethod);
        ilGenerator.Emit(OpCodes.Ldc_I4_1);
        ilGenerator.Emit(OpCodes.Add);
        ilGenerator.Emit(OpCodes.Ret);

        var callerDelegate = callerMethod.CreateDelegate<Func<Task<int>, Task<int>>>();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> result = callerDelegate(tcs.Task);
        Assert.False(result.IsCompleted);

        tcs.SetResult(40);
        Assert.Equal(42, await result);
        GC.KeepAlive(calleeDelegate);
    }

    [ConditionalFact(typeof(Async2Reflection), nameof(IsRuntimeAsyncDynamicMethodSupported))]
    public static async Task DynamicMethod_SetImplementationFlags_Async_PropagatesAwaitException()
    {
        DynamicMethod dynamicMethod = CreateAsyncDynamicMethod(
            "DynamicAsyncExceptionMethod",
            typeof(Task<int>),
            new Type[] { typeof(Task<int>) });
        EmitAwaitInt32Return(dynamicMethod.GetILGenerator(), typeof(Task<>));

        var del = dynamicMethod.CreateDelegate<Func<Task<int>, Task<int>>>();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> result = del(tcs.Task);
        var exception = new InvalidOperationException("dynamic async await failed");

        tcs.SetException(exception);
        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(() => result);
        Assert.Same(exception, actual);
    }

    [ConditionalFact(typeof(Async2Reflection), nameof(IsRuntimeAsyncDynamicMethodSupported))]
    public static async Task DynamicMethod_SetImplementationFlags_Async_PropagatesAwaitCancellation()
    {
        DynamicMethod dynamicMethod = CreateAsyncDynamicMethod(
            "DynamicAsyncCancellationMethod",
            typeof(Task<int>),
            new Type[] { typeof(Task<int>) });
        EmitAwaitInt32Return(dynamicMethod.GetILGenerator(), typeof(Task<>));

        var del = dynamicMethod.CreateDelegate<Func<Task<int>, Task<int>>>();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> result = del(tcs.Task);

        tcs.SetCanceled();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => result);
        Assert.True(result.IsCanceled);
    }

    [ConditionalFact(typeof(Async2Reflection), nameof(IsRuntimeAsyncDynamicMethodSupported))]
    public static void DynamicMethod_SetImplementationFlags_Async_RequiresTaskLikeReturnType()
    {
        DynamicMethod dynamicMethod = new DynamicMethod(
            "DynamicAsyncInvalidReturnTypeMethod",
            typeof(int),
            Type.EmptyTypes);

        SetRuntimeAsyncImplementationFlags(dynamicMethod);

        ILGenerator ilGenerator = dynamicMethod.GetILGenerator();
        ilGenerator.Emit(OpCodes.Ldc_I4_1);
        ilGenerator.Emit(OpCodes.Ret);

        Assert.Throws<NotSupportedException>(() => dynamicMethod.CreateDelegate<Func<int>>());
    }

    [ConditionalFact(typeof(TestLibrary.Utilities), nameof(TestLibrary.Utilities.IsReflectionEmitSupported))]
    public static void DynamicMethod_SetImplementationFlags_ThrowsAfterCreateDelegate()
    {
        DynamicMethod dynamicMethod = new DynamicMethod(
            "DynamicMethodWithBakedImplementationFlags",
            typeof(int),
            Type.EmptyTypes);

        ILGenerator ilGenerator = dynamicMethod.GetILGenerator();
        ilGenerator.Emit(OpCodes.Ldc_I4_1);
        ilGenerator.Emit(OpCodes.Ret);

        _ = dynamicMethod.CreateDelegate<Func<int>>();

        Assert.Throws<InvalidOperationException>(() =>
            SetRuntimeAsyncImplementationFlags(dynamicMethod));
    }

    private static DynamicMethod CreateTaskIntAddOneAsyncDynamicMethod(string name) =>
        CreateTaskIntAddOneAsyncDynamicMethodCore(new DynamicMethod(
            name,
            typeof(Task<int>),
            new Type[] { typeof(Task<int>) }));

    private static DynamicMethod CreateTaskIntAddOneAsyncDynamicMethod(string name, Module module) =>
        CreateTaskIntAddOneAsyncDynamicMethodCore(new DynamicMethod(
            name,
            typeof(Task<int>),
            new Type[] { typeof(Task<int>) },
            module));

    private static DynamicMethod CreateTaskIntAddOneAsyncDynamicMethod(string name, Type owner) =>
        CreateTaskIntAddOneAsyncDynamicMethodCore(new DynamicMethod(
            name,
            typeof(Task<int>),
            new Type[] { typeof(Task<int>) },
            owner));

    private static DynamicMethod CreateTaskIntAddOneAsyncDynamicMethodCore(DynamicMethod dynamicMethod)
    {
        SetRuntimeAsyncImplementationFlags(dynamicMethod);
        EmitAwaitInt32Add(dynamicMethod.GetILGenerator(), typeof(Task<>), 1);
        return dynamicMethod;
    }

    private static DynamicMethod CreateAsyncDynamicMethod(string name, Type returnType, Type[] parameterTypes)
    {
        DynamicMethod dynamicMethod = new DynamicMethod(name, returnType, parameterTypes);
        SetRuntimeAsyncImplementationFlags(dynamicMethod);
        return dynamicMethod;
    }

    private static void SetRuntimeAsyncImplementationFlags(DynamicMethod dynamicMethod) =>
        dynamicMethod.SetImplementationFlags(dynamicMethod.GetMethodImplementationFlags() | MethodImplAttributes.Async);

    private static void EmitAwaitInt32Add(ILGenerator ilGenerator, Type awaitableGenericTypeDefinition, int value)
    {
        EmitAwaitInt32(ilGenerator, awaitableGenericTypeDefinition);
        ilGenerator.Emit(OpCodes.Ldc_I4, value);
        ilGenerator.Emit(OpCodes.Add);
        ilGenerator.Emit(OpCodes.Ret);
    }

    private static void EmitAwaitInt32Return(ILGenerator ilGenerator, Type awaitableGenericTypeDefinition)
    {
        EmitAwaitInt32(ilGenerator, awaitableGenericTypeDefinition);
        ilGenerator.Emit(OpCodes.Ret);
    }

    private static void EmitAwaitInt32(ILGenerator ilGenerator, Type awaitableGenericTypeDefinition)
    {
        ilGenerator.Emit(OpCodes.Ldarg_0);
        ilGenerator.Emit(OpCodes.Call, GetAsyncHelpersAwaitInt32Method(awaitableGenericTypeDefinition));
    }

    private static MethodInfo GetAsyncHelpersAwaitInt32Method(Type awaitableGenericTypeDefinition)
    {
        if (awaitableGenericTypeDefinition == typeof(Task<>))
            return s_taskIntAwaitMethod;

        if (awaitableGenericTypeDefinition == typeof(ValueTask<>))
            return s_valueTaskIntAwaitMethod;

        throw new ArgumentException(null, nameof(awaitableGenericTypeDefinition));
    }

    private static MethodInfo GetAsyncHelpersAwaitMethod(Type awaitableType) =>
        typeof(AsyncHelpers)
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(m =>
                m.Name == "Await" &&
                !m.IsGenericMethodDefinition &&
                m.GetParameters() is { Length: 1 } parameters &&
                parameters[0].ParameterType == awaitableType);

    private static MethodInfo GetAsyncHelpersAwaitGenericMethod(Type awaitableGenericTypeDefinition) =>
        typeof(AsyncHelpers)
            .GetMethods(BindingFlags.Static | BindingFlags.Public)
            .Single(m =>
                m.Name == "Await" &&
                m.IsGenericMethodDefinition &&
                m.GetParameters() is { Length: 1 } parameters &&
                parameters[0].ParameterType.IsGenericType &&
                parameters[0].ParameterType.GetGenericTypeDefinition() == awaitableGenericTypeDefinition);

    public class PrivateAsync1<T>
    {
        public static int s;
        private static async Task<T> a_task1(int i)
        {
            s++;
            if (i == 0)
            {
                await Task.Yield();
                return default;
            }

            return await Accessors2.accessor<T>(null, i - 1);
        }
    }

    public class PrivateAsync2
    {
        public static int s;
        private static async Task<T> a_task2<T>(int i)
        {
            s++;
            if (i == 0)
            {
                await Task.Yield();
                return default;
            }

            return await Accessors1<T>.accessor(null, i - 1);
        }
    }

    public class Accessors1<T>
    {
        [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "a_task1")]
        public extern static Task<T> accessor(PrivateAsync1<T> o, int i);
    }

    public class Accessors2
    {
        [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "a_task2")]
        public extern static Task<T> accessor<T>(PrivateAsync2 o, int i);
    }

    [Fact]
    public static void UnsafeAccessors()
    {
        PrivateAsync1<int>.s = 0;
        PrivateAsync2.s = 0;

        Accessors2.accessor<int>(null, 7).GetAwaiter().GetResult();
        Assert.Equal(4, PrivateAsync1<int>.s);
        Assert.Equal(4, PrivateAsync2.s);

        Accessors1<int>.accessor(null, 7).GetAwaiter().GetResult();
        Assert.Equal(8, PrivateAsync1<int>.s);
        Assert.Equal(8, PrivateAsync2.s);
    }

    [Fact]
    public static void UnsafeAccessorsAsync()
    {
        UnsafeAccessorsAsyncInner().GetAwaiter().GetResult();
    }

    private static async Task UnsafeAccessorsAsyncInner()
    {
        PrivateAsync1<int>.s = 0;
        PrivateAsync2.s = 0;

        await Accessors2.accessor<int>(null, 7);
        Assert.Equal(4, PrivateAsync1<int>.s);
        Assert.Equal(4, PrivateAsync2.s);

        await Accessors1<int>.accessor(null, 7);
        Assert.Equal(8, PrivateAsync1<int>.s);
        Assert.Equal(8, PrivateAsync2.s);
    }

    [Fact]
    public static void CurrentMethod()
    {
        // Note: async1 leaks implementation details here and returns "Void MoveNext()"
        Assert.Equal("System.Threading.Tasks.Task`1[System.String] GetCurrentMethodAsync()", GetCurrentMethodAsync().Result);
        Assert.Equal("System.Threading.Tasks.Task`1[System.String] GetCurrentMethodAsync()", GetCurrentMethodAwait().Result);

        Assert.Equal("System.Threading.Tasks.Task`1[System.String] GetCurrentMethodTask()", GetCurrentMethodTask().Result);
        Assert.Equal("System.Threading.Tasks.Task`1[System.String] GetCurrentMethodTask()", GetCurrentMethodAwaitTask().Result);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<string> GetCurrentMethodAsync()
    {
        await Task.Yield();
        MethodInfo mi = (MethodInfo)MethodBase.GetCurrentMethod()!;
        return mi.ToString()!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<string> GetCurrentMethodAwait()
    {
        return await GetCurrentMethodAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<string> GetCurrentMethodTask()
    {
        MethodInfo mi = (MethodInfo)MethodBase.GetCurrentMethod()!;
        return Task.FromResult(mi.ToString()!);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<string> GetCurrentMethodAwaitTask()
    {
        return await GetCurrentMethodTask();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [ActiveIssue("https://github.com/dotnet/runtime/issues/122547", typeof(TestLibrary.Utilities), nameof(TestLibrary.Utilities.IsCoreClrInterpreter))]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void FromStack(int level)
    {
        // StackFrame.GetMethod() is not supported on NativeAOT
        if (TestLibrary.Utilities.IsNativeAot)
        {
            return;
        }

        if (level == 0)
        {
            // Note: async1 leaks implementation details here and returns "Void MoveNext()"
            Assert.Equal("System.Threading.Tasks.Task`1[System.String] FromStackAsync(Int32)", FromStackAsync(0).Result);
            Assert.Equal("System.Threading.Tasks.Task`1[System.String] FromStackAsync(Int32)", FromStackAwait(0).Result);

            Assert.Equal("System.Threading.Tasks.Task`1[System.String] FromStackTask(Int32)", FromStackTask(0).Result);
            Assert.Equal("System.Threading.Tasks.Task`1[System.String] FromStackTask(Int32)", FromStackAwaitTask(0).Result);
        }
        else
        {
            // Note: we go through suspend/resume, that is why we see dispatcher as the caller.
            //       we do not see the resume stub though.
            Assert.Equal("Void DispatchContinuations()", FromStackAsync(1).Result);
            Assert.Equal("Void DispatchContinuations()", FromStackAwait(1).Result);

            Assert.Equal("Void FromStack(Int32)", FromStackTask(1).Result);
            // Note: we do not go through suspend/resume, that is why we see the actual caller.
            //       we do not see the async->Task thunk though.
            Assert.Equal("System.Threading.Tasks.Task`1[System.String] FromStackAwaitTask(Int32)", FromStackAwaitTask(1).Result);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<string> FromStackAsync(int level)
    {
        await Task.Yield();
        StackFrame stackFrame = new StackFrame(level);
        MethodInfo mi = (MethodInfo)stackFrame.GetMethod();
        return mi.ToString()!;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<string> FromStackAwait(int level)
    {
        return await FromStackAsync(level);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<string> FromStackTask(int level)
    {
        StackFrame stackFrame = new StackFrame(level);
        MethodInfo mi = (MethodInfo)stackFrame.GetMethod();
        return Task.FromResult(mi.ToString()!);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<string> FromStackAwaitTask(int level)
    {
        return await FromStackTask(level);
    }

    [ActiveIssue("https://github.com/dotnet/runtime/issues/122547", typeof(TestLibrary.Utilities), nameof(TestLibrary.Utilities.IsNativeAot))]
    [ActiveIssue("https://github.com/dotnet/runtime/issues/122547", typeof(TestLibrary.Utilities), nameof(TestLibrary.Utilities.IsCoreClrInterpreter))]
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void FromStackDMI(int level)
    {
        if (level == 0)
        {
            // Note: async1 leaks implementation details here and returns "Void MoveNext()"
            Assert.Equal("FromStackDMIAsync", FromStackDMIAsync(0).Result);
            Assert.Equal("FromStackDMIAsync", FromStackDMIAwait(0).Result);

            Assert.Equal("FromStackDMITask", FromStackDMITask(0).Result);
            Assert.Equal("FromStackDMITask", FromStackDMIAwaitTask(0).Result);
        }
        else
        {
            // Note: we go through suspend/resume, that is why we see dispatcher as the caller.
            //       we do not see the resume stub though.
            Assert.Equal("DispatchContinuations", FromStackDMIAsync(1).Result);
            Assert.Equal("DispatchContinuations", FromStackDMIAwait(1).Result);

            Assert.Equal("FromStackDMI", FromStackDMITask(1).Result);
            // Note: we do not go through suspend/resume, that is why we see the actual caller.
            //       we do not see the async->Task thunk though.
            Assert.Equal("FromStackDMIAwaitTask", FromStackDMIAwaitTask(1).Result);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<string> FromStackDMIAsync(int level)
    {
        await Task.Yield();
        StackFrame stackFrame = new StackFrame(level);
        DiagnosticMethodInfo mi = DiagnosticMethodInfo.Create(stackFrame);
        return mi.Name;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<string> FromStackDMIAwait(int level)
    {
        return await FromStackDMIAsync(level);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<string> FromStackDMITask(int level)
    {
        StackFrame stackFrame = new StackFrame(level);
        DiagnosticMethodInfo mi = DiagnosticMethodInfo.Create(stackFrame);
        return Task.FromResult(mi.Name);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<string> FromStackDMIAwaitTask(int level)
    {
        return await FromStackDMITask(level);
    }

    [Fact]
    public static void EnumerateAll()
    {
        string[] actual = EnumAll.GetAll();
        string[] expected =
            {"Boolean Equals(System.Object)",
                 "Void Finalize()",
                 "System.Threading.Tasks.Task`1[System.Int32] get_P1()",
                 "System.String[] GetAll()",
                 "Int32 GetHashCode()",
                 "System.Type GetType()",
                 "System.Threading.Tasks.Task`1[System.Int32] M1()",
                 "System.Threading.Tasks.Task`1[System.Int32] M2()",
                 "System.Object MemberwiseClone()",
                 "System.String ToString()" };

        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < actual.Length; i++)
        {
            Assert.Equal(actual[i], expected[i]);
        }
    }

    class EnumAll
    {
        public static Task<int> M1() => Task.FromResult(1);

        public async Task<int> M2() => 1;

        public static Task<int> P1 => Task.FromResult(1);

        public static string[] GetAll()
        {
            Type t = typeof(EnumAll);
            List<string> names = new();
            foreach (MethodInfo mi in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static).OrderBy(it => it.Name))
            {
                names.Add(mi.ToString()!);
            }

            return names.ToArray();
        }
    }
}
