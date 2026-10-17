// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Runtime.CompilerServices
{
    public static partial class AsyncHelpers
    {
        // Deliberately internal and opt-in. The caller must own a quiescent, never-completing
        // IValueTaskSource<int>. This is an exact-build experiment, not a persistence API.
        // A record is [method index, state, pointer-free schema, boxed live values].
        internal static object[][] CaptureCheckpointForTesting(Task<int> task, DynamicMethod[] methods)
            => CaptureCheckpointCore(task, methods, null);

        // A single guarded script frame. The trusted test host owns the stopped await and
        // encodes only its known runtime bindings; this is not a general object serializer.
        internal static object[][] CaptureHostCheckpointForTesting<T>(
            Task<T> task, DynamicMethod[] methods, Func<object, object> encodeBinding)
        {
            ArgumentNullException.ThrowIfNull(encodeBinding);
            return CaptureCheckpointCore(task, methods, encodeBinding);
        }

        private static unsafe object[][] CaptureCheckpointCore<T>(
            Task<T> task, DynamicMethod[] methods, Func<object, object>? encodeBinding)
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
                for (int i = 0; i < methods.Length; i++)
                {
                    int[] candidate = GetCheckpointInfo(methods[i], current.State,
                        out _, out IntPtr type, out IntPtr resume, host);
                    if (resume == (IntPtr)current.ResumeInfo &&
                        type == (IntPtr)RuntimeHelpers.GetMethodTable(current))
                    {
                        methodIndex = i;
                        schema = candidate;
                        break;
                    }
                }
                if (schema == null)
                    throw new NotSupportedException("Continuation does not belong to the supplied methods.");

                ValidateCheckpointSchema(schema, host);
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
                ref byte data = ref RuntimeHelpers.GetRawData(current);
                for (int i = 0; i < values.Length; i++)
                {
                    ref byte slot = ref Unsafe.Add(ref data, schema[7 + i * 3]);
                    object? value = schema[8 + i * 3] == 1
                        ? Unsafe.ReadUnaligned<int>(ref slot)
                        : Unsafe.As<byte, object?>(ref slot);
                    ValidateCheckpointValue(schema[8 + i * 3], value, host);
                    values[i] = host && value != null && schema[8 + i * 3] != 1
                        ? encodeBinding!(value) : value;
                }

                frames.Add(host
                    ? new object[] { methodIndex, current.State, schema, values, context == null ? null! : encodeBinding!(context) }
                    : new object[] { methodIndex, current.State, schema, values });
                current = current.Next;
            }
            if (host && frames.Count != 1)
                throw new NotSupportedException("Host checkpoint proof requires exactly one script frame.");
            GC.KeepAlive(methods);
            GC.KeepAlive(task);
            return frames.ToArray();
        }

        internal static Task<int> RestoreCheckpointForTesting(
            object[][] records, DynamicMethod[] methods, int awaitedResult)
            => RestoreCheckpointCore<int>(records, methods, awaitedResult, null);

        internal static Task<T> RestoreHostCheckpointForTesting<T>(
            object[][] records, DynamicMethod[] methods, int awaitedResult, Func<object, object> decodeBinding)
        {
            ArgumentNullException.ThrowIfNull(decodeBinding);
            if (records.Length != 1)
                throw new NotSupportedException("Host checkpoint proof requires exactly one script frame.");
            return RestoreCheckpointCore<T>(records, methods, awaitedResult, decodeBinding);
        }

        private static unsafe Task<T> RestoreCheckpointCore<T>(
            object[][] records, DynamicMethod[] methods, int awaitedResult, Func<object, object>? decodeBinding)
        {
            bool host = decodeBinding != null;
            if (records.Length == 0 || records.Length > 16)
                throw new NotSupportedException("Invalid checkpoint frame count.");

            Continuation? first = null;
            Continuation? last = null;
            foreach (object[] record in records)
            {
                if (record.Length != (host ? 5 : 4) || record[0] is not int index ||
                    (uint)index >= (uint)methods.Length || record[1] is not int state ||
                    record[2] is not int[] savedSchema || record[3] is not object?[] values)
                    throw new NotSupportedException("Malformed checkpoint record.");

                if (host && methods[index].ReturnType != typeof(Task<T>))
                    throw new NotSupportedException("Checkpoint task result type does not match the script.");
                int[] schema = GetCheckpointInfo(methods[index], state,
                    out object keepAlive, out IntPtr type, out IntPtr resume, host);
                ValidateCheckpointSchema(schema, host);
                if (!schema.AsSpan().SequenceEqual(savedSchema) || values.Length != schema[6])
                    throw new NotSupportedException("Checkpoint layout does not match the recreated method.");
                var restoredValues = new object?[values.Length];
                for (int i = 0; i < values.Length; i++)
                {
                    object? value = values[i];
                    if (host && schema[8 + i * 3] != 1 && value != null)
                        value = decodeBinding!(value);
                    ValidateCheckpointValue(schema[8 + i * 3], value, host);
                    restoredValues[i] = value;
                }
                ExecutionContext? context = null;
                if (host && record[4] != null)
                    context = decodeBinding!(record[4]) as ExecutionContext
                        ?? throw new NotSupportedException("Host did not restore an execution context.");

                var current = (Continuation)RuntimeTypeHandle.InternalAllocNoChecks((MethodTable*)type);
                current.State = state;
                current.Flags = (ContinuationFlags)((uint)schema[2] & ~7u);
                current.ResumeInfo = (ResumeInfo*)resume;
                ref byte data = ref RuntimeHelpers.GetRawData(current);
                Unsafe.As<byte, object?>(ref Unsafe.Add(ref data, schema[4])) = keepAlive;
                int contextOffset = CheckpointFlagOffset(schema, ContinuationFlags.ExecutionContextIndexFirstBit, ContinuationFlags.ExecutionContextIndexNumBits);
                if (contextOffset != 0)
                    Unsafe.As<byte, ExecutionContext?>(ref Unsafe.Add(ref data, contextOffset)) = context;
                else if (context != null)
                    throw new NotSupportedException("Unexpected execution context.");
                for (int i = 0; i < values.Length; i++)
                {
                    ref byte slot = ref Unsafe.Add(ref data, schema[7 + i * 3]);
                    if (schema[8 + i * 3] == 1)
                        Unsafe.WriteUnaligned(ref slot, (int)restoredValues[i]!);
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
            task.ExecuteDirectly(null);
            GC.KeepAlive(methods);
            return task;
        }

        private static void ValidateCheckpointValue(int kind, object? value, bool host = false)
        {
            if ((kind == 1 && value is int) || (kind == 2 && (value == null || value is string || host)))
                return;
            throw new NotSupportedException("Checkpoint proof supports only Int32 and null/string live values.");
        }

        private static void ValidateCheckpointSchema(int[] schema, bool host = false)
        {
            if (IntPtr.Size != 8 || schema.Length < 7 || schema[0] != 1 ||
                schema[3] < 24 || schema[3] > 1024 * 1024 || (schema[5] != 1 && (!host || schema[5] != 3)) ||
                schema[6] < 0 || schema[6] > 1024 || schema.Length != 7 + schema[6] * 3 ||
                schema[4] < 24 || schema[4] > schema[3] - 8 || schema[4] % 8 != 0 ||
                (!host && (((uint)schema[2] >> 8) & 7) != 0))
                throw new NotSupportedException("Unsupported checkpoint frame layout.");

            CheckpointFlagOffset(schema, ContinuationFlags.ExecutionContextIndexFirstBit, ContinuationFlags.ExecutionContextIndexNumBits);
            CheckpointFlagOffset(schema, ContinuationFlags.ContinuationContextIndexFirstBit, ContinuationFlags.ContinuationContextIndexNumBits);
            CheckpointFlagOffset(schema, ContinuationFlags.ExceptionIndexFirstBit, ContinuationFlags.ExceptionIndexNumBits);
            uint resultIndex = (uint)schema[2] >> 11;
            if (schema[5] == 1 && (resultIndex == 0 || 24L + (resultIndex - 1L) * 8 > schema[3] - 4))
                throw new NotSupportedException("Missing Int32 result slot.");
            for (int i = 0; i < schema[6]; i++)
            {
                int offset = schema[7 + i * 3];
                int kind = schema[8 + i * 3];
                if ((kind != 1 && kind != 2) || offset < 24 ||
                    offset > schema[3] - (kind == 1 ? 4 : 8) || (kind != 1 && offset % 8 != 0))
                    throw new NotSupportedException("Unsupported live slot in checkpoint: kind " + kind + ", IL variable " + schema[9 + i * 3] + ".");
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

        private static int[] GetCheckpointInfo(DynamicMethod method, int state,
            out object keepAlive, out IntPtr type, out IntPtr resume, bool host = false)
        {
            if (host
                ? !method.ReturnType.IsGenericType || method.ReturnType.GetGenericTypeDefinition() != typeof(Task<>)
                : method.ReturnType != typeof(Task<int>))
                throw new NotSupportedException("Checkpoint proof requires Task<Int32> methods.");
            int[] schema = null!;
            keepAlive = null!;
            GetCheckpointInfoForTesting(method.GetMethodDescriptor().Value, state,
                ObjectHandleOnStack.Create(ref schema), ObjectHandleOnStack.Create(ref keepAlive),
                out type, out resume);
            GC.KeepAlive(method);
            return schema;
        }

        private sealed partial class RuntimeAsyncTask<T>
        {
            internal Continuation? CheckpointProofState
            {
                get => m_stateObject as Continuation;
                set => m_stateObject = value;
            }
        }

        [LibraryImport(RuntimeHelpers.QCall, EntryPoint = "AsyncHelpers_GetCheckpointInfoForTesting")]
        private static partial void GetCheckpointInfoForTesting(IntPtr method, int state,
            ObjectHandleOnStack schema, ObjectHandleOnStack keepAlive,
            out IntPtr continuationType, out IntPtr resumeInfo);
    }
}
