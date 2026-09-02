// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static System.Runtime.CompilerServices.RuntimeHelpers;

namespace System.Reflection.Emit
{
    public sealed partial class DynamicMethod : MethodInfo
    {
        private RuntimeType[] _parameterTypes;
        internal IRuntimeMethodInfo? _methodHandle;
        private bool IsBaked => _methodHandle != null;
        private MethodImplAttributes _methodImplFlags = DefaultMethodImplAttributes;
        private MethodInfo[]? _runtimeAsyncBuilderMethods;

        private void SetImplementationFlagsCore(MethodImplAttributes attributes) =>
            _methodImplFlags = DefaultMethodImplAttributes | (attributes & MethodImplAttributes.Async);

        private MethodImplAttributes GetMethodImplementationFlagsCore() => _methodImplFlags;

        internal bool IsRuntimeAsync => (_methodImplFlags & MethodImplAttributes.Async) != 0;

        internal byte[] GetRuntimeAsyncMethodSignature(
            out bool isValueTask,
            out RuntimeMethodHandleInternal builderCreateMethod,
            out RuntimeMethodHandleInternal builderTaskGetterMethod,
            out RuntimeMethodHandleInternal builderSetResultMethod,
            out RuntimeMethodHandleInternal builderSetExceptionMethod)
        {
            isValueTask = false;
            builderCreateMethod = RuntimeMethodHandleInternal.EmptyHandle;
            builderTaskGetterMethod = RuntimeMethodHandleInternal.EmptyHandle;
            builderSetResultMethod = RuntimeMethodHandleInternal.EmptyHandle;
            builderSetExceptionMethod = RuntimeMethodHandleInternal.EmptyHandle;

            if (!IsRuntimeAsync)
                return Array.Empty<byte>();

            Type bodyReturnType = GetRuntimeAsyncBodyReturnType(
                _returnType,
                out isValueTask,
                out MethodInfo? builderCreate,
                out MethodInfo? builderTaskGetter,
                out MethodInfo? builderSetResult,
                out MethodInfo? builderSetException);

            if (builderCreate != null)
            {
                Debug.Assert(builderSetResult != null);
                Debug.Assert(builderSetException != null);

                _runtimeAsyncBuilderMethods = builderTaskGetter == null
                    ? [builderCreate, builderSetResult, builderSetException]
                    : [builderCreate, builderTaskGetter, builderSetResult, builderSetException];

                builderCreateMethod = new RuntimeMethodHandleInternal(builderCreate.MethodHandle.Value);
                builderTaskGetterMethod = builderTaskGetter == null
                    ? RuntimeMethodHandleInternal.EmptyHandle
                    : new RuntimeMethodHandleInternal(builderTaskGetter.MethodHandle.Value);
                builderSetResultMethod = new RuntimeMethodHandleInternal(builderSetResult.MethodHandle.Value);
                builderSetExceptionMethod = new RuntimeMethodHandleInternal(builderSetException.MethodHandle.Value);
            }

            // Keep the reflected builder methods rooted for the lifetime of this DynamicMethod.
            GC.KeepAlive(_runtimeAsyncBuilderMethods);
            return SignatureHelper.GetMethodSigHelper(
                null, CallingConvention, bodyReturnType, null, null, _parameterTypes, null, null).GetSignature(true);
        }

