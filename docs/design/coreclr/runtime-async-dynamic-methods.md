# Runtime Async Dynamic Methods

Status: implemented design for issue #118074.

## Summary

`DynamicMethod` can opt into runtime async by setting `MethodImplAttributes.Async` before the method is baked. The generated method remains assemblyless from the caller's perspective, while CoreCLR represents it as a paired ordinary Task/ValueTask-returning facade and an async-call body descriptor. The JIT applies the existing runtime-async transformation to the emitted IL; `DynamicMethod` does not synthesize a C#-style state machine.

## Public API and flag contract

```csharp
public sealed class DynamicMethod : MethodInfo
{
    public void SetImplementationFlags(MethodImplAttributes attributes);
}
```

The effective default is `MethodImplAttributes.IL | MethodImplAttributes.NoInlining`.

`SetImplementationFlags` accepts the existing no-op representations of `IL` and `NoInlining`, plus the optional `Async` bit. The effective value is normalized to `IL | NoInlining`, with `Async` added when requested. Every other nonzero implementation bit is rejected with `ArgumentOutOfRangeException` rather than being stored without effect.

The method implementation flags become immutable when the dynamic method is baked. Calling the setter after delegate creation, invocation, or another operation that creates the runtime method descriptor throws `InvalidOperationException` before validating the requested bits.

`GetMethodImplementationFlags`, `MethodImplAttribute` reflection, and `IsDefined` report the effective normalized flags.

## Valid runtime-async signatures

A runtime-async dynamic method must return one of:

- `Task`
- `ValueTask`
- `Task<T>`
- `ValueTask<T>`

The async body signature is derived from the public signature:

- `Task` and `ValueTask` become `void`;
- `Task<T>` and `ValueTask<T>` become `T`;
- parameters and calling convention are preserved.

Validation occurs when the runtime descriptor is requested. An `Async` dynamic method with another return type fails with `NotSupportedException`.

## Runtime representation

A runtime-async dynamic method is represented by two `DynamicMethodDesc` instances in the module's dynamic method table:

1. **Ordinary facade** — keeps the public Task/ValueTask-returning signature and is marked `ReturnsTaskOrValueTask | Thunk`.
2. **Async body** — uses the unwrapped return signature and is marked `AsyncCall | IsAsyncVariant`, with `IsAsyncVariantForValueTask` when applicable.

The pair shares one synthetic `mdMethodDef` token and one module. The token does not name a metadata row; it is a bounded identity used by the existing ordinary/async variant lookup. Synthetic RIDs are allocated under the dynamic-method-table lock and are capped at the 24-bit MethodDef RID limit. Exhaustion fails with `OverflowException` before descriptor checkout, wraparound, or collision. Ordinary non-async dynamic methods continue to use `mdMethodDefNil`.

The emitted IL and managed resolver belong to the async body. The ordinary facade receives a separate resolver handle because either descriptor may be queried or JITed independently, but the pair represents one managed `DynamicMethod` lifetime.

## JIT and ABI behavior

Variant lookup uses the shared module/token identity plus `AsyncVariantLookup`. The ordinary facade's synthetic thunk calls the async body using the runtime-async calling convention. When the async body is compiled, the EE supplies the emitted dynamic IL through its resolver and sets the runtime-async method options expected by the JIT.

The runtime-async ABI adds the continuation input/output used by suspension and resumption. Non-async callers enter through the ordinary Task/ValueTask facade. Async-aware callers can resolve and call the async body directly. Await recognition, context handling, continuation layout, exception propagation, and return-value transport remain the responsibilities described by the general runtime-async specification and code-generator contract.

## Construction and publication

Descriptor checkout, signatures, names, resolver handles, reflection stub allocation, and collectible-loader ownership are exception-safe.

Construction keeps ownership in native holders until every throwing allocation completes. The descriptors are returned to the free list on failure, native buffers remain owned by their array holders, and long-weak handles remain owned by handle holders. Only after successful construction are the buffers, handles, and descriptors published to the managed method object.

A runtime-async pair acquires one collectible `LoaderAllocator` reference because it represents one managed `DynamicMethod`, not two independently collectible methods.

## Destruction and recycling

Finalization treats the pair as one ownership unit while retiring both descriptors:

1. discover the paired descriptor without creating a new associate;
2. under `FEATURE_PORTABLE_ENTRYPOINTS`, clear pending thunk-resolution state for both descriptors before recycling;
3. emit ETW/EventPipe destruction and profiler unload notifications for both descriptors;
4. retire code-heap allocations for both resolvers;
5. release both names and signatures;
6. destroy both resolvers and return both descriptors to the free list;
7. release the single collectible-loader reference.

If code-heap retirement cannot complete immediately, destruction is deferred and retried by the finalizer thread. Lifecycle notifications are emitted once, before the retryable native cleanup phase.

## Platform behavior

CoreCLR implements runtime-async `DynamicMethod` support when runtime async and Reflection.Emit are available.

Mono supports `DynamicMethod` but not this runtime-async path, so setting `Async` throws `PlatformNotSupportedException` and leaves the effective flags unchanged. NativeAOT does not support creating `DynamicMethod` and retains its existing platform-not-supported behavior.

