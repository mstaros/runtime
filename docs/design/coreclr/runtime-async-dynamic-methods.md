# Runtime Async Dynamic Methods

Status: draft design note for issue #118074.

## Summary

Runtime async methods let valid async-shaped IL be JITed as an async state machine when the method carries the runtime async method implementation flag. Static IL producers can emit methods with that flag, but `DynamicMethod` currently does not expose a way to customize its method implementation flags. That prevents assemblyless dynamic methods from participating in the same runtime async path.

This note describes the intended support for `MethodImplAttributes.Async` on `DynamicMethod`.

## Motivation

`DynamicMethod` is the natural Reflection.Emit primitive for in-process code generation when the generated method does not need an owning assembly, metadata table entry, or collectible assembly lifetime. Runtime async currently cannot be enabled for such methods because dynamic methods report fixed implementation flags, effectively `IL | NoInlining`.

That limitation forces advanced IL generators to choose between less desirable designs:

- emit dynamic assemblies only so method implementation flags can be represented;
- generate a custom async state machine in IL;
- avoid runtime async for generated code.

Supporting runtime async directly on `DynamicMethod` keeps the generated method assemblyless while allowing the JIT and runtime async machinery to own the async transformation.

## Goals

- Allow a `DynamicMethod` to carry `MethodImplAttributes.Async`.
- Preserve the existing default behavior for dynamic methods that do not request runtime async.
- Allow async-aware callers to call the async-call entrypoint directly when the target is async capable.
- Keep non-async callers able to use the normal task-returning entrypoint.
- Require IL generators to emit the same valid async-shaped IL required for non-dynamic runtime async methods.

## Non-goals

- Do not make arbitrary dynamic method IL become async automatically.
- Do not add C# compiler support in this change.
- Do not require generated dynamic methods to live in emitted assemblies.
- Do not define a general-purpose high-level async IL builder API.

## Proposed API shape

The core requirement is a way to set method implementation flags before the dynamic method is completed and JITed.

Possible API shapes include:

```csharp
public sealed class DynamicMethod : MethodInfo
{
    public void SetMethodImplementationFlags(MethodImplAttributes attributes);
}
```

or a constructor overload/factory option that accepts `MethodImplAttributes`.

The API should reject changes after the method has been baked, associated with a delegate, or otherwise used for invocation. That mirrors the existing model where a dynamic method's signature and IL are configured before use.

The default should remain compatible with existing behavior. A dynamic method that does not opt into runtime async should continue to report the existing implementation flags.

## Runtime behavior

When a dynamic method carries `MethodImplAttributes.Async`, the runtime treats it as an async-capable method in the same sense as a non-dynamic runtime async method:

- the JIT sees the method as runtime async capable;
- the method has the async call convention used by runtime async;
- async callers can call the async-call entrypoint directly;
- non-async callers can use the task-returning entrypoint or thunk;
- awaits inside the async method can be optimized into direct async calls when the awaited method is also async capable.

The dynamic method remains assemblyless. The support is about preserving method implementation flags and passing them through the existing runtime async recognition path, not about giving the dynamic method a normal metadata owner.

## IL contract

The IL generator is responsible for emitting valid runtime async IL. The runtime should not attempt to infer or repair async shape from arbitrary IL.

Invalid IL should fail through the same validation, verification, JIT, or runtime paths that apply to invalid non-dynamic runtime async methods.

## Reflection behavior

`DynamicMethod.GetMethodImplementationFlags()` should reflect the configured implementation flags. Existing callers that do not configure flags should continue seeing the existing defaults.

Reflection over dynamic methods is already limited compared with normal methods. This feature should not require new metadata tables or a synthetic declaring type.

## Test plan

Suggested coverage:

- default `DynamicMethod` implementation flags remain unchanged;
- a dynamic method can be configured with `MethodImplAttributes.Async`;
- `GetMethodImplementationFlags()` reports the configured async flag;
- a runtime async dynamic method can be invoked from a non-async caller through the task-returning path;
- a runtime async dynamic method can await another runtime async dynamic method;
- invalid async-shaped IL fails consistently with invalid non-dynamic runtime async IL where practical.

## Open questions

- Should the public API allow arbitrary implementation flags or only a constrained subset relevant to dynamic methods?
- Should the setter be named after method implementation flags, or should runtime async get a narrower opt-in API?
- What exact exception should be thrown if flags are changed after the method is used?
- Which tests should live in managed library tests versus CoreCLR runtime async tests?
