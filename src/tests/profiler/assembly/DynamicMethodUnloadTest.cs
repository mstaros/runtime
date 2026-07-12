// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Profiler.Tests
{
    internal static class DynamicMethodUnloadTest
    {
        private static readonly Guid ProfilerGuid = new Guid("D57A7F32-6B5F-4D85-9C9B-2A764DF2C151");

        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("RunTest", StringComparison.OrdinalIgnoreCase))
            {
                return RunTest();
            }

            return ProfilerTestRunner.Run(
                profileePath: Assembly.GetExecutingAssembly().Location,
                testName: nameof(DynamicMethodUnloadTest),
                profilerClsid: ProfilerGuid,
                envVarProfilerPrefix: "DOTNET");
        }

        private static int RunTest()
        {
            (WeakReference method, WeakReference @delegate) = CreateExecuteAndRelease();

            for (int attempt = 0; attempt < 40 && (method.IsAlive || @delegate.IsAlive); attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Sleep(10);
            }

            if (method.IsAlive || @delegate.IsAlive)
            {
                Console.WriteLine("Runtime-async DynamicMethod was not collected.");
                return -1;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(100);
            return 100;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (WeakReference Method, WeakReference Delegate) CreateExecuteAndRelease()
        {
            DynamicMethod dynamicMethod = new DynamicMethod(
                "RuntimeAsyncProfilerUnloadTarget",
                typeof(Task<int>),
                Type.EmptyTypes,
                typeof(DynamicMethodUnloadTest));
            dynamicMethod.SetImplementationFlags(MethodImplAttributes.Async);

            ILGenerator ilGenerator = dynamicMethod.GetILGenerator();
            ilGenerator.Emit(OpCodes.Ldc_I4, 42);
            ilGenerator.Emit(OpCodes.Ret);

            Func<Task<int>> @delegate = dynamicMethod.CreateDelegate<Func<Task<int>>>();
            int result = @delegate().GetAwaiter().GetResult();
            if (result != 42)
            {
                throw new InvalidOperationException($"Unexpected result: {result}.");
            }

            return (new WeakReference(dynamicMethod), new WeakReference(@delegate));
        }
    }
}
