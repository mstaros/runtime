// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace System.Runtime.CompilerServices
{
    public static partial class AsyncHelpers
    {
        // Deliberately internal and opt-in. The caller must own a quiescent, never-completing
        // IValueTaskSource<int>. This is an exact-build experiment, not a persistence API.
        // A record is [method index, state, pointer-free schema, boxed live values].
        internal static unsafe object[][] CaptureCheckpointForTesting(Task<int> task, DynamicMethod[] methods)
        {
            if (task is not RuntimeAsyncTask<int> runtimeTask || task.IsCompleted ||
                runtimeTask.CheckpointProofState is not ValueTaskSourceContinuation { Next: { } current })
                throw new NotSupportedException("Expected a quiescent runtime-async ValueTask source.");

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
                        out _, out IntPtr type, out IntPtr resume);
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

                ValidateCheckpointSchema(schema);
                if (((uint)current.Flags & ~7u) != ((uint)schema[2] & ~7u) ||
                    (current.Flags & (ContinuationFlags.ContinueOnCapturedSynchronizationContext |
                                      ContinuationFlags.ContinueOnCapturedTaskScheduler)) != 0 ||
                    (current.TryGetExecutionContext(out var context) && context != null) ||
                    ((((uint)current.Flags >> 6) & 3) != 0 && current.GetContinuationContext() != null))
                    throw new NotSupportedException("Captured contexts are outside the checkpoint proof.");

                var values = new object?[schema[6]];
                ref byte data = ref RuntimeHelpers.GetRawData(current);
                for (int i = 0; i < values.Length; i++)
                {
                    ref byte slot = ref Unsafe.Add(ref data, schema[7 + i * 3]);
                    object? value = schema[8 + i * 3] == 1
                        ? Unsafe.ReadUnaligned<int>(ref slot)
                        : Unsafe.As<byte, object?>(ref slot);
                    ValidateCheckpointValue(schema[8 + i * 3], value);
                    values[i] = value;
                }

                frames.Add(new object[] { methodIndex, current.State, schema, values });
                current = current.Next;
            }
            GC.KeepAlive(methods);
            GC.KeepAlive(task);
            return frames.ToArray();
        }

        internal static unsafe Task<int> RestoreCheckpointForTesting(
            object[][] records, DynamicMethod[] methods, int awaitedResult)
        {
            if (records.Length == 0 || records.Length > 16)
                throw new NotSupportedException("Invalid checkpoint frame count.");

            Continuation? first = null;
            Continuation? last = null;
            foreach (object[] record in records)
            {
                if (record.Length != 4 || record[0] is not int index ||
                    (uint)index >= (uint)methods.Length || record[1] is not int state ||
                    record[2] is not int[] savedSchema || record[3] is not object?[] values)
                    throw new NotSupportedException("Malformed checkpoint record.");

                int[] schema = GetCheckpointInfo(methods[index], state,
                    out object keepAlive, out IntPtr type, out IntPtr resume);
                ValidateCheckpointSchema(schema);
                if (!schema.AsSpan().SequenceEqual(savedSchema) || values.Length != schema[6])
                    throw new NotSupportedException("Checkpoint layout does not match the recreated method.");
                for (int i = 0; i < values.Length; i++)
                    ValidateCheckpointValue(schema[8 + i * 3], values[i]);

                var current = (Continuation)RuntimeTypeHandle.InternalAllocNoChecks((MethodTable*)type);
                current.State = state;
                current.Flags = (ContinuationFlags)((uint)schema[2] & ~7u);
                current.ResumeInfo = (ResumeInfo*)resume;
                ref byte data = ref RuntimeHelpers.GetRawData(current);
                Unsafe.As<byte, object?>(ref Unsafe.Add(ref data, schema[4])) = keepAlive;
                for (int i = 0; i < values.Length; i++)
                {
                    ref byte slot = ref Unsafe.Add(ref data, schema[7 + i * 3]);
                    if (schema[8 + i * 3] == 1)
                        Unsafe.WriteUnaligned(ref slot, (int)values[i]!);
                    else
                        Unsafe.As<byte, object?>(ref slot) = values[i];
                }
                if (last == null)
                    first = current;
                else
                    last.Next = current;
                last = current;
            }

            // Recreate the pending await as a typed completed result. The ordinary dispatcher
            // injects it into the first frame and then resumes all callers.
            var head = new RuntimeAsyncTaskContinuation { Next = first };
            head.Initialize(Task.FromResult(awaitedResult));
            var task = new RuntimeAsyncTask<int>();
            task.CheckpointProofState = head;
            task.ExecuteDirectly(null);
            GC.KeepAlive(methods);
            return task;
        }

        private static void ValidateCheckpointValue(int kind, object? value)
        {
            if ((kind == 1 && value is int) || (kind == 2 && (value == null || value is string)))
                return;
            throw new NotSupportedException("Checkpoint proof supports only Int32 and null/string live values.");
        }

        private static void ValidateCheckpointSchema(int[] schema)
        {
            if (IntPtr.Size != 8 || schema.Length < 7 || schema[0] != 1 ||
                schema[3] < 24 || schema[3] > 1024 * 1024 || schema[5] != 1 ||
                schema[6] < 0 || schema[6] > 1024 || schema.Length != 7 + schema[6] * 3 ||
                schema[4] < 24 || schema[4] > schema[3] - 8 || schema[4] % 8 != 0 ||
                (((uint)schema[2] >> 8) & 7) != 0)
                throw new NotSupportedException("Unsupported checkpoint frame layout.");

            uint resultIndex = (uint)schema[2] >> 11;
            if (resultIndex == 0 || 24L + (resultIndex - 1L) * 8 > schema[3] - 4)
                throw new NotSupportedException("Missing Int32 result slot.");
            for (int i = 0; i < schema[6]; i++)
            {
                int offset = schema[7 + i * 3];
                int kind = schema[8 + i * 3];
                if ((kind != 1 && kind != 2) || offset < 24 ||
                    offset > schema[3] - (kind == 1 ? 4 : 8) || (kind == 2 && offset % 8 != 0))
                    throw new NotSupportedException("Unsupported live slot in checkpoint.");
            }
        }

        private static int[] GetCheckpointInfo(DynamicMethod method, int state,
            out object keepAlive, out IntPtr type, out IntPtr resume)
        {
            if (method.ReturnType != typeof(Task<int>))
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