The method is assemblyless in the Reflection.Emit sense: callers do not create or retain a dynamic assembly. Internally the descriptor still belongs to a runtime module. That module and its CoreLib provide the runtime-async capability boundary required by the general specification; no synthetic user-visible metadata type or assembly is introduced.

## IL contract

The IL producer must emit valid runtime-async IL. The runtime does not infer async intent from arbitrary IL and does not repair invalid stack, await, exception-region, byref, or suspension shapes. Invalid input fails through the same import, validation, JIT, or execution paths as invalid metadata-backed runtime-async methods.

Both `ILGenerator` and `DynamicILInfo` are supported. The public method signature remains Task/ValueTask-returning even though the emitted body follows the unwrapped runtime-async return convention.

## Static IL verification

The metadata-backed IL verifier treats `MethodImplAttributes.Async` as semantic input. At a `ret` instruction, an async method is checked against the unwrapped public return type:

- `Task` and `ValueTask` require an empty evaluation stack.
- `Task<T>` and `ValueTask<T>` require one value assignable to `T`.

A method marked `Async` with another public return type is invalid. A method without the `Async` bit retains the ordinary ECMA-335 return-stack rule and must return a value assignable to its declared return type.

A live `DynamicMethod` cannot be passed directly to ILVerify. It has no metadata row or persisted PE image, and the synthetic MethodDef token used internally by the runtime is not a token in user-visible metadata. Static verification of dynamically emitted runtime-async IL therefore requires a metadata-backed verification image containing an equivalent method signature, implementation flags, local signature, exception regions, and IL body.

The verification image is a surrogate for the IL producer contract. It validates that the producer emitted statically valid runtime-async IL, but it does not validate the paired `DynamicMethodDesc` representation, runtime-async JIT transformation, suspension behavior, descriptor lifetime, or reflection normalization. Those remain owned by the managed library and CoreCLR execution tests described below.

Until the runtime-async contract is standardized, the verifier and runtime should be built from the same source revision. Command-line verification must use the matching framework reference set and identify `System.Private.CoreLib` as the system module. Any materialization path, including `PersistedAssemblyBuilder` or serialization of an executable `MethodBuilder` assembly, must have focused tests proving that the `Async` implementation bit and the emitted method body survive unchanged. Producers that generate helper methods, nested lambdas, local functions, iterator cores, or cleanup methods must verify the complete generated method graph rather than only the public entry method.

The source-built CLI/library identity, manifest format, revision-pinning rules, and consumer acceptance requirements are defined in [Runtime-Async IL Verification Artifacts](../../runtime-async-ilverification-artifacts.md).

## Reflection behavior

Reflection exposes the ordinary facade. The async body descriptor is an implementation variant and is normalized back to the ordinary method by reflection lookup. `CreateDelegate`, `Invoke`, module-bound constructors, owner-bound constructors, custom-attribute queries, and direct dynamic-method calls preserve the existing `DynamicMethod` surface while using the paired runtime representation internally.

## Memory cost

Dynamic method chunks currently allocate `AsyncMethodData` for every descriptor, including descriptors that are later used only by ordinary synchronous dynamic methods. `AsyncMethodData` contains an `AsyncMethodFlags` value and a `Signature`.

The incremental descriptor storage is:

- 24 bytes per descriptor on 64-bit targets;
- 12 bytes per descriptor on 32-bit targets.

A runtime-async pair therefore contains 48 bytes or 24 bytes of async metadata respectively, in addition to the second descriptor, resolver, signature/name buffers, entrypoint state, and any generated code.

Chunk capacity is computed as:

```text
floor(MethodDescChunk::MaxSizeOfMethodDescs /
      (DynamicMethodDesc base size + NonVtableSlot + NativeCodeSlot + AsyncMethodData))
```

This reduces descriptors per chunk compared with an ordinary-only layout. The current design accepts that fixed cost to keep descriptor layout uniform, variant lookup compatible with the existing MethodDesc machinery, and descriptor reuse simple. A split ordinary/async pool or sidecar allocation would add table and lifetime complexity and is not justified without workload measurements showing a material regression.

## Verification ownership

Managed library tests cover the public API, flag normalization, reflection behavior, bake immutability, and Mono gating. CoreCLR runtime tests cover Task and ValueTask execution, `DynamicILInfo`, multiple awaits, exceptions and cancellation, dynamic-to-dynamic awaits, RID exhaustion, construction rollback, forced finalization and pair reuse, portable-entrypoint cleanup, profiler callbacks, and EventPipe load/unload identity.

Portable-entrypoint cleanup is a CoreCLR WASM-specific execution test because `FEATURE_PORTABLE_ENTRYPOINTS` is enabled for that target. Its test-only configuration marks both descriptors pending, forces pair finalization, and rejects either descriptor if it reaches the free-list checkout path with stale pending state.

## Non-goals

- Converting arbitrary synchronous IL into async IL.
- Adding C# compiler support.
- Providing a high-level async IL builder.
- Exposing the async body descriptor through reflection.
- Supporting implementation flags that do not have defined `DynamicMethod` execution semantics.
