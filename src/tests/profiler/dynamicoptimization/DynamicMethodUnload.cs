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
    public static class DynamicMethodUnload
    {
        private static readonly Guid ProfilerClsid =
            new Guid("D57A7F32-6B5F-4D85-9C9B-2A764DF2C151");

        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("RunTest", StringComparison.OrdinalIgnoreCase))
            {
                return RunTest();
            }

            return ProfilerTestRunner.Run(
                profileePath: Assembly.GetExecutingAssembly().Location,
                testName: nameof(DynamicMethodUnload),
                profilerClsid: ProfilerClsid);
        }

        private static int RunTest()
        {
            (WeakReference method, WeakReference @delegate) = CreateExecuteAndRelease();

            for (int attempt = 0; attempt < 50; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                if (!method.IsAlive && !@delegate.IsAlive)
                {
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(20);
                    return 100;
                }

                Thread.Sleep(10);
            }

            Console.WriteLine("FAIL: Runtime-async DynamicMethod remained alive after forced finalization.");
            return -1;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (WeakReference Method, WeakReference Delegate) CreateExecuteAndRelease()
        {
            DynamicMethod dynamicMethod = new DynamicMethod(
                "RuntimeAsyncProfilerUnloadTarget",
                typeof(Task<int>),
                Type.EmptyTypes,
                typeof(DynamicMethodUnload).Module);
            dynamicMethod.SetImplementationFlags(MethodImplAttributes.Async);

            ILGenerator ilGenerator = dynamicMethod.GetILGenerator();
            ilGenerator.Emit(OpCodes.Ldc_I4, 42);
            ilGenerator.Emit(OpCodes.Ret);

            Func<Task<int>> @delegate = dynamicMethod.CreateDelegate<Func<Task<int>>>();
            if (@delegate().GetAwaiter().GetResult() != 42)
            {
                throw new InvalidOperationException("Runtime-async DynamicMethod returned an unexpected value.");
            }

            return (new WeakReference(dynamicMethod), new WeakReference(@delegate));
        }
    }
}