        private static Type GetRuntimeAsyncBodyReturnType(
            Type returnType,
            out bool isValueTask,
            out MethodInfo? builderCreate,
            out MethodInfo? builderTaskGetter,
            out MethodInfo? builderSetResult,
            out MethodInfo? builderSetException)
        {
            isValueTask = returnType == typeof(ValueTask);
            builderCreate = null;
            builderTaskGetter = null;
            builderSetResult = null;
            builderSetException = null;

            if (returnType == typeof(Task) || isValueTask)
                return typeof(void);

            if (returnType.IsGenericType)
            {
                Type genericDefinition = returnType.GetGenericTypeDefinition();
                if (genericDefinition == typeof(Task<>) || genericDefinition == typeof(ValueTask<>))
                {
                    isValueTask = genericDefinition == typeof(ValueTask<>);
                    return returnType.GetGenericArguments()[0];
                }
            }

            if (returnType == typeof(void))
            {
                return GetRuntimeAsyncBuilderBodyReturnType(
                    typeof(AsyncVoidMethodBuilder),
                    returnType,
                    requireTaskProperty: false,
                    out builderCreate,
                    out builderTaskGetter,
                    out builderSetResult,
                    out builderSetException);
            }

            AsyncMethodBuilderAttribute? builderAttribute =
                (AsyncMethodBuilderAttribute?)Attribute.GetCustomAttribute(
                    returnType,
                    typeof(AsyncMethodBuilderAttribute),
                    inherit: false);
            if (builderAttribute == null)
                throw new NotSupportedException(SR.NotSupported_DynamicMethodAsyncReturnType);

            Type builderType = builderAttribute.BuilderType;
            if (builderType.IsGenericTypeDefinition)
            {
                Type[] returnTypeArguments = returnType.IsGenericType
                    ? returnType.GetGenericArguments()
                    : Type.EmptyTypes;
                if (builderType.GetGenericArguments().Length != returnTypeArguments.Length)
                    throw new NotSupportedException(SR.NotSupported_DynamicMethodAsyncReturnType);

                try
                {
                    builderType = builderType.MakeGenericType(returnTypeArguments);
                }
                catch (ArgumentException)
                {
                    throw new NotSupportedException(SR.NotSupported_DynamicMethodAsyncReturnType);
                }
                catch (TypeLoadException)
                {
                    throw new NotSupportedException(SR.NotSupported_DynamicMethodAsyncReturnType);
                }
            }

            if (builderType.ContainsGenericParameters || builderType.IsByRefLike)
                throw new NotSupportedException(SR.NotSupported_DynamicMethodAsyncReturnType);

            return GetRuntimeAsyncBuilderBodyReturnType(
                builderType,
                returnType,
                requireTaskProperty: true,
                out builderCreate,
                out builderTaskGetter,
                out builderSetResult,
                out builderSetException);
        }

        private static Type GetRuntimeAsyncBuilderBodyReturnType(
            Type builderType,
            Type returnType,
            bool requireTaskProperty,
            out MethodInfo? builderCreate,
            out MethodInfo? builderTaskGetter,
            out MethodInfo? builderSetResult,
            out MethodInfo? builderSetException)
        {
            const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

            builderCreate = builderType.GetMethod(
                "Create",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            builderTaskGetter = requireTaskProperty
                ? builderType.GetProperty("Task", PublicInstance)?.GetMethod
                : null;
            builderSetException = builderType.GetMethod(
                "SetException",
                PublicInstance,
                binder: null,
                types: [typeof(Exception)],
                modifiers: null);

            if (builderCreate == null ||
                builderCreate.IsGenericMethod ||
                builderCreate.ReturnType != builderType ||
                (requireTaskProperty &&
                    (builderTaskGetter == null ||
                     builderTaskGetter.IsStatic ||
                     builderTaskGetter.ReturnType != returnType)) ||
                builderSetException == null ||
                builderSetException.IsStatic ||
                builderSetException.IsGenericMethod ||
                builderSetException.ReturnType != typeof(void))
            {
                throw new NotSupportedException(SR.NotSupported_DynamicMethodAsyncReturnType);
            }

            builderSetResult = null;
            foreach (MethodInfo method in builderType.GetMethods(PublicInstance))
            {
                if (method.Name != "SetResult" ||
                    method.IsStatic ||
                    method.IsGenericMethod ||
                    method.ReturnType != typeof(void))
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length > 1 ||
                    (parameters.Length == 1 && parameters[0].ParameterType.IsByRef))
                {
                    continue;
                }

                if (builderSetResult != null)
                    throw new NotSupportedException(SR.NotSupported_DynamicMethodAsyncReturnType);

                builderSetResult = method;
            }

            if (builderSetResult == null)
                throw new NotSupportedException(SR.NotSupported_DynamicMethodAsyncReturnType);

            ParameterInfo[] setResultParameters = builderSetResult.GetParameters();
            return setResultParameters.Length == 0
                ? typeof(void)
                : setResultParameters[0].ParameterType;
        }
        private RuntimeType _returnType;
        private DynamicILGenerator? _ilGenerator;
        private DynamicILInfo? _dynamicILInfo;
        private bool _initLocals;
        private Module _module;
        internal bool _skipVisibility;
        internal RuntimeType? _typeOwner;
        private MethodBaseInvoker? _invoker;
        private Signature? _signature;
        private string _name;
        private MethodAttributes _attributes;
        private CallingConventions _callingConvention;
        private RuntimeParameterInfo[]? _parameters;

        // needed to keep the object alive during jitting
        // assigned by the DynamicResolver ctor
        internal DynamicResolver? _resolver;

