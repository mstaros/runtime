// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace System.Runtime.CompilerServices
{
    public static partial class AsyncHelpers
    {
        private const int CheckpointHeaderSize = 11;
        private const int CheckpointSlotSize = 7;
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

        // Extended host proof for a mixed script/DLL continuation chain. Metadata methods must be closed and
        // may be collectible; exact DLL bytes are host-pinned. Instantiations of shared generic code are told
        // apart by their saved generic context. A suspended ValueTask<T> method's ValueTask wraps such a task,
        // which its AsTask() returns.
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

            ContinuationFlags notifierFlags = head is ValueTaskSourceContinuation source
                ? source.CheckpointSchedulingFlags : head != current ? head.Flags : 0;
            if ((notifierFlags & (ContinuationFlags.ContinueOnCapturedSynchronizationContext |
                                  ContinuationFlags.ContinueOnCapturedTaskScheduler)) != 0)
                throw new NotSupportedException("A pending notifier requires a captured scheduling context.");

            var frames = new List<object[]>();
            var scheduling = new List<bool>();
            var bindings = new List<(object?[] Values, int Index)>();
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
                        out _, out IntPtr type, out IntPtr resume, out Type?[] candidateTypes, out _,
                        out IntPtr candidateMethodContext, out IntPtr candidateClassContext, out int sharedCode, host, (IntPtr)current.ResumeInfo);
                    if (candidate == null || resume != (IntPtr)current.ResumeInfo ||
                        type != (IntPtr)RuntimeHelpers.GetMethodTable(current))
                        continue;
                    // Instantiations of shared generic code share resume addresses; only the saved
                    // instantiation context decides which of them owns this continuation.
                    ValidateCheckpointSchema(candidate, host);
                    if (sharedCode != 0 && !IsCheckpointInstantiation(candidate, current, methods[i], candidateMethodContext, candidateClassContext,
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
                // No binding reaches the host codec until every frame has passed its layout and slot policy.
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
                    // A promoted variable whose type holds references is handed over whole, as one box of that type:
                    // its first slot carries the box and its other slots nothing.
                    if (CheckpointPromotedVariable(schema, i, methods[methodIndex]) is Type variableType)
                    {
                        if (!host)
                            throw new NotSupportedException("A checkpoint value slot needs a host binding.");
                        if (CheckpointVariableFirstSlot(schema, i) == i)
                        {
                            values[i] = ReadCheckpointPromotedVariable(schema, i, variableType, ref data);
                            if (values[i] != null) bindings.Add((values, i));
                        }
                        continue;
                    }
                    // A value is boxed as its declared type and a token handed over whole, for the host to bind; an
                    // empty Nullable<T> boxes as null and needs no binding.
                    if (CheckpointValueSlotType(schema, i, methods[methodIndex], slotTypes[i]) is Type valueType)
                    {
                        if (!host)
                            throw new NotSupportedException("A checkpoint value slot needs a host binding.");
                        object? boxed = valueType == typeof(CancellationToken) ? ReadCheckpointToken(schema, i, ref slot)
                            : RuntimeHelpers.Box(ref slot, valueType.TypeHandle);
                        values[i] = boxed;
                        if (boxed != null) bindings.Add((values, i));
                        continue;
                    }
                    object? value = schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize] == 1
                        ? Unsafe.ReadUnaligned<int>(ref slot)
                        : Unsafe.As<byte, object?>(ref slot);
                    ValidateCheckpointValue(schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize], value, host);
                    ValidateCheckpointReference(schema, i, slotTypes[i], value, declared[i]);
                    if (schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize] == 2 && value != null)
                        referenceTypes[i] = CheckpointBoundTypeIdentity(value.GetType(), methods);
                    values[i] = value;
                    if (host && value != null && schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize] != 1)
                        bindings.Add((values, i));
                }

                string identity = CheckpointMethodIdentity(methods[methodIndex]);
                scheduling.Add(((current.Flags | notifierFlags) & ContinuationFlags.ContinueOnThreadPool) != 0);
                notifierFlags = 0;
                frames.Add(host
                    ? new object[] { methodIndex, current.State, schema, values, context!, identity, referenceTypes }
                    : new object[] { methodIndex, current.State, schema, values, identity, referenceTypes });
                if (host && context != null) bindings.Add((frames[^1], 4));
                current = current.Next;
            }
            if (host && !allowMultipleFrames && frames.Count != 1)
                throw new NotSupportedException("Host checkpoint proof requires exactly one script frame.");
            int identityIndex = host ? 5 : 4;
            for (int f = 0; f < frames.Count; f++)
            {
                object[] frame = frames[f];
                frame[identityIndex] = CheckpointFramePrefix(f, frames.Count, (int)frame[1], scheduling[f]) + (string)frame[identityIndex];
            }
            foreach (var (values, index) in bindings)
                values[index] = encodeBinding!(values[index]!);
            GC.KeepAlive(methods);
            GC.KeepAlive(task);
            return frames.ToArray();
        }

        internal static Task<int> RestoreCheckpointForTesting(
            object[][] records, DynamicMethod[] methods, int awaitedResult, Action<object>? observeFrame = null)
            => RestoreCheckpointCore<int>(records, methods, Task.FromResult(awaitedResult), null, observeFrame);

        internal static Task<T> RestoreHostCheckpointForTesting<T>(
            object[][] records, DynamicMethod[] methods, int awaitedResult, Func<object, object> decodeBinding, Action<int[]>? validateSlots = null)
        {
            ArgumentNullException.ThrowIfNull(decodeBinding);
            if (records.Length != 1)
                throw new NotSupportedException("Host checkpoint proof requires exactly one script frame.");
            return RestoreCheckpointCore<T>(records, methods, Task.FromResult(awaitedResult), decodeBinding, validateSlots: validateSlots);
        }

        internal static Task<T> RestoreMethodCheckpointForTesting<T>(
            object[][] records, MethodInfo[] methods, int awaitedResult, Func<object, object> decodeBinding, Action<int[]>? validateSlots = null)
        {
            ArgumentNullException.ThrowIfNull(decodeBinding);
            return RestoreCheckpointCore<T>(records, methods, Task.FromResult(awaitedResult), decodeBinding, validateSlots: validateSlots);
        }

        // Restores a node whose first frame resumes from a completed awaited outcome: a result, a fault or a
        // cancellation, as that task carries it. Frames may return Task, Task<X>, ValueTask or ValueTask<X>;
        // each caller must await exactly what its callee returns, and the node's task produces what its
        // outermost frame returns.
        internal static Task<T> RestoreMethodCheckpointWithOutcomeForTesting<T>(
            object[][] records, MethodInfo[] methods, Task awaited, Func<object, object> decodeBinding, Action<int[]>? validateSlots = null)
        {
            ArgumentNullException.ThrowIfNull(awaited);
            ArgumentNullException.ThrowIfNull(decodeBinding);
            return RestoreCheckpointCore<T>(records, methods, awaited, decodeBinding, validateSlots: validateSlots, deliverVoidOutcome: true);
        }

        private static unsafe Task<T> RestoreCheckpointCore<T>(
            object[][] records, MethodInfo[] methods, Task awaited, Func<object, object>? decodeBinding, Action<object>? observeFrame = null, Action<int[]>? validateSlots = null,
            bool deliverVoidOutcome = false)
        {
            bool host = decodeBinding != null;
            if (records.Length == 0 || records.Length > 16)
                throw new NotSupportedException("Invalid checkpoint frame count.");

            // What each frame runs and awaits, for the checks across frames below.
            var frameMethods = new MethodInfo[records.Length];
            var frameKinds = new int[records.Length];
            var frameAwaited = new Type?[records.Length];
            var layouts = new (object KeepAlive, IntPtr Type, IntPtr Resume, IntPtr MethodContext, IntPtr ClassContext,
                Type?[] SlotTypes, bool[] Declared)[records.Length];
            for (int f = 0; f < records.Length; f++)
            {
                object[] record = records[f];
                if (record is null || record.Length != (host ? 7 : 6) || record[0] is not int index ||
                    (uint)index >= (uint)methods.Length || record[1] is not int state ||
                    record[2] is not int[] savedSchema || record[3] is not object?[] values ||
                    record[host ? 5 : 4] is not string identity || record[host ? 6 : 5] is not string?[] referenceTypes)
                    throw new NotSupportedException("Malformed checkpoint record.");

                // A host binds frames only through instances it holds. A method of a context already told to
                // unload is a stale binding: restoring it would run a retiring instance's code.
                if (methods[index] is not DynamicMethod)
                    ValidateCheckpointDestination(methods[index]);
                int[] schema = GetCheckpointInfo(methods[index], state,
                    out object keepAlive, out IntPtr type, out IntPtr resume, out Type?[] slotTypes, out Type? awaitedType,
                    out IntPtr methodContext, out IntPtr classContext, out _, host)!;
                int separator = identity.IndexOf(':');
                if (separator < 0 || !identity.AsSpan(separator + 1).SequenceEqual(CheckpointMethodIdentity(methods[index])))
                    throw new NotSupportedException("Checkpoint method identity does not match the recreated method.");
                ValidateCheckpointSchema(schema, host);
                if (savedSchema.Length >= CheckpointHeaderSize && (schema[8] != savedSchema[8] || schema[9] != savedSchema[9]))
                    throw new NotSupportedException("Checkpoint JIT configuration differs.");
                if (!schema.AsSpan().SequenceEqual(savedSchema) || values.Length != schema[6] || referenceTypes.Length != values.Length)
                    throw new NotSupportedException("Checkpoint layout does not match the recreated method.");
                validateSlots?.Invoke((int[])schema.Clone());
                frameMethods[f] = methods[index];
                frameKinds[f] = schema[5];
                frameAwaited[f] = awaitedType;
                slotTypes = CheckpointSlotTypes(schema, slotTypes, methods[index], out bool[] declared);
                layouts[f] = (keepAlive, type, resume, methodContext, classContext, slotTypes, declared);
                for (int i = 0; i < values.Length; i++)
                {
                    int kind = schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize];
                    if (kind == 3 && values[i] != null)
                        throw new NotSupportedException("A saved generic context slot carries a value.");
                    if (CheckpointPromotedVariable(schema, i, methods[index]) is Type variable)
                    {
                        ValidateCheckpointPromotedField(schema, i, variable);
                        if ((CheckpointVariableFirstSlot(schema, i) != i && values[i] != null) || referenceTypes[i] != null)
                            throw new NotSupportedException("Invalid promoted field payload.");
                    }
                    else
                    {
                        Type? valueType = CheckpointValueSlotType(schema, i, methods[index], slotTypes[i]);
                        if ((valueType != null || kind != 2 || values[i] == null) && referenceTypes[i] != null)
                            throw new NotSupportedException("Unexpected checkpoint reference identity.");
                        if (valueType == null && kind == 1 && values[i] is not int)
                            throw new NotSupportedException("Invalid checkpoint Int32 slot.");
                    }
                }
                if (host && record[4] != null && CheckpointFlagOffset(schema, ContinuationFlags.ExecutionContextIndexFirstBit,
                        ContinuationFlags.ExecutionContextIndexNumBits) == 0)
                    throw new NotSupportedException("Unexpected execution context.");
            }

            // Each caller resumes with what its callee returns, written into the caller's typed result slot, so
            // the callee's result type must be exactly what the caller awaits. The task produces the outermost
            // frame's result; for a ValueTask<T> frame the host wraps it, as the runtime's own thunk does.
            if (host)
            {
                for (int f = 1; f < records.Length; f++)
                    ValidateCheckpointAwait(frameKinds[f], frameAwaited[f], CheckpointReturnType(frameMethods[f - 1]));
                if (frameMethods[^1].ReturnType != typeof(Task<T>) && frameMethods[^1].ReturnType != typeof(ValueTask<T>))
                    throw new NotSupportedException("Checkpoint task result type does not match the outermost frame.");
            }

            // Recreate the pending await as a completed task. The ordinary dispatcher delivers its result into
            // the first frame, or throws its fault or cancellation at that await and routes it to the nearest
            // frame with a handler, then resumes all callers. Without an outcome to deliver, a void await is a
            // script frame's awaiter suspension, which reads its result from a host-rebound awaiter slot.
            RuntimeAsyncTaskContinuation? outcome = null;
            if (frameKinds[0] != 3 || deliverVoidOutcome)
            {
                outcome = new RuntimeAsyncTaskContinuation();
                InitializeCheckpointOutcome(outcome, frameKinds[0], frameAwaited[0], awaited);
            }

            // Ordinal, length and suspension state bind a frame to its position even when all result types match.
            // The trusted host authenticates the whole ordered snapshot, including its run and generation.
            for (int f = 0; f < records.Length; f++)
            {
                string identity = (string)records[f][host ? 5 : 4];
                if (!identity.StartsWith(CheckpointFramePrefix(f, records.Length, (int)records[f][1], identity[0] == 'P'),
                        StringComparison.Ordinal))
                    throw new NotSupportedException("Checkpoint frame order, count or suspension state differs from capture.");
            }

            Continuation? first = null;
            Continuation? last = null;
            for (int f = 0; f < records.Length; f++)
            {
                object[] record = records[f];
                int index = (int)record[0], state = (int)record[1];
                int[] schema = (int[])record[2];
                object?[] values = (object?[])record[3];
                string?[] referenceTypes = (string?[])record[host ? 6 : 5];
                var (keepAlive, type, resume, methodContext, classContext, slotTypes, declared) = layouts[f];
                var restoredValues = new object?[values.Length];
                var valueTypes = new Type?[values.Length];
                var variables = new Type?[values.Length];
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
                    // A promoted variable whose type holds references comes back as one box of exactly that type,
                    // carried by its first slot.
                    if (CheckpointPromotedVariable(schema, i, methods[index]) is Type variableType)
                    {
                        ValidateCheckpointPromotedField(schema, i, variableType);
                        if (CheckpointVariableFirstSlot(schema, i) == i)
                        {
                            object? box = host && value != null ? decodeBinding!(value) : null;
                            Type? underlying = Nullable.GetUnderlyingType(variableType);
                            if (box == null ? underlying == null || value != null : box.GetType() != (underlying ?? variableType))
                                throw new NotSupportedException("A restored value differs from its slot's declared type " + variableType + ".");
                            if (underlying != null)
                            {
                                object storage = RuntimeTypeHandle.InternalAllocNoChecks((MethodTable*)variableType.TypeHandle.Value);
                                CastHelpers.Unbox_Nullable(ref RuntimeHelpers.GetRawData(storage), (MethodTable*)variableType.TypeHandle.Value, box);
                                box = storage;
                            }
                            restoredValues[i] = box;
                        }
                        else if (value != null)
                            throw new NotSupportedException("A promoted field carries a value of its own.");
                        if (referenceTypes[i] != null)
                            throw new NotSupportedException("A restored value differs from its slot's declared type " + variableType + ".");
                        variables[i] = variableType;
                        continue;
                    }
                    // A value must come back as a box of exactly its declared type (T, or null for an empty
                    // Nullable<T>), and a token as a CancellationToken.
                    if (CheckpointValueSlotType(schema, i, methods[index], slotTypes[i]) is Type valueType)
                    {
                        object? bound = host && value != null ? decodeBinding!(value) : null;
                        Type boxedType = Nullable.GetUnderlyingType(valueType) ?? valueType;
                        if ((bound == null ? boxedType == valueType || value != null : bound.GetType() != boxedType) ||
                            referenceTypes[i] != null)
                            throw new NotSupportedException("A restored value differs from its slot's declared type " + valueType + ".");
                        restoredValues[i] = bound;
                        valueTypes[i] = valueType;
                        continue;
                    }
                    if (host && schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize] != 1 && value != null)
                        value = decodeBinding!(value);
                    ValidateCheckpointValue(schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize], value, host);
                    ValidateCheckpointReference(schema, i, slotTypes[i], value, declared[i]);
                    string? actualType = schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize] == 2 && value != null
                        ? CheckpointBoundTypeIdentity(value.GetType(), methods) : null;
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
                current.Flags = (ContinuationFlags)schema[2] & ~ContinuationFlags.AllContinuationFlags;
                if (((string)record[host ? 5 : 4])[0] == 'P') current.Flags |= ContinuationFlags.ContinueOnThreadPool;
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
                    if (variables[i] != null)
                        CopyCheckpointField(ref slot, ref Unsafe.Add(ref RuntimeHelpers.GetRawData(restoredValues[CheckpointVariableFirstSlot(schema, i)]!),
                            schema[CheckpointHeaderSize + 5 + i * CheckpointSlotSize]), kind, schema[CheckpointHeaderSize + 6 + i * CheckpointSlotSize]);
                    else if (valueTypes[i] is Type valueType)
                        WriteCheckpointValue(ref slot, kind, valueType, restoredValues[i], schema[CheckpointHeaderSize + 6 + i * CheckpointSlotSize]);
                    else if (kind == 1)
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

            // A direct resume bypasses the metadata method prologue. Restore the CLR entry invariant
            // for precise-init types after all frame checks, before running any resumed instructions.
            foreach (MethodInfo method in frameMethods)
                if (method.DeclaringType is { } owner && (owner.Attributes & TypeAttributes.BeforeFieldInit) == 0)
                    InitializeCheckpointType(owner);
            observeFrame?.Invoke(first!);
            Continuation root = first!;
            if (outcome != null)
            {
                outcome.Next = first;
                root = outcome;
            }
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

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2059",
            Justification = "The exact-build checkpoint experiment uses the host-retained, untrimmed declaring assembly.")]
        private static void InitializeCheckpointType(Type owner) => RuntimeHelpers.RunClassConstructor(owner.TypeHandle);

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

        // What a value slot of a metadata frame restores as: its declared type, restored from a box of that type, or
        // CancellationToken, bound by the host whether the JIT saves it whole (kind 5) or promotes its single field.
        // Null for an Int32, reference or generic-context slot. Admission follows the variable's declared type, not
        // the JIT's choice to promote it: a promoted field is admitted only as part of a variable admitted whole, and
        // a promoted variable whose type holds references is handed over whole (CheckpointPromotedVariable).
        private static Type? CheckpointValueSlotType(int[] schema, int index, MethodInfo method, Type? jitType)
        {
            int kind = schema[CheckpointHeaderSize + 1 + index * CheckpointSlotSize];
            int origin = schema[CheckpointHeaderSize + 4 + index * CheckpointSlotSize];
            if (kind == 3 || (kind <= 2 && origin != 2))
                return null;
            if (method is DynamicMethod)
            {
                // A script frame's variables have no declared types here; it keeps Int32 and reference slots.
                if (kind >= 4)
                    throw new NotSupportedException("A script frame's value slot is outside this checkpoint proof.");
                return null;
            }
            int size = schema[CheckpointHeaderSize + 6 + index * CheckpointSlotSize];
            if (origin == 2)
            {
                Type? variable = CheckpointVariableType(schema, index, method);
                if (variable == typeof(CancellationToken))
                    return typeof(CancellationToken);
                if (variable == null || !IsCheckpointValueType(variable))
                    throw new NotSupportedException("A field of a live " + (variable?.ToString() ?? "unidentified variable") +
                        " is outside this checkpoint proof: native-sized integers, pointers and ref structs are not restored.");
                if (kind <= 2)
                    return null;
            }
            Type? declared = CheckpointDeclaredSlotType(schema, index, method);
            if (kind == 5)
            {
                if (declared == typeof(CancellationToken) && jitType == typeof(CancellationToken))
                    return typeof(CancellationToken);
                if (declared == null || declared == typeof(CancellationToken) || !IsCheckpointValueType(declared, references: true) ||
                    (jitType != null && jitType != declared) || RuntimeHelpers.SizeOf(declared.TypeHandle) != size)
                    throw new NotSupportedException("A live struct with references is outside this checkpoint proof: " +
                        (declared?.ToString() ?? "unidentified") + ". Native-sized integers, pointers and ref structs are not restored.");
                return declared;
            }
            if (declared == null || !IsCheckpointValueType(declared) || (jitType != null && jitType != declared) ||
                RuntimeHelpers.SizeOf(declared.TypeHandle) != size)
                throw new NotSupportedException("A live value is outside this checkpoint proof: " + (declared?.ToString() ?? "unidentified") +
                    ". Native-sized integers, pointers and references are not restored from bytes.");
            return declared;
        }

        // Restored from their bytes: primitives other than native-sized integers, enums over them, and structs whose
        // fields all qualify. With references, a struct's fields may also hold references, which the host binds. A
        // native-sized integer or a pointer may be a handle, and a ref struct cannot be boxed.
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("ReflectionAnalysis", "IL2070:UnrecognizedReflectionPattern",
            Justification = "Opt-in checkpoint proof: a live variable's type and its fields are rooted by the frame being captured or restored.")]
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("ReflectionAnalysis", "IL2075:UnrecognizedReflectionPattern",
            Justification = "Opt-in checkpoint proof: a live variable's type and its fields are rooted by the frame being captured or restored.")]
        private static bool IsCheckpointValueType(Type type, bool references = false, int depth = 0)
        {
            if (type.IsPointer || type.IsFunctionPointer || !type.IsValueType || type.IsByRefLike || depth > 16)
                return false;
            if (type.IsEnum)
                type = Enum.GetUnderlyingType(type);
            if (type.IsPrimitive)
                return type != typeof(IntPtr) && type != typeof(UIntPtr);
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Type fieldType = field.FieldType;
                if (references && !fieldType.IsValueType && !fieldType.IsPointer && !fieldType.IsFunctionPointer && !fieldType.IsByRef)
                    continue;
                if (!IsCheckpointValueType(fieldType, references, depth + 1))
                    return false;
            }
            return true;
        }

        // A CancellationToken is a single reference to its source, saved whole (kind 5) or as that promoted field.
        private static CancellationToken ReadCheckpointToken(int[] schema, int index, ref byte slot) =>
            schema[CheckpointHeaderSize + 1 + index * CheckpointSlotSize] == 5
                ? Unsafe.As<byte, CancellationToken>(ref slot)
                : new CancellationToken(Unsafe.As<byte, CancellationTokenSource?>(ref slot));

        // A promoted variable whose type holds references reaches the host whole, as one box of its declared type, so
        // the host decides on that type whatever the JIT promotes. Null for any other slot. CancellationToken keeps its
        // binding; Nullable<T> is presented to the host using normal CLR boxing.
        private static Type? CheckpointPromotedVariable(int[] schema, int index, MethodInfo method)
        {
            if (schema[CheckpointHeaderSize + 4 + index * CheckpointSlotSize] != 2 || method is DynamicMethod)
                return null;
            Type? variable = CheckpointVariableType(schema, index, method);
            if (variable == null || !variable.IsValueType || variable == typeof(CancellationToken) || !CheckpointHasReferences(variable))
                return null;
            if (!IsCheckpointValueType(variable, references: true))
                throw new NotSupportedException("A field of a live " + variable +
                    " is outside this checkpoint proof: native-sized integers, pointers and ref structs are not restored.");
            return variable;
        }

        private static unsafe bool CheckpointHasReferences(Type type) => ((MethodTable*)type.TypeHandle.Value)->ContainsGCPointers;

        // A promoted variable's slots share its IL variable; the first of them carries its box.
        private static int CheckpointVariableFirstSlot(int[] schema, int index)
        {
            int il = schema[CheckpointHeaderSize + 2 + index * CheckpointSlotSize];
            for (int j = 0; j < index; j++)
            {
                if (schema[CheckpointHeaderSize + 4 + j * CheckpointSlotSize] == 2 && schema[CheckpointHeaderSize + 2 + j * CheckpointSlotSize] == il)
                    return j;
            }
            return index;
        }

        // A promoted variable as one box of its declared type: each saved field is copied to its offset, and a field
        // the JIT did not save keeps its default.
        private static unsafe object? ReadCheckpointPromotedVariable(int[] schema, int first, Type variable, ref byte data)
        {
            object box = RuntimeTypeHandle.InternalAllocNoChecks((MethodTable*)variable.TypeHandle.Value);
            ref byte target = ref RuntimeHelpers.GetRawData(box);
            int il = schema[CheckpointHeaderSize + 2 + first * CheckpointSlotSize];
            for (int j = first; j < schema[6]; j++)
            {
                if (schema[CheckpointHeaderSize + 4 + j * CheckpointSlotSize] != 2 || schema[CheckpointHeaderSize + 2 + j * CheckpointSlotSize] != il)
                    continue;
                CopyCheckpointField(ref Unsafe.Add(ref target, ValidateCheckpointPromotedField(schema, j, variable)),
                    ref Unsafe.Add(ref data, schema[CheckpointHeaderSize + j * CheckpointSlotSize]),
                    schema[CheckpointHeaderSize + 1 + j * CheckpointSlotSize], schema[CheckpointHeaderSize + 6 + j * CheckpointSlotSize]);
            }
            return Nullable.GetUnderlyingType(variable) == null ? box : RuntimeHelpers.Box(ref target, variable.TypeHandle);
        }

        // A saved field must lie within its variable, and its kind must agree with where the variable's type keeps
        // references: a reference exactly at one, a value clear of them, a struct with references aligned over them.
        private static int ValidateCheckpointPromotedField(int[] schema, int index, Type variable)
        {
            int kind = schema[CheckpointHeaderSize + 1 + index * CheckpointSlotSize];
            int fieldOffset = schema[CheckpointHeaderSize + 5 + index * CheckpointSlotSize];
            int size = schema[CheckpointHeaderSize + 6 + index * CheckpointSlotSize];
            var references = new List<int>();
            CheckpointReferenceOffsets(variable, 0, references, 0);
            bool covers = references.Exists(offset => offset >= fieldOffset && offset < fieldOffset + size);
            if (fieldOffset > RuntimeHelpers.SizeOf(variable.TypeHandle) - size ||
                (kind == 2 ? !references.Contains(fieldOffset) : kind == 5 ? !covers || fieldOffset % 8 != 0 || size % 8 != 0 : covers))
                throw new NotSupportedException("A promoted field of " + variable + " does not match its type's layout.");
            return fieldOffset;
        }

        // The offsets at which a value type keeps references, through its nested structs.
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("ReflectionAnalysis", "IL2070:UnrecognizedReflectionPattern",
            Justification = "Opt-in checkpoint proof: a live variable's type and its fields are rooted by the frame being captured or restored.")]
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("ReflectionAnalysis", "IL2075:UnrecognizedReflectionPattern",
            Justification = "Opt-in checkpoint proof: a live variable's type and its fields are rooted by the frame being captured or restored.")]
        private static void CheckpointReferenceOffsets(Type type, int baseOffset, List<int> offsets, int depth)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field is not RtFieldInfo runtimeField)
                    continue;
                int offset = baseOffset + RuntimeFieldHandle.GetInstanceFieldOffset(runtimeField);
                Type fieldType = field.FieldType;
                if (!fieldType.IsValueType)
                    offsets.Add(offset);
                else if (!fieldType.IsPrimitive && !fieldType.IsEnum && depth < 16)
                    CheckpointReferenceOffsets(fieldType, offset, offsets, depth + 1);
            }
        }

        // One saved value: a reference through a typed store, a struct with references with write barriers, and
        // anything else as its bytes.
        private static void CopyCheckpointField(ref byte destination, ref byte source, int kind, int size)
        {
            if (kind == 2)
                Unsafe.As<byte, object?>(ref destination) = Unsafe.As<byte, object?>(ref source);
            else if (kind == 5)
                Buffer.BulkMoveWithWriteBarrier(ref destination, ref source, (nuint)size);
            else
                Unsafe.CopyBlockUnaligned(ref destination, ref source, (uint)size);
        }

        // Writes a restored value: a token through a typed store, a Nullable<T> from its T or null, and any other
        // value from its box.
        private static unsafe void WriteCheckpointValue(ref byte slot, int kind, Type valueType, object? value, int size)
        {
            if (valueType == typeof(CancellationToken))
            {
                CancellationToken token = (CancellationToken)value!;
                if (kind == 5)
                    Unsafe.As<byte, CancellationToken>(ref slot) = token;
                else
                    Unsafe.As<byte, CancellationTokenSource?>(ref slot) = Unsafe.As<CancellationToken, CancellationTokenSource?>(ref token);
            }
            else if (Nullable.GetUnderlyingType(valueType) != null)
                CastHelpers.Unbox_Nullable(ref slot, (MethodTable*)valueType.TypeHandle.Value, value);
            else
                CopyCheckpointField(ref slot, ref RuntimeHelpers.GetRawData(value!), kind, size);
        }

        // What a frame's method produces for its caller: the X of its Task<X> or ValueTask<X>, or void for Task or
        // ValueTask. Its async variant returns X whichever of them the method declares.
        private static Type CheckpointReturnType(MethodInfo method)
        {
            Type returnType = method.ReturnType;
            if (returnType == typeof(Task) || returnType == typeof(ValueTask))
                return typeof(void);
            if (returnType.IsGenericType && (returnType.GetGenericTypeDefinition() == typeof(Task<>) ||
                returnType.GetGenericTypeDefinition() == typeof(ValueTask<>)))
                return returnType.GetGenericArguments()[0];
            throw new NotSupportedException("Checkpoint proof requires Task, Task<T>, ValueTask or ValueTask<T> methods.");
        }

        // A frame awaiting a callee frame must await exactly what that callee returns. Shared generic code
        // reports __Canon for a type parameter; the callee's closed result must then have that shape.
        private static void ValidateCheckpointAwait(int kind, Type? awaitedType, Type calleeResult)
        {
            bool matches = kind switch
            {
                1 => calleeResult == typeof(int),
                2 => awaitedType != null && (awaitedType == calleeResult ||
                    (ContainsCanon(awaitedType) && MatchesCanonicalShape(awaitedType, calleeResult))),
                3 => calleeResult == typeof(void),
                _ => false,
            };
            if (!matches)
                throw new NotSupportedException("A checkpoint frame awaits a different type than its callee returns.");
        }

        // Seeds the first frame's await with a completed task. A result is copied into the frame's typed result
        // slot, so the task must be a Task<X> of exactly the awaited type; a void await takes any task.
        private static void InitializeCheckpointOutcome(RuntimeAsyncTaskContinuation head, int kind, Type? awaitedType, Task awaited)
        {
            if (!awaited.IsCompleted)
                throw new NotSupportedException("The awaited outcome must be a completed task.");
            if (kind == 3)
            {
                head.Initialize(awaited);
                return;
            }
            Type expected = kind == 1 ? typeof(int) : kind == 2 && awaitedType != null ? awaitedType
                : throw new NotSupportedException("The first checkpoint frame's await has no known result type.");
            // A shared generic first frame reports __Canon for a type parameter: its exact result type is unknown.
            if (ContainsCanon(expected))
                throw new NotSupportedException("A shared generic first frame awaits a type parameter; its outcome type is not known exactly.");
            if (CheckpointTaskResultType(awaited.GetType()) != expected)
                throw new NotSupportedException("The awaited outcome's type differs from what the first frame awaits.");
            InitializeCheckpointOutcome(expected, head, awaited);
        }

        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("ReflectionAnalysis", "IL2060:MakeGenericMethod",
            Justification = "Opt-in checkpoint proof: the result type is the frame's own awaited type, rooted by the frame's code.")]
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AotAnalysis", "IL3050:RequiresDynamicCode",
            Justification = "Opt-in checkpoint proof on CoreCLR; not used with native AOT.")]
        private static void InitializeCheckpointOutcome(Type resultType, RuntimeAsyncTaskContinuation head, Task awaited) =>
            typeof(AsyncHelpers).GetMethod(nameof(InitializeCheckpointTypedOutcome), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(resultType).Invoke(null, new object[] { head, awaited });

        private static void InitializeCheckpointTypedOutcome<TResult>(RuntimeAsyncTaskContinuation head, Task awaited) =>
            head.Initialize((Task<TResult>)awaited);

        // The X of the Task<X> a task's type derives from; null for a plain Task.
        private static Type? CheckpointTaskResultType(Type type)
        {
            for (Type? current = type; current != null; current = current.BaseType)
            {
                if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Task<>))
                    return current.GetGenericArguments()[0];
            }
            return null;
        }

        private static void ValidateCheckpointSchema(int[] schema, bool host = false)
        {
            if (IntPtr.Size != 8 || schema.Length < CheckpointHeaderSize || schema[0] != 6 || schema[7] < 0 ||
                schema[3] < 24 || schema[3] > 1024 * 1024 || (schema[5] != 1 && (!host || (schema[5] != 2 && schema[5] != 3))) ||
                schema[6] < 0 || schema[6] > 1024 || schema.Length != CheckpointHeaderSize + schema[6] * CheckpointSlotSize ||
                (schema[4] != -1 && (schema[4] < 24 || schema[4] > schema[3] - 8 || schema[4] % 8 != 0)) ||
                (!host && (((uint)schema[2] >> 8) & 7) != 0))
                throw new NotSupportedException("Unsupported checkpoint frame layout.");

            CheckpointFlagOffset(schema, ContinuationFlags.ExecutionContextIndexFirstBit, ContinuationFlags.ExecutionContextIndexNumBits);
            CheckpointFlagOffset(schema, ContinuationFlags.ContinuationContextIndexFirstBit, ContinuationFlags.ContinuationContextIndexNumBits);
            CheckpointFlagOffset(schema, ContinuationFlags.ExceptionIndexFirstBit, ContinuationFlags.ExceptionIndexNumBits);
            uint resultIndex = (uint)schema[2] >> 11;
            if ((schema[5] == 1 || schema[5] == 2) && (resultIndex == 0 || 24L + (resultIndex - 1L) * 8 > schema[3] - (schema[5] == 1 ? 4 : 1)))
                throw new NotSupportedException("Missing result slot.");
            int contexts = 0;
            for (int i = 0; i < schema[6]; i++)
            {
                int offset = schema[CheckpointHeaderSize + i * CheckpointSlotSize];
                int kind = schema[CheckpointHeaderSize + 1 + i * CheckpointSlotSize];
                int origin = schema[CheckpointHeaderSize + 4 + i * CheckpointSlotSize];
                int fieldOffset = schema[CheckpointHeaderSize + 5 + i * CheckpointSlotSize];
                int size = schema[CheckpointHeaderSize + 6 + i * CheckpointSlotSize];
                if ((uint)origin > 6 || fieldOffset < 0 || (origin != 2 && fieldOffset != 0) ||
                    ((origin == 1 || origin == 2) && schema[CheckpointHeaderSize + 2 + i * CheckpointSlotSize] < 0) ||
                    ((origin == 3 || origin == 4) && kind != 2) || ((origin == 5 || origin == 6) != (kind == 3)))
                    throw new NotSupportedException("Invalid checkpoint slot origin.");
                if (kind == 3 && ++contexts > 1)
                    throw new NotSupportedException("A checkpoint frame has more than one generic context slot.");
                if (kind < 1 || kind > 5 || offset < 24 || size <= 0 || offset > schema[3] - size ||
                    (kind == 1 && size != 4) || ((kind == 2 || kind == 3) && size != 8) || (kind != 1 && kind != 4 && offset % 8 != 0))
                    throw new NotSupportedException("Unsupported live slot in checkpoint: kind " + kind + ", IL variable " + schema[CheckpointHeaderSize + 2 + i * CheckpointSlotSize] + ".");
                // A JIT temporary has no variable to restore a value as; Int32 and reference temporaries are compared
                // by position.
                if (kind >= 4 && origin == 0)
                    throw new NotSupportedException("A live value without an identified variable is outside this checkpoint proof: kind " +
                        kind + ", " + size + " bytes.");
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

        private static string CheckpointFramePrefix(int index, int count, int state, bool pool) =>
            string.Create(CultureInfo.InvariantCulture, $"{(pool ? 'P' : 'I')}{index}/{count}/{state}:");

        private static string CheckpointBoundTypeIdentity(Type type, MethodInfo[] methods)
        {
            string identity = DynamicResolver.CheckpointTypeIdentity(type);
            var bindings = new Text.StringBuilder();
            void Add(Type value)
            {
                if (value.HasElementType) { Add(value.GetElementType()!); return; }
                var context = AssemblyLoadContext.GetLoadContext(value.Assembly);
                if (context != AssemblyLoadContext.Default)
                {
                    int anchor = Array.FindIndex(methods, m => AssemblyLoadContext.GetLoadContext(m.Module.Assembly) == context);
                    if (anchor < 0 || context == null || context.IsUnloading)
                        throw new NotSupportedException("Checkpoint value has no live load-context binding in the host method table.");
                    bindings.Append('/').Append(anchor.ToString(CultureInfo.InvariantCulture));
                }
                else
                    bindings.Append("/d");
                if (value.IsGenericType)
                    foreach (Type argument in value.GetGenericArguments()) Add(argument);
            }
            Add(type);
            return identity + bindings.ToString();
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
            // context. Collectible code is admitted too: a frame is matched by its code address, so it names the
            // load that owns that code, and its continuation keeps that load's context alive.
            if (method.DeclaringType == null || method.ContainsGenericParameters)
                throw new NotSupportedException("Metadata checkpoint proof requires a closed method.");
        }

        // Restore refuses a method whose code, or a type of whose instantiation, belongs to a load context that
        // is unloading. Capture does not: a snapshot names methods by identity, never by context.
        private static void ValidateCheckpointDestination(MethodInfo method)
        {
            ValidateCheckpointLoadContext(method.Module.Assembly);
            if (method.DeclaringType is { IsGenericType: true } declaring)
            {
                foreach (Type argument in declaring.GetGenericArguments())
                    ValidateCheckpointArgument(argument);
            }
            if (method.IsGenericMethod)
            {
                foreach (Type argument in method.GetGenericArguments())
                    ValidateCheckpointArgument(argument);
            }
        }

        private static void ValidateCheckpointArgument(Type type)
        {
            while (type.HasElementType)
                type = type.GetElementType()!;
            ValidateCheckpointLoadContext(type.Assembly);
            if (type.IsGenericType)
            {
                foreach (Type argument in type.GetGenericArguments())
                    ValidateCheckpointArgument(argument);
            }
        }

        private static void ValidateCheckpointLoadContext(Assembly assembly)
        {
            if (AssemblyLoadContext.GetLoadContext(assembly) is { IsUnloading: true })
                throw new NotSupportedException("Checkpoint restore binds a method whose load context is unloading.");
        }

        // awaitedType is the exact type a kind-2 await produces (possibly __Canon in shared generic code);
        // null for an Int32 (kind 1) or void (kind 3) await.
        private static int[]? GetCheckpointInfo(MethodInfo method, int state,
            out object keepAlive, out IntPtr type, out IntPtr resume, out Type?[] slotTypes, out Type? awaitedType,
            out IntPtr methodContext, out IntPtr classContext, out int sharedCode, bool host = false, IntPtr expectedResume = default)
        {
            int[] schema = null!;
            slotTypes = null!;
            keepAlive = null!;
            awaitedType = null;
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
                ObjectHandleOnStack.Create(ref slotTypes), ObjectHandleOnStack.Create(ref awaitedType),
                out type, out resume, out methodContext, out classContext, out sharedCode);
            GC.KeepAlive(method);
            // A capture probe must skip nonowners before enforcing the owner's signature.
            if (schema != null)
            {
                if (!host && method.ReturnType != typeof(Task<int>))
                    throw new NotSupportedException("Checkpoint proof requires Task<Int32> methods.");
                if (host)
                    CheckpointReturnType(method);
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
                    return contextOrigin == 5 || !method.IsGenericMethod;
                // Name what the frame received, so a refusal shows which instantiation it was.
                string received = contextOrigin == 5
                    ? RuntimeType.GetMethodBase(null, new RuntimeMethodHandleInternal(saved))?.ToString() ?? "?"
                    : "?";
                mismatch = (mismatch == null ? "" : mismatch + " ") + "For " + method + " the saved generic context 0x" +
                    saved.ToString("X") + " (" + received + ") is not 0x" + expected.ToString("X") + ".";
                return false;
            }
            Type declaring = method.DeclaringType!;
            if (method.IsStatic || method.IsGenericMethod || !declaring.IsGenericType)
                return false;
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
            return false;
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

        // Whether a closed type has a shared generic shape, where __Canon stands for any reference type.
        private static bool MatchesCanonicalShape(Type canonical, Type exact)
        {
            if (canonical == exact)
                return true;
            if (canonical.Assembly == typeof(object).Assembly && canonical.FullName == "System.__Canon")
                return !exact.IsValueType && !exact.IsByRef && !exact.IsPointer;
            if (canonical.HasElementType)
            {
                return exact.HasElementType && canonical.IsSZArray == exact.IsSZArray && canonical.IsArray == exact.IsArray &&
                    (!canonical.IsArray || canonical.GetArrayRank() == exact.GetArrayRank()) &&
                    canonical.IsByRef == exact.IsByRef && canonical.IsPointer == exact.IsPointer &&
                    MatchesCanonicalShape(canonical.GetElementType()!, exact.GetElementType()!);
            }
            if (!canonical.IsGenericType || !exact.IsGenericType || canonical.GetGenericTypeDefinition() != exact.GetGenericTypeDefinition())
                return false;
            Type[] canonicalArguments = canonical.GetGenericArguments();
            Type[] exactArguments = exact.GetGenericArguments();
            for (int i = 0; i < canonicalArguments.Length; i++)
            {
                if (!MatchesCanonicalShape(canonicalArguments[i], exactArguments[i]))
                    return false;
            }
            return true;
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
        private static Type? CheckpointVariableType(int[] schema, int index, MethodInfo method)
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
            return CloseCheckpointType(variable, method);
        }

        // The declared type of a slot's IL variable, or of its promoted field at the slot's field offset.
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("ReflectionAnalysis", "IL2075:UnrecognizedReflectionPattern",
            Justification = "Opt-in checkpoint proof: the closed method, its locals' types and their fields are rooted by the frame being captured or restored.")]
        private static Type? CheckpointDeclaredSlotType(int[] schema, int index, MethodInfo method)
        {
            Type? variable = CheckpointVariableType(schema, index, method);
            if (variable == null || schema[CheckpointHeaderSize + 4 + index * CheckpointSlotSize] == 1)
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
            ObjectHandleOnStack schema, ObjectHandleOnStack keepAlive, ObjectHandleOnStack slotTypes, ObjectHandleOnStack awaitedType,
            out IntPtr continuationType, out IntPtr resumeInfo, out IntPtr methodContext, out IntPtr classContext, out int sharedCode);
    }
}
