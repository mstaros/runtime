# Await runtime-async DynamicMethod reflection facts synchronously

## Problem

`src/tests/async/reflection/reflection.cs` declares 14 `[ConditionalFact]` tests as `public static async Task` methods (lines 246–541). The XUnitWrapperGenerator standalone runner invokes test methods without awaiting, so each of these runs only to its first incomplete `await`; later assertions and exceptions land in a discarded `Task`.

`promote-canonical-coreroot.ps1` builds `reflection.csproj` standalone with `/p:TestFilter=DynamicMethod_SetImplementationFlags`; 13 of the 14 async tests match that filter, which is the 13 × CS4014 in `SimpleRunner.g.cs`. The committed script contains no suppression; `WarningsNotAsErrors=CS4014` was only ever passed by hand.

## Change

- For each of the 14 tests: keep the `[ConditionalFact(...)]` on a synchronous `public static void <Name>()` that calls `<Name>Core().GetAwaiter().GetResult()`; the existing async body becomes `private static async Task <Name>Core()`. Same pattern as the file's existing `UnsafeAccessorsAsync` / `UnsafeAccessorsAsyncInner` pair.
- `promote-canonical-coreroot.ps1` is unchanged; it already enforces CS4014.
- No generator changes; test infrastructure stays upstream.

## Acceptance

- Standalone build of `reflection.csproj` (`/p:BuildAsStandalone=true`) compiles with zero CS4014 and no suppression.
- `promote-canonical-coreroot.ps1` proof run reaches exit 100 with the 14 tests executed to completion.