        internal bool _restrictedSkipVisibility;

        //
        // Delegate and method creation
        //

        public sealed override Delegate CreateDelegate(Type delegateType) =>
            CreateDelegate(delegateType, target: null);

        public sealed override Delegate CreateDelegate(Type delegateType, object? target)
        {
            if (_restrictedSkipVisibility)
            {
                // Compile the method since accessibility checks are done as part of compilation
                GetMethodDescriptor();
                IRuntimeMethodInfo? methodHandle = _methodHandle;
                CompileMethod(methodHandle != null ? methodHandle.Value : RuntimeMethodHandleInternal.EmptyHandle);
                GC.KeepAlive(methodHandle);
            }

            return Delegate.CreateDelegateForDynamicMethod(delegateType, target, GetMethodDescriptor(), this);
        }

        // This is guaranteed to return a valid handle
        internal RuntimeMethodHandle GetMethodDescriptor()
        {
            if (_methodHandle == null)
            {
                lock (this)
                {
                    if (_methodHandle == null)
                    {
                        if (_dynamicILInfo != null)
                            _dynamicILInfo.GetCallableMethod((RuntimeModule)_module, this);
                        else
                        {
                            if (_ilGenerator == null || _ilGenerator.ILOffset == 0)
                                throw new InvalidOperationException(SR.Format(SR.InvalidOperation_BadEmptyMethodBody, Name));

                            _ilGenerator.GetCallableMethod((RuntimeModule)_module, this);
                        }
                    }
                }
            }
            return new RuntimeMethodHandle(_methodHandle!);
        }

        private MethodBaseInvoker Invoker
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return _invoker ??= new MethodBaseInvoker(this, Signature);
            }
        }

        internal Signature Signature
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                [MethodImpl(MethodImplOptions.NoInlining)] // move lazy sig generation out of the hot path
                Signature LazyCreateSignature()
                {
                    Debug.Assert(_methodHandle != null);
                    Debug.Assert(_parameterTypes != null);

                    Signature newSig = new Signature(_methodHandle, _parameterTypes, _returnType, CallingConvention);
                    Volatile.Write(ref _signature, newSig);
                    return newSig;
                }

                return _signature ?? LazyCreateSignature();
            }
        }

        public override object? Invoke(object? obj, BindingFlags invokeAttr, Binder? binder, object?[]? parameters, CultureInfo? culture)
        {
            if ((CallingConvention & CallingConventions.VarArgs) == CallingConventions.VarArgs)
                throw new NotSupportedException(SR.NotSupported_CallToVarArg);

            //
            // We do not demand any permission here because the caller already has access
            // to the current DynamicMethod object, and it could just as easily emit another
            // Transparent DynamicMethod to call the current DynamicMethod.
            //

            _ = GetMethodDescriptor();
            // ignore obj since it's a static method

            // verify arguments
            int argCount = (parameters != null) ? parameters.Length : 0;
            if (Signature.Arguments.Length != argCount)
                throw new TargetParameterCountException(SR.Arg_ParmCnt);
            object? retValue = argCount switch
            {
                0 => Invoker.InvokeWithNoArgs(obj, invokeAttr),
                1 => Invoker.InvokeWithOneArg(obj, invokeAttr, binder, parameters!, culture),
                2 or 3 or 4 => Invoker.InvokeWithFewArgs(obj, invokeAttr, binder, parameters!, culture),
                _ => Invoker.InvokeWithManyArgs(obj, invokeAttr, binder, parameters!, culture),
            };
            GC.KeepAlive(this);
            return retValue;
        }

        public DynamicILInfo GetDynamicILInfo()
        {
            if (_dynamicILInfo == null)
            {
                byte[] methodSignature = SignatureHelper.GetMethodSigHelper(
                        null, CallingConvention, ReturnType, null, null, _parameterTypes, null, null).GetSignature(true);
                _dynamicILInfo = new DynamicILInfo(this, methodSignature);
            }
            return _dynamicILInfo;
        }

        public ILGenerator GetILGenerator(int streamSize)
        {
            if (_ilGenerator == null)
            {
                byte[] methodSignature = SignatureHelper.GetMethodSigHelper(
                    null, CallingConvention, ReturnType, null, null, _parameterTypes, null, null).GetSignature(true);
                _ilGenerator = new DynamicILGenerator(this, methodSignature, streamSize);
            }
            return _ilGenerator;
        }
    }
}
