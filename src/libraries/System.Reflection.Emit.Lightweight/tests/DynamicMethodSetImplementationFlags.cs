// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace System.Reflection.Emit.Tests
{
    public class DynamicMethodSetImplementationFlagsTests
    {
        private const MethodImplAttributes DefaultImplementationFlags =
            MethodImplAttributes.IL | MethodImplAttributes.NoInlining;

        public static bool IsRuntimeAsyncDynamicMethodSupported =>
            PlatformDetection.IsReflectionEmitSupported && PlatformDetection.IsRuntimeAsyncSupported;

        public static bool IsMonoRuntimeWithReflectionEmitSupported =>
            PlatformDetection.IsMonoRuntime && PlatformDetection.IsReflectionEmitSupported;

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsReflectionEmitSupported))]
        public void GetMethodImplementationFlags_DefaultReportsEffectiveFlags()
        {
            DynamicMethod method = CreateMethod();

            Assert.Equal(DefaultImplementationFlags, method.GetMethodImplementationFlags());
        }

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsReflectionEmitSupported))]
        public void SetImplementationFlags_SupportedNonAsyncFlagsNormalize()
        {
            foreach (MethodImplAttributes attributes in new[]
            {
                MethodImplAttributes.IL,
                MethodImplAttributes.NoInlining,
            })
            {
                DynamicMethod method = CreateMethod();

                method.SetImplementationFlags(attributes);

                Assert.Equal(DefaultImplementationFlags, method.GetMethodImplementationFlags());
            }
        }

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsReflectionEmitSupported))]
        public void SetImplementationFlags_UnsupportedFlagsThrowWithoutChangingEffectiveFlags()
        {
            foreach (MethodImplAttributes attributes in new[]
            {
                MethodImplAttributes.NoOptimization,
                MethodImplAttributes.Synchronized,
                MethodImplAttributes.Native,
            })
            {
                DynamicMethod method = CreateMethod();

                ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
                    method.SetImplementationFlags(attributes));

                Assert.Equal("attributes", exception.ParamName);
                Assert.Equal(DefaultImplementationFlags, method.GetMethodImplementationFlags());
            }
        }

        [ConditionalFact(typeof(DynamicMethodSetImplementationFlagsTests), nameof(IsRuntimeAsyncDynamicMethodSupported))]
        public void SetImplementationFlags_AsyncFlagsNormalize()
        {
            foreach (MethodImplAttributes attributes in new[]
            {
                MethodImplAttributes.Async,
                MethodImplAttributes.NoInlining | MethodImplAttributes.Async,
            })
            {
                DynamicMethod method = CreateMethod();

                method.SetImplementationFlags(attributes);

                Assert.Equal(
                    DefaultImplementationFlags | MethodImplAttributes.Async,
                    method.GetMethodImplementationFlags());
            }
        }

        [ConditionalFact(typeof(DynamicMethodSetImplementationFlagsTests), nameof(IsMonoRuntimeWithReflectionEmitSupported))]
        public void SetImplementationFlags_AsyncOnMonoThrowsPlatformNotSupportedException()
        {
            foreach (MethodImplAttributes attributes in new[]
            {
                MethodImplAttributes.Async,
                MethodImplAttributes.NoInlining | MethodImplAttributes.Async,
            })
            {
                DynamicMethod method = CreateMethod();

                Assert.Throws<PlatformNotSupportedException>(() =>
                    method.SetImplementationFlags(attributes));
                Assert.Equal(DefaultImplementationFlags, method.GetMethodImplementationFlags());
            }
        }

        [ConditionalFact(typeof(PlatformDetection), nameof(PlatformDetection.IsReflectionEmitSupported))]
        public void SetImplementationFlags_AfterCreateDelegateThrowsInvalidOperationExceptionFirst()
        {
            DynamicMethod method = CreateMethod();
            ILGenerator ilGenerator = method.GetILGenerator();
            ilGenerator.Emit(OpCodes.Ldc_I4_0);
            ilGenerator.Emit(OpCodes.Ret);
            _ = method.CreateDelegate(typeof(Func<int>));

            Assert.Throws<InvalidOperationException>(() =>
                method.SetImplementationFlags(MethodImplAttributes.NoOptimization));
            Assert.Throws<InvalidOperationException>(() =>
                method.SetImplementationFlags(MethodImplAttributes.Async));
            Assert.Equal(DefaultImplementationFlags, method.GetMethodImplementationFlags());
        }

        private static DynamicMethod CreateMethod() =>
            new DynamicMethod("ImplementationFlags", typeof(int), Type.EmptyTypes);
    }
}
