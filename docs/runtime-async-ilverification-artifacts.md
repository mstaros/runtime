# Runtime-Async IL Verification Artifacts

This document defines how external IL producers consume the runtime-async verifier from this runtime fork. The authoritative verifier is built from source; a global `ilverify` command or a public `dotnet-ilverify` / `Microsoft.ILVerification` package is not an acceptable substitute unless it is independently proven to contain the same runtime-async changes.

## Shared verifier source

The command-line and in-process forms are separate build products, but they compile the same verifier implementation:

- `src/coreclr/tools/ILVerify/ILVerify.csproj` imports `../ILVerification/ILVerification.projitems`.
- `src/coreclr/tools/ILVerification/ILVerification.csproj` imports `ILVerification.projitems`.
- Both imports resolve to `src/coreclr/tools/ILVerification/ILVerification.projitems` in the same checkout.

The CLI does not consume a separately versioned verifier package. It compiles the shared project-items source directly, so the CLI and library cannot diverge in verification semantics without one of their project imports changing. The artifact publisher checks this identity before building either form.

## Publishing artifacts

Run the publisher from the runtime repository root:

```powershell
pwsh ./publish-runtime-async-ilverification.ps1 `
    -Configuration Release `
    -ExpectedRevision <full-runtime-commit>
```

The default output is:

```text
artifacts/runtime-async-ilverification/Release/
├── cli/
│   ├── ILVerify.dll
│   ├── ILVerify.deps.json
│   ├── ILVerify.runtimeconfig.json
│   └── supporting assemblies
├── library/
│   └── ILVerification.dll
└── runtime-async-ilverification-manifest.json
```

The publisher:

1. Resolves and compares the shared `ILVerification.projitems` imports.
2. Requires the exact `-ExpectedRevision` when one is supplied.
3. Rejects a dirty checkout by default.
4. Builds the library and publishes the CLI from that checkout.
5. Records the Git revision, shared project-items hash, artifact-relative paths, lengths, and SHA-256 hashes in the manifest.

`-AllowDirty` exists only for local script validation. Artifacts whose manifest has `sourceDirty: true` are not eligible for CI or external-consumer verification.

## Consumer acceptance rules

Before invoking the CLI or loading the library, a consumer must validate the manifest:

- `schemaVersion` is supported and `contract` is `runtime-async-ilverification`.
- `sourceRevision` equals the exact runtime revision required by the consumer.
- `sourceDirty` is `false`.
- Every listed file exists under the artifact root and matches its recorded length and SHA-256 hash.
- `cliEntryAssembly` and `libraryAssembly` resolve inside the artifact root.
- `requiredSystemModule` is `System.Private.CoreLib`.

A consumer must fail closed when any check fails. It must not search `PATH`, invoke an unrelated global tool, restore a public verifier package, or silently accept a different runtime revision.

## CLI invocation

Invoke the manifest-selected CLI assembly with a compatible `dotnet` host:

```text
dotnet <artifact-root>/cli/ILVerify.dll <input-assembly>
    --system-module System.Private.CoreLib
    --reference <matching-reference-assembly-or-Core_Root-pattern>
```

All input dependencies must be supplied explicitly. The framework references must come from the runtime build associated with the manifest revision; mixing the verifier with an unrelated framework build is unsupported.

## In-process invocation

Consumers that require in-process verification may load the manifest-selected `library/ILVerification.dll`. They must apply the same manifest and revision checks as CLI consumers. The library is an alternative transport for the same shared verifier source, not an independently versioned contract.

## Verification image materialization

`PersistedAssemblyBuilder` is the authoritative runtime-owned materialization path. Focused `ILVerification.Tests` coverage emits valid and deliberately invalid runtime-async methods through `ILGenerator`, saves the PE in memory, and proves that the persisted image preserves the public signature, `MethodImplAttributes.Async`, local signature, exception region, and method body before invoking the verifier.

A runnable `AssemblyBuilder` created with `AssemblyBuilderAccess.Run` or `RunAndCollect` has no framework persistence API. Serializing it requires a third-party metadata writer such as ILPack. This runtime fork does not add or endorse such a dependency, and therefore does not treat runnable-assembly serialization as an authoritative verification path. An external producer may evaluate that option independently only if it runs the same metadata, IL, and positive/negative verifier round-trip matrix. Until that proof exists for the producer's exact method shapes and serializer version, it must use `PersistedAssemblyBuilder`.

## Ownership boundary

This artifact contract establishes verifier identity and transport. Metadata-backed materialization fidelity is tracked separately: a producer must still prove that its persisted verification image preserves method signatures, implementation flags, locals, exception regions, and IL bodies before verifier success is treated as evidence about its live dynamic methods.
