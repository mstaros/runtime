// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Runtime.CompilerServices
{
    public static partial class AsyncHelpers
    {
        private const int CheckpointHeaderSize = 11;
        private const int CheckpointSlotSize = 6;
        // Deliberately internal and opt-in. The caller must own a quiescent, never-completing
        // IValueTaskSource<int>. This is an exact-build experiment, not a persistence API.
        // Records also contain a canonical method identity and captured reference types.
        internal static object[][] CaptureCheckpointForTesting(Task<int> task, DynamicMethod[] methods)
            => CaptureCheckpointCore(task, methods, null);

        // A single guarded script frame. The trusted test host owns the stopped await and
        // encodes only its known runtime bindings; this is not a general object serializer.
        internal static object[][] CaptureHostCheckpointForTesting<T>(
            Task<T> task, DynamicMethod[] methods, Func<object, object> encodeBinding, Action<int[]>? validateSlots = null)
        {
            ArgumentNullException.ThrowIfNull(encodeBinding);
            return CaptureCheckpointCore(task, methods, encodeBinding, validateSlots);
        }

        // Extended host proof for a mixed script/DLL continuation chain. Metadata methods must be closed
        // and noncollectible, including their instantiation; exact DLL bytes are host-pinned. Instantiations
        // of shared generic code are told apart by their saved generic context.
        internal static object[][] CaptureMethodCheckpointForTesting<T>(
            Task<T> task, MethodInfo[] methods, Func<object, object> encodeBinding, Action<int[]>? validateSlots = null)
        {
            ArgumentNullException.ThrowIfNull(encodeBinding);
            return CaptureCheckpointCore(task, methods, encodeBinding, validateSlots, allowMultipleFrames: true);
        }

        private static unsafe object[][] CaptureCheckpointCore<T>(
            Task<T> task, MethodInfo[] methods, Func<object, object>? encodeBinding, Action<int[]>? validateSlots = null,
            bool allowMultipleFrames = false)
        {
            bool host = encodeBinding != null;
            if (task is not RuntimeAsyncTask<T> runtimeTask || task.IsCompleted ||
                runtimeTask.CheckpointProofState is not { } head)
                throw new NotSupportedException("Expected a quiescent runtime-async task.");
            Continuation? current;
            if (head is ValueTaskSourceContinuation || (host && head is RuntimeAsyncTaskContinuation))
                current = head.Next;
            else if (host)
                current = head; // Source awaiter lowering suspends directly in the script frame.
            else
                throw new NotSupportedException("Expected a quiescent runtime-async ValueTask source.");
            if (current == null)
                throw new NotSupportedException("The checkpoint contains no suspended script frame.");

            var frames = new List<object[]>();
            while (current != null)
            {
                if (frames.Count == 16)
                    throw new NotSupportedException("Checkpoint proof supports at most 16 frames.");

                int methodIndex = -1;
                int[]? schema = null;
                Type?[] slotTypes = null!;
                IntPtr methodContext = default, classContext = default;
                string? mismatch = null;
                for (int i = 0; i < methods.Length; i++)
                {
                    int[]? candidate = GetCheckpointInfo(methods[i], current.State,
                        out _, out IntPtr type, out IntPtr resume, out Type?[] candidateTypes,
                        out IntPtr candidateMethodContext, out IntPtr candidateClassContext, host, (IntPtr)current.ResumeInfo);
                    if (candidate == null || resume != (IntPtr)current.ResumeInfo ||
                        type != (IntPtr)RuntimeHelpers.GetMethodTable(current))
                        continue;
                    // Instantiations of shared generic code share resume addresses; only the saved
                    // instantiation context decides which of them owns this continuation.
                    ValidateCheckpointSchema(candidate, host);
                    if (!IsCheckpointInstantiation(candidate, current, methods[i], candidateMethodContext, candidateClassContext,
                        ref mismatch))
                        continue;
                    if (schema != null)
                    {
                        if (ReferenceEquals(methods[i], methods[methodIndex]))
                            continue;
                        throw new NotSupportedException("Continuation matches more than one supplied method.");
                    }
                    methodIndex = i;
                    schema = candidate;
                    slotTypes = candidateTypes;
                    methodContext = candidateMethodContext;
                    classContext = candidateClassContext;
                    // Nongeneric code owns its resume addresses alone, so the first owner is the owner; only a
                    // generic owner needs the full scan, which also probes (and may compile) every later method.
                    if (methods[i] is DynamicMethod || !(methods[i].IsGenericMethod || methods[i].DeclaringType!.IsGenericType))
                        break;
                }
                if (schema == null)
                    throw new NotSupportedException("Continuation does not belong to the supplied methods." + (mismatch == null ? "" : " " + mismatch));

                ValidateCheckpointSchema(schema, host);
                slotTypes = CheckpointSlotTypes(schema, slotTypes, methods[methodIndex], out bool[] declared);
                int contextOrigin = CheckpointContextOrigin(schema);
                if (contextOrigin != 0 && schema[4] != -1 &&
                    !ReferenceEquals(Unsafe.As<byte, object?>(ref Unsafe.Add(ref RuntimeHelpers.GetRawData(current), schema[4])),
                        CheckpointContextKeepAlive(contextOrigin, methodContext, classContext)))
                    throw new NotSupportedException("The generic context keep-alive differs from its instantiation.");
                // Validate the complete layout before walking any user-owned object.
                validateSlots?.Invoke((int[])schema.Clone());
                if (((uint)current.Flags & ~7u) != ((uint)schema[2] & ~7u) ||
                    (current.Flags & (ContinuationFlags.ContinueOnCapturedSynchronizationContext |
                                      ContinuationFlags.ContinueOnCapturedTaskScheduler)) != 0 ||
                    (current.TryGetExecutionContext(out var context) && context != null && !host) ||
                    ((((uint)current.Flags >> 6) & 3) != 0 && current.GetContinuationContext() != null))
                    throw new NotSupportedException("Captured contexts are outside the checkpoint proof.");

                if (current.HasException())
                {
                    int exceptionOffset = CheckpointFlagOffset(schema, ContinuationFlags.ExceptionIndexFirstBit, ContinuationFlags.ExceptionIndexNumBits);
                    if (Unsafe.As<byte, object?>(ref Unsafe.Add(ref RuntimeHelpers.GetRawData(current), exceptionOffset)) != null)
                        throw new NotSupportedException("An active exception cannot be checkpointed.");
                }

                var values = new object?[schema[6]];
                var referenceTypes = new string?[values.Length];
                ref byte data = ref RuntimeHelpers.GetRawData(current);
                for (int i = 0; i < values.Length; i++)
                {
                    // The instantiation context was matched above; it is never persisted, and restore
                    // rebinds it from the recreated method.
                    if (schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize] == 3)
                        continue;
                    ref byte slot = ref Unsafe.Add(ref data, schema[CheckpointHeaderSize + i * CheckpointSlotSize]);
                    object? value = schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize] == 1
                        ? Unsafe.ReadUnaligned<int>(ref slot)
                        : Unsafe.As<byte, object?>(ref slot);
                    ValidateCheckpointValue(schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize], value, host);
                    ValidateCheckpointReference(schema, i, slotTypes[i], value, declared[i]);
                    if (schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize] == 2 && value != null)
                        referenceTypes[i] = DynamicResolver.CheckpointTypeIdentity(value.GetType());
                    values[i] = host && value != null && schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize] != 1
                        ? encodeBinding!(value) : value;
                }

                string identity = CheckpointMethodIdentity(methods[methodIndex]);
                frames.Add(host
                    ? new object[] { methodIndex, current.State, schema, values, context == null ? null! : encodeBinding!(context), identity, referenceTypes }
                    : new object[] { methodIndex, current.State, schema, values, identity, referenceTypes });
                current = current.Next;
            }
            if (host && !allowMultipleFrames && frames.Count != 1)
                throw new NotSupportedException("Host checkpoint proof requires exactly one script frame.");
            GC.KeepAlive(methods);
            GC.KeepAlive(task);
            return frames.ToArray();
        }

        internal static Task<int> RestoreCheckpointForTesting(
            object[][] records, DynamicMethod[] methods, int awaitedResult, Action<object>? observeFrame = null)
            => RestoreCheckpointCore<int>(records, methods, awaitedResult, null, observeFrame);

        internal static Task<T> RestoreHostCheckpointForTesting<T>(
            object[][] records, DynamicMethod[] methods, int awaitedResult, Func<object, object> decodeBinding, Action<int[]>? validateSlots = null)
        {
            ArgumentNullException.ThrowIfNull(decodeBinding);
            if (records.Length != 1)
                throw new NotSupportedException("Host checkpoint proof requires exactly one script frame.");
            return RestoreCheckpointCore<T>(records, methods, awaitedResult, decodeBinding, validateSlots: validateSlots);
        }

        internal static Task<T> RestoreMethodCheckpointForTesting<T>(
            object[][] records, MethodInfo[] methods, int awaitedResult, Func<object, object> decodeBinding, Action<int[]>? validateSlots = null)
        {
            ArgumentNullException.ThrowIfNull(decodeBinding);
            return RestoreCheckpointCore<T>(records, methods, awaitedResult, decodeBinding, validateSlots: validateSlots);
        }

        private static unsafe Task<T> RestoreCheckpointCore<T>(
            object[][] records, MethodInfo[] methods, int awaitedResult, Func<object, object>? decodeBinding, Action<object>? observeFrame = null, Action<int[]>? validateSlots = null)
        {
            bool host = decodeBinding != null;
            if (records.Length == 0 || records.Length > 16)
                throw new NotSupportedException("Invalid checkpoint frame count.");

            Continuation? first = null;
            Continuation? last = null;
            foreach (object[] record in records)
            {
                if (record.Length != (host ? 7 : 6) || record[0] is not int index ||
                    (uint)index >= (uint)methods.Length || record[1] is not int state ||
                    record[2] is not int[] savedSchema || record[3] is not object?[] values ||
                    record[host ? 5 : 4] is not string identity || record[host ? 6 : 5] is not string?[] referenceTypes)
                    throw new NotSupportedException("Malformed checkpoint record.");

                if (host && methods[index].ReturnType != typeof(Task<T>))
                    throw new NotSupportedException("Checkpoint task result type does not match the script.");
                int[] schema = GetCheckpointInfo(methods[index], state,
                    out object keepAlive, out IntPtr type, out IntPtr resume, out Type?[] slotTypes,
                    out IntPtr methodContext, out IntPtr classContext, host)!;
                if (identity != CheckpointMethodIdentity(methods[index]))
                    throw new NotSupportedException("Checkpoint method identity does not match the recreated method.");
                ValidateCheckpointSchema(schema, host);
                if (savedSchema.Length >= CheckpointHeaderSize && (schema[8] != savedSchema[8] || schema[9] != savedSchema[9]))
                    throw new NotSupportedException("Checkpoint JIT configuration differs.");
                if (!schema.AsSpan().SequenceEqual(savedSchema) || values.Length != schema[6] || referenceTypes.Length != values.Length)
                    throw new NotSupportedException("Checkpoint layout does not match the recreated method.");
                validateSlots?.Invoke((int[])schema.Clone());
                slotTypes = CheckpointSlotTypes(schema, slotTypes, methods[index], out bool[] declared);
                var restoredValues = new object?[values.Length];
                for (int i = 0; i < values.Length; i++)
                {
                    object? value = values[i];
                    if (schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize] == 3)
                    {
                        // A generic context is rebound from the recreated method, never persisted.
                        if (value != null)
                            throw new NotSupportedException("A saved generic context slot carries a value.");
                        continue;
                    }
                    if (host && schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize] != 1 && value != null)
                        value = decodeBinding!(value);
                    ValidateCheckpointValue(schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize], value, host);
                    ValidateCheckpointReference(schema, i, slotTypes[i], value, declared[i]);
                    string? actualType = schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize] == 2 && value != null
                        ? DynamicResolver.CheckpointTypeIdentity(value.GetType()) : null;
                    if (referenceTypes[i] != actualType)
                        throw new NotSupportedException("Checkpoint reference type differs from capture.");
                    restoredValues[i] = value;
                }
                ExecutionContext? context = null;
                if (host && record[4] != null)
                    context = decodeBinding!(record[4]) as ExecutionContext
                        ?? throw new NotSupportedException("Host did not restore an execution context.");

                var current = (Continuation)RuntimeTypeHandle.InternalAllocNoChecks((MethodTable*)type);
                current.State = state;
                current.Flags = (ContinuationFlags)((uint)schema[2] & ~6u);
                current.ResumeInfo = (ResumeInfo*)resume;
                ref byte data = ref RuntimeHelpers.GetRawData(current);
                if (schema[4] != -1)
                {
                    int contextOrigin = CheckpointContextOrigin(schema);
                    Unsafe.As<byte, object?>(ref Unsafe.Add(ref data, schema[4])) = contextOrigin == 0
                        ? keepAlive : CheckpointContextKeepAlive(contextOrigin, methodContext, classContext);
                }
                int contextOffset = CheckpointFlagOffset(schema, ContinuationFlags.ExecutionContextIndexFirstBit, ContinuationFlags.ExecutionContextIndexNumBits);
                if (contextOffset != 0)
                    Unsafe.As<byte, ExecutionContext?>(ref Unsafe.Add(ref data, contextOffset)) = context;
                else if (context != null)
                    throw new NotSupportedException("Unexpected execution context.");
                for (int i = 0; i < values.Length; i++)
                {
                    ref byte slot = ref Unsafe.Add(ref data, schema[CheckpointHeaderSize + i * CheckpointSlotSize]);
                    int kind = schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize];
                    if (kind == 1)
                        Unsafe.WriteUnaligned(ref slot, (int)restoredValues[i]!);
                    else if (kind == 3)
                        Unsafe.WriteUnaligned(ref slot,
                            schema[CheckpointHeaderSize + 4 + i * CheckpointSlotSize] == 5 ? methodContext : classContext);
                    else
                        Unsafe.As<byte, object?>(ref slot) = restoredValues[i];
                }
                if (last == null)
                    first = current;
                else
                    last.Next = current;
                last = current;
            }

            // Recreate the pending await as a typed completed result. The ordinary dispatcher
            // injects it into the first frame and then resumes all callers.
            observeFrame?.Invoke(first!);
            Continuation root = first!;
            if (((int[])records[0][2])[5] == 1)
            {
                var head = new RuntimeAsyncTaskContinuation { Next = first };
                head.Initialize(Task.FromResult(awaitedResult));
                root = head;
            }
            // A void awaiter suspension reads its result from a host-rebound awaiter slot.
            var task = new RuntimeAsyncTask<T>();
            task.CheckpointProofState = root;
            // A direct script-frame restore also has to honor its scheduling constraint.
            if ((first!.Flags & ContinuationFlags.ContinueOnThreadPool) != 0 &&
                (SynchronizationContext.Current is { } sync && sync.GetType() != typeof(SynchronizationContext) ||
                 TaskScheduler.Current != TaskScheduler.Default))
                ThreadPool.UnsafeQueueUserWorkItemInternal(task, preferLocal: false);
            else
                task.ExecuteDirectly(null);
            GC.KeepAlive(methods);
            return task;
        }

        private static void ValidateCheckpointReference(int[] schema, int index, Type? expected, object? value,
            bool declaredOnly = false)
        {
            if (schema[CheckpointHeaderSize + 1 + index * CheckpointSlotSize] != 2 || value == null)
                return;
            // A declared type from a shared generic slot admits its subtypes.
            bool exact = !declaredOnly && schema[CheckpointHeaderSize + 3 + index * CheckpointSlotSize] != 0;
            if (expected == null || (exact ? value.GetType() != expected : !expected.IsInstanceOfType(value)))
                throw new NotSupportedException("Decoded reference is incompatible with the continuation slot.");
        }

        private static void ValidateCheckpointValue(int kind, object? value, bool host = false)
        {
            if ((kind == 1 && value is int) || (kind == 2 && (value == null || value is string || host)))
                return;
            throw new NotSupportedException("Checkpoint proof supports only Int32 and null/string live values.");
        }

        private static void ValidateCheckpointSchema(int[] schema, bool host = false)
        {
            if (IntPtr.Size != 8 || schema.Length < CheckpointHeaderSize || schema[0] != 4 || schema[7] < 0 ||
                schema[3] < 24 || schema[3] > 1024 * 1024 || (schema[5] != 1 && (!host || schema[5] != 3)) ||
                schema[6] < 0 || schema[6] > 1024 || schema.Length != CheckpointHeaderSize + schema[6] * CheckpointSlotSize ||
                (schema[4] != -1 && (schema[4] < 24 || schema[4] > schema[3] - 8 || schema[4] % 8 != 0)) ||
                (!host && (((uint)schema[2] >> 8) & 7) != 0))
                throw new NotSupportedException("Unsupported checkpoint frame layout.");

            CheckpointFlagOffset(schema, ContinuationFlags.ExecutionContextIndexFirstBit, ContinuationFlags.ExecutionContextIndexNumBits);
            CheckpointFlagOffset(schema, ContinuationFlags.ContinuationContextIndexFirstBit, ContinuationFlags.ContinuationContextIndexNumBits);
            CheckpointFlagOffset(schema, ContinuationFlags.ExceptionIndexFirstBit, ContinuationFlags.ExceptionIndexNumBits);
            uint resultIndex = (uint)schema[2] >> 11;
            if (schema[5] == 1 && (resultIndex == 0 || 24L + (resultIndex - 1L) * 8 > schema[3] - 4))
                throw new NotSupportedException("Missing Int32 result slot.");
            int contexts = 0;
            for (int i = 0; i < schema[6]; i++)
            {
                int offset = schema[CheckpointHeaderSize + i * CheckpointSlotSize];
                int kind = schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize];
                int origin = schema[CheckpointHeaderSize + 4 + i * CheckpointSlotSize];
                int fieldOffset = schema[CheckpointHeaderSize + 5 + i * CheckpointSlotSize];
                if ((uint)origin > 6 || fieldOffset < 0 || (origin != 2 && fieldOffset != 0) ||
                    ((origin == 1 || origin == 2) && schema[CheckpointHeaderSize + 2 + i * CheckpointSlotSize] < 0) ||
                    ((origin == 3 || origin == 4) && kind != 2) || ((origin == 5 || origin == 6) != (kind == 3)))
                    throw new NotSupportedException("Invalid checkpoint slot origin.");
                if (kind == 3 && ++contexts > 1)
                    throw new NotSupportedException("A checkpoint frame has more than one generic context slot.");
                if ((kind != 1 && kind != 2 && kind != 3) || offset < 24 ||
                    offset > schema[3] - (kind == 1 ? 4 : 8) || (kind != 1 && offset % 8 != 0))
                    throw new NotSupportedException("Unsupported live slot in checkpoint: kind " + kind + ", IL variable " + schema[CheckpointHeaderSize + 2 + i * CheckpointSlotSize] + ".");
            }
        }

        private static int CheckpointFlagOffset(int[] schema, ContinuationFlags firstBit, ContinuationFlags numBits)
        {
            uint mask = (1u << (int)numBits) - 1;
            uint index = ((uint)schema[2] >> (int)firstBit) & mask;
            if (index == 0) return 0;
            long offset = 16L + index * 8;
            if (offset < 24 || offset > schema[3] - 8)
                throw new NotSupportedException("Invalid checkpoint binding offset.");
            return (int)offset;
        }

        private static string CheckpointMethodIdentity(MethodInfo method)
        {
            if (method is DynamicMethod dynamicMethod)
                return dynamicMethod._resolver!.GetCheckpointIdentity();
            ValidateCheckpointMethod(method);
            string identity = "metadata:" + method.Module.ModuleVersionId.ToString("D") + ":" +
                method.MetadataToken.ToString("X8") + ":" +
                DynamicResolver.CheckpointTypeIdentity(method.DeclaringType!) + ":" + method.ToString();
            // A generic method's instantiation is part of its identity.
            if (method.IsGenericMethod)
            {
                foreach (Type argument in method.GetGenericArguments())
                    identity += ":" + DynamicResolver.CheckpointTypeIdentity(argument);
            }
            return identity;
        }

        private static void ValidateCheckpointMethod(MethodInfo method)
        {
            // Closed generic instantiations are admitted; their frames are matched through the saved generic
            // context. Collectible code, including an instantiation over a collectible type, is not.
            if (method.DeclaringType == null || method.ContainsGenericParameters || method.IsCollectible)
                throw new NotSupportedException("Metadata checkpoint proof requires a closed, noncollectible method.");
        }

        private static int[]? GetCheckpointInfo(MethodInfo method, int state,
            out object keepAlive, out IntPtr type, out IntPtr resume, out Type?[] slotTypes,
            out IntPtr methodContext, out IntPtr classContext, bool host = false, IntPtr expectedResume = default)
        {
            int[] schema = null!;
            slotTypes = null!;
            keepAlive = null!;
            IntPtr handle;
            if (method is DynamicMethod dynamicMethod)
                handle = dynamicMethod.GetMethodDescriptor().Value;
            else
            {
                ValidateCheckpointMethod(method);
                handle = method.MethodHandle.Value;
            }
            GetCheckpointInfoForTesting(handle, state, expectedResume,
                ObjectHandleOnStack.Create(ref schema), ObjectHandleOnStack.Create(ref keepAlive),
                ObjectHandleOnStack.Create(ref slotTypes), out type, out resume, out methodContext, out classContext);
            GC.KeepAlive(method);
            // A capture probe must skip nonowners before enforcing the owner's signature.
            if (schema != null)
            {
                if (host
                    ? !method.ReturnType.IsGenericType || method.ReturnType.GetGenericTypeDefinition() != typeof(Task<>)
                    : method.ReturnType != typeof(Task<int>))
                    throw new NotSupportedException("Checkpoint proof requires Task<Int32> methods.");
            }
            return schema;
        }

        // The frame's hidden generic-context slot: origin 5 (instantiated method) or 6 (instantiated
        // class), or 0 when the frame has none.
        private static int CheckpointContextOrigin(int[] schema)
        {
            for (int i = 0; i < schema[6]; i++)
            {
                int origin = schema[CheckpointHeaderSize + 4 + i * CheckpointSlotSize];
                if (origin == 5 || origin == 6)
                    return origin;
            }
            return 0;
        }

        // What AllocContinuationMethod and AllocContinuationClass store in the keep-alive field.
        private static unsafe object? CheckpointContextKeepAlive(int origin, IntPtr methodContext, IntPtr classContext)
        {
            if (origin == 5)
                return RuntimeMethodHandle.GetLoaderAllocator(new RuntimeMethodHandleInternal(methodContext));
            IntPtr handle = ((MethodTable*)classContext)->GetLoaderAllocatorHandle();
            return handle == IntPtr.Zero ? null : GCHandle.FromIntPtr(handle).Target;
        }

        // Shared generic instantiations share resume addresses, so a frame of shared code names its
        // instantiation through the hidden generic context or a generic receiver. Other code belongs to
        // a single instantiation; a continuation matching several supplied methods is rejected by the caller.
        private static bool IsCheckpointInstantiation(int[] schema, Continuation continuation, MethodInfo method,
            IntPtr methodContext, IntPtr classContext, ref string? mismatch)
        {
            if (method is DynamicMethod)
                return true;
            ref byte data = ref RuntimeHelpers.GetRawData(continuation);
            int contextOrigin = CheckpointContextOrigin(schema);
            for (int i = 0; contextOrigin != 0 && i < schema[6]; i++)
            {
                if (schema[CheckpointHeaderSize + 4 + i * CheckpointSlotSize] != contextOrigin)
                    continue;
                IntPtr saved = Unsafe.ReadUnaligned<IntPtr>(ref Unsafe.Add(ref data, schema[CheckpointHeaderSize + i * CheckpointSlotSize]));
                IntPtr expected = contextOrigin == 5 ? methodContext : classContext;
                if (saved == expected)
                    return true;
                // Name what the frame received, so a refusal shows which instantiation it was.
                string received = contextOrigin == 5
                    ? RuntimeType.GetMethodBase(null, new RuntimeMethodHandleInternal(saved))?.ToString() ?? "?"
                    : "?";
                mismatch = (mismatch == null ? "" : mismatch + " ") + "For " + method + " the saved generic context 0x" +
                    saved.ToString("X") + " (" + received + ") is not 0x" + expected.ToString("X") + ".";
                return false;
            }
            Type declaring = method.DeclaringType!;
            if (method.IsStatic || !declaring.IsGenericType)
                return true;
            for (int i = 0; i < schema[6]; i++)
            {
                if (schema[CheckpointHeaderSize + 4 + i * CheckpointSlotSize] != 1 ||
                    schema[CheckpointHeaderSize + 2 + i * CheckpointSlotSize] != 0 ||
                    schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize] != 2)
                    continue;
                for (Type? type = Unsafe.As<byte, object?>(ref Unsafe.Add(ref data, schema[CheckpointHeaderSize + i * CheckpointSlotSize]))?.GetType();
                    type != null; type = type.BaseType)
                {
                    if (type == declaring)
                        return true;
                }
                return false;
            }
            return true;
        }

        // Shared generic code reports canonical (__Canon) classes for slots typed by a type parameter.
        // Their declared type follows from the slot's IL variable or promoted field and the closed method,
        // and a value is then checked against that declared type. Unidentified slots stay rejected.
        private static Type?[] CheckpointSlotTypes(int[] schema, Type?[] slotTypes, MethodInfo method, out bool[] declared)
        {
            declared = new bool[slotTypes.Length];
            Type?[]? exact = null;
            for (int i = 0; i < slotTypes.Length; i++)
            {
                if (slotTypes[i] is not Type type || !ContainsCanon(type))
                    continue;
                Type? declaredType = method is DynamicMethod ? null : CheckpointDeclaredSlotType(schema, i, method);
                if (declaredType == null || declaredType.ContainsGenericParameters || ContainsCanon(declaredType))
                    throw new NotSupportedException("A shared generic slot has no identified declared type.");
                exact ??= (Type?[])slotTypes.Clone();
                exact[i] = declaredType;
                declared[i] = true;
            }
            return exact ?? slotTypes;
        }

        private static bool ContainsCanon(Type type)
        {
            // __Canon cannot be named in IL (ldtoken of it is an invalid program), so match its identity.
            if (type.Assembly == typeof(object).Assembly && type.FullName == "System.__Canon")
                return true;
            if (type.HasElementType)
                return ContainsCanon(type.GetElementType()!);
            if (type.IsGenericType)
            {
                foreach (Type argument in type.GetGenericArguments())
                {
                    if (ContainsCanon(argument))
                        return true;
                }
            }
            return false;
        }

        // IL variables number the arguments (the receiver first) and then the locals.
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:RequiresUnreferencedCode",
            Justification = "Opt-in checkpoint proof: the closed method, its locals' types and their fields are rooted by the frame being captured or restored.")]
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("ReflectionAnalysis", "IL2075:UnrecognizedReflectionPattern",
            Justification = "Opt-in checkpoint proof: the closed method, its locals' types and their fields are rooted by the frame being captured or restored.")]
        private static Type? CheckpointDeclaredSlotType(int[] schema, int index, MethodInfo method)
        {
            int il = schema[CheckpointHeaderSize + 2 + index * CheckpointSlotSize];
            int origin = schema[CheckpointHeaderSize + 4 + index * CheckpointSlotSize];
            if ((origin != 1 && origin != 2) || il < 0)
                return null;
            ParameterInfo[] parameters = method.GetParameters();
            int receiver = method.IsStatic ? 0 : 1;
            Type? variable;
            if (il < parameters.Length + receiver)
            {
                variable = il < receiver ? method.DeclaringType : parameters[il - receiver].ParameterType;
            }
            else
            {
                IList<LocalVariableInfo>? locals = method.GetMethodBody()?.LocalVariables;
                int local = il - parameters.Length - receiver;
                if (locals == null || local >= locals.Count)
                    return null;
                variable = locals[local].LocalType;
            }
            if (variable == null)
                return null;
            variable = CloseCheckpointType(variable, method);
            if (origin == 1)
                return variable;
            int fieldOffset = schema[CheckpointHeaderSize + 5 + index * CheckpointSlotSize];
            foreach (FieldInfo field in variable.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field is RtFieldInfo runtimeField && RuntimeFieldHandle.GetInstanceFieldOffset(runtimeField) == fieldOffset)
                    return CloseCheckpointType(field.FieldType, method);
            }
            return null;
        }

        // A closed generic method's local signature may still name its type parameters; substitute them.
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("ReflectionAnalysis", "IL2055:UnrecognizedReflectionPattern",
            Justification = "Opt-in checkpoint proof: the closed method, its locals' types and their fields are rooted by the frame being captured or restored.")]
        private static Type CloseCheckpointType(Type type, MethodInfo method)
        {
            if (!type.ContainsGenericParameters)
                return type;
            if (type.IsGenericParameter)
            {
                return type.DeclaringMethod != null
                    ? method.GetGenericArguments()[type.GenericParameterPosition]
                    : method.DeclaringType!.GetGenericArguments()[type.GenericParameterPosition];
            }
            if (type.IsSZArray)
                return CloseCheckpointType(type.GetElementType()!, method).MakeArrayType();
            if (type.IsArray)
                return CloseCheckpointType(type.GetElementType()!, method).MakeArrayType(type.GetArrayRank());
            if (type.IsByRef)
                return CloseCheckpointType(type.GetElementType()!, method).MakeByRefType();
            if (type.IsPointer)
                return CloseCheckpointType(type.GetElementType()!, method).MakePointerType();
            Type[] arguments = type.GetGenericArguments();
            for (int i = 0; i < arguments.Length; i++)
                arguments[i] = CloseCheckpointType(arguments[i], method);
            return type.GetGenericTypeDefinition().MakeGenericType(arguments);
        }

        internal static int CheckpointPublicationStateForTesting(DynamicMethod method)
        {
            int state = GetCheckpointPublicationStateForTesting(method.GetMethodDescriptor().Value);
            GC.KeepAlive(method);
            return state;
        }

        [LibraryImport(RuntimeHelpers.QCall, EntryPoint = "AsyncHelpers_GetCheckpointPublicationStateForTesting")]
        private static partial int GetCheckpointPublicationStateForTesting(IntPtr method);

        private sealed partial class RuntimeAsyncTask<T>
        {
            internal Continuation? CheckpointProofState
            {
                get => m_stateObject as Continuation;
                set => m_stateObject = value;
            }
        }

        [LibraryImport(RuntimeHelpers.QCall, EntryPoint = "AsyncHelpers_GetCheckpointInfoForTesting")]
        private static partial void GetCheckpointInfoForTesting(IntPtr method, int state, IntPtr expectedResume,
            ObjectHandleOnStack schema, ObjectHandleOnStack keepAlive, ObjectHandleOnStack slotTypes,
            out IntPtr continuationType, out IntPtr resumeInfo, out IntPtr methodContext, out IntPtr classContext);
    }
}
