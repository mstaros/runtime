// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Threading.Tasks;
using ILVerify;
using Xunit;

namespace ILVerification.Tests
{
    public sealed class PersistedAssemblyVerificationTests
    {
        private const string ValidMethodName = "ValidAsync";
        private const string MissingAsyncMethodName = "MissingAsync";

        [Fact]
        public static void PersistedAssemblyBuilder_PreservesRuntimeAsyncVerificationImage()
        {
            byte[] image = CreateVerificationImage();

            using var resolver = new ImageResolver(image);
            PEReader peReader = resolver.GeneratedAssembly;
            MetadataReader metadataReader = peReader.GetMetadataReader();
            MethodDefinitionHandle validHandle = FindMethod(metadataReader, ValidMethodName);
            MethodDefinitionHandle missingAsyncHandle = FindMethod(metadataReader, MissingAsyncMethodName);

            MethodDefinition validDefinition = metadataReader.GetMethodDefinition(validHandle);
            MethodDefinition missingAsyncDefinition = metadataReader.GetMethodDefinition(missingAsyncHandle);

            Assert.True((validDefinition.ImplAttributes & MethodImplAttributes.Async) != 0);
            Assert.True((missingAsyncDefinition.ImplAttributes & MethodImplAttributes.Async) == 0);
            Assert.Equal(
                metadataReader.GetBlobBytes(validDefinition.Signature),
                metadataReader.GetBlobBytes(missingAsyncDefinition.Signature));

            MethodBodyBlock validBody = peReader.GetMethodBody(validDefinition.RelativeVirtualAddress);
            MethodBodyBlock missingAsyncBody = peReader.GetMethodBody(missingAsyncDefinition.RelativeVirtualAddress);

            Assert.False(validBody.LocalSignature.IsNil);
            Assert.Equal(validBody.GetILBytes().ToArray(), missingAsyncBody.GetILBytes().ToArray());
            Assert.Equal(18, validBody.GetILBytes().Length);
            Assert.Equal(OpCodes.Ldc_I4_S.Value, validBody.GetILBytes()[0]);
            Assert.Equal(42, validBody.GetILBytes()[1]);
            Assert.Equal(OpCodes.Stloc_0.Value, validBody.GetILBytes()[2]);
            Assert.Equal(OpCodes.Leave.Value, validBody.GetILBytes()[3]);
            Assert.Equal(OpCodes.Pop.Value, validBody.GetILBytes()[8]);
            Assert.Equal(OpCodes.Ldc_I4_M1.Value, validBody.GetILBytes()[9]);
            Assert.Equal(OpCodes.Stloc_0.Value, validBody.GetILBytes()[10]);
            Assert.Equal(OpCodes.Leave.Value, validBody.GetILBytes()[11]);
            Assert.Equal(OpCodes.Ldloc_0.Value, validBody.GetILBytes()[16]);
            Assert.Equal(OpCodes.Ret.Value, validBody.GetILBytes()[17]);

            ExceptionRegion validRegion = Assert.Single(validBody.ExceptionRegions);
            ExceptionRegion missingAsyncRegion = Assert.Single(missingAsyncBody.ExceptionRegions);
            Assert.Equal(ExceptionRegionKind.Catch, validRegion.Kind);
            Assert.Equal(validRegion.Kind, missingAsyncRegion.Kind);
            Assert.Equal(validRegion.TryOffset, missingAsyncRegion.TryOffset);
            Assert.Equal(validRegion.TryLength, missingAsyncRegion.TryLength);
            Assert.Equal(validRegion.HandlerOffset, missingAsyncRegion.HandlerOffset);
            Assert.Equal(validRegion.HandlerLength, missingAsyncRegion.HandlerLength);

            Assembly loadedAssembly = Assembly.Load(image);
            Type loadedType = loadedAssembly.GetType("RuntimeAsyncVerificationImage", throwOnError: true);
            MethodInfo validMethod = loadedType.GetMethod(ValidMethodName, BindingFlags.Public | BindingFlags.Static);
            MethodInfo missingAsyncMethod = loadedType.GetMethod(MissingAsyncMethodName, BindingFlags.Public | BindingFlags.Static);

            Assert.Equal(typeof(Task<int>), validMethod.ReturnType);
            Assert.Equal(validMethod.ReturnType, missingAsyncMethod.ReturnType);
            Assert.True((validMethod.GetMethodImplementationFlags() & MethodImplAttributes.Async) != 0);
            Assert.True((missingAsyncMethod.GetMethodImplementationFlags() & MethodImplAttributes.Async) == 0);

            AssertPersistedMethodBody(validMethod.GetMethodBody());
            AssertPersistedMethodBody(missingAsyncMethod.GetMethodBody());
            Assert.Equal(
                validMethod.GetMethodBody().GetILAsByteArray(),
                missingAsyncMethod.GetMethodBody().GetILAsByteArray());

            var verifier = new Verifier(resolver, new VerifierOptions
            {
                IncludeMetadataTokensInErrorMessages = true,
                SanityChecks = true
            });
            verifier.SetSystemModuleName(resolver.SystemModuleName);

            Assert.Empty(verifier.Verify(peReader, validHandle));

            VerificationResult missingAsyncResult = Assert.Single(verifier.Verify(peReader, missingAsyncHandle));
            Assert.Equal(VerifierError.StackUnexpected, missingAsyncResult.Code);
        }


        [Theory]
        [InlineData("array", true)]
        [InlineData("implicit", true)]
        [InlineData("range", true)]
        [InlineData("local", true)]
        [InlineData("default", true)]
        [InlineData("initial", true)]
        [InlineData("indirect", true)]
        [InlineData("store-indirect", true)]
        [InlineData("constructor-call", true)]
        [InlineData("branch", true)]
        [InlineData("stack-merge", true)]
        [InlineData("loop", true)]
        [InlineData("local-ref", false)]
        [InlineData("pointer", false)]
        [InlineData("branch-unsafe", false)]
        [InlineData("stack-merge-unsafe", false)]
        [InlineData("loop-unsafe", false)]
        [InlineData("finally-unsafe", false)]
        [InlineData("alias", false)]
        [InlineData("byref-local-alias", false)]
        [InlineData("constructor-call-unsafe", false)]
        public static void Span_returns_require_proven_heap_or_default_storage(string scenario, bool accepted)
        {
            foreach (Type spanType in new[] { typeof(Span<int>), typeof(ReadOnlySpan<int>) })
            {
                byte[] image = CreateSpanReturnImage(spanType, scenario);
                using var resolver = new ImageResolver(image);
                var verifier = new Verifier(resolver, new VerifierOptions { SanityChecks = true });
                verifier.SetSystemModuleName(resolver.SystemModuleName);
                VerificationResult[] findings = verifier.Verify(resolver.GeneratedAssembly,
                    FindMethod(resolver.GeneratedAssembly.GetMetadataReader(), "SpanReturn")).ToArray();
                if (accepted)
                {
                    Assert.Empty(findings);
                    Type generated = Assembly.Load(image).GetType("SpanReturnImage", throwOnError: true);
                    var run = (Func<bool, int[], int>)generated.GetMethod("Run").CreateDelegate(typeof(Func<bool, int[], int>));
                    foreach (bool condition in new[] { false, true })
                    {
                        int expected = scenario == "default" || scenario == "initial" ? 0
                            : scenario == "range" ? 1
                            : (scenario == "branch" || scenario == "stack-merge") && !condition ? 0 : 3;
                        Assert.Equal(expected, run(condition, new[] { 1, 2, 3 }));
                    }
                }
                else
                {
                    Assert.Contains(findings, finding => finding.Code == VerifierError.ReturnPtrToStack);
                }
            }
        }

        [Theory]
        [InlineData("loop-access")]
        [InlineData("loop-stackalloc")]
        [InlineData("merge-access")]
        [InlineData("predecessor-access")]
        public static void Span_lifetime_analysis_continues_after_filterable_findings(string scenario)
        {
            foreach (Type spanType in new[] { typeof(Span<int>), typeof(ReadOnlySpan<int>) })
            {
                var assembly = new PersistedAssemblyBuilder(new AssemblyName($"SpanFindings_{Guid.NewGuid():N}"), typeof(object).Assembly);
                ModuleBuilder module = assembly.DefineDynamicModule("SpanFindings");
                TypeBuilder owner = module.DefineType("PrivateData");
                FieldBuilder field = owner.DefineField("Value", typeof(int), FieldAttributes.Private | FieldAttributes.Static);
                owner.CreateType();
                TypeBuilder type = module.DefineType("SpanFindings", TypeAttributes.Public);
                MethodBuilder method = type.DefineMethod("SpanReturn", MethodAttributes.Public | MethodAttributes.Static,
                    spanType, new[] { typeof(bool), typeof(int[]) });
                ILGenerator il = method.GetILGenerator();
                LocalBuilder result = il.DeclareLocal(spanType);
                LocalBuilder element = il.DeclareLocal(typeof(int));
                ConstructorInfo arrayConstructor = spanType.GetConstructor(new[] { typeof(int[]) });
                ConstructorInfo refConstructor = spanType.GetConstructor(new[] { typeof(int).MakeByRefType() });
                void ArrayValue() { il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Newobj, arrayConstructor); }
                void UnsafeValue() { il.Emit(OpCodes.Ldloca, element); il.Emit(OpCodes.Newobj, refConstructor); }
                void AccessFinding() { il.Emit(OpCodes.Ldsfld, field); il.Emit(OpCodes.Pop); }

                if (scenario.StartsWith("loop", StringComparison.Ordinal))
                {
                    Label body = il.DefineLabel(), condition = il.DefineLabel();
                    ArrayValue(); il.Emit(OpCodes.Stloc, result); il.Emit(OpCodes.Br, condition);
                    il.MarkLabel(body); UnsafeValue(); il.Emit(OpCodes.Stloc, result);
                    il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Starg_S, (byte)0);
                    il.MarkLabel(condition); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Brtrue, body);
                    if (scenario == "loop-stackalloc")
                    {
                        il.Emit(OpCodes.Ldc_I4_4); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Localloc);
                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Newobj, spanType.GetConstructor(new[] { typeof(void).MakePointerType(), typeof(int) }));
                        il.Emit(OpCodes.Pop);
                    }
                    else AccessFinding();
                    il.Emit(OpCodes.Ldloc, result);
                }
                else if (scenario == "merge-access")
                {
                    Label alternate = il.DefineLabel(), join = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Brfalse, alternate);
                    ArrayValue(); il.Emit(OpCodes.Br, join);
                    il.MarkLabel(alternate); UnsafeValue();
                    il.MarkLabel(join); AccessFinding();
                }
                else
                {
                    Label alternate = il.DefineLabel(), writer = il.DefineLabel(), join = il.DefineLabel();
                    ArrayValue(); il.Emit(OpCodes.Stloc, result);
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Brfalse, alternate); il.Emit(OpCodes.Br, join);
                    il.MarkLabel(alternate); AccessFinding(); il.Emit(OpCodes.Br, writer);
                    il.MarkLabel(writer); UnsafeValue(); il.Emit(OpCodes.Stloc, result); il.Emit(OpCodes.Br, join);
                    il.MarkLabel(join); il.Emit(OpCodes.Ldloc, result);
                }
                il.Emit(OpCodes.Ret);
                type.CreateType();
                using var image = new MemoryStream();
                assembly.Save(image);
                using var resolver = new ImageResolver(image.ToArray());
                var verifier = new Verifier(resolver, new VerifierOptions { SanityChecks = true });
                verifier.SetSystemModuleName(resolver.SystemModuleName);
                VerificationResult[] findings = verifier.Verify(resolver.GeneratedAssembly,
                    FindMethod(resolver.GeneratedAssembly.GetMetadataReader(), "SpanReturn")).ToArray();
                Assert.Contains(findings, finding => finding.Code == (scenario == "loop-stackalloc"
                    ? VerifierError.Unverifiable : VerifierError.FieldAccess));
                Assert.Contains(findings, finding => finding.Code == VerifierError.ReturnPtrToStack);
                // Deliberately malformed lifetime: never load or execute this image.
            }
        }

        private static byte[] CreateSpanReturnImage(Type spanType, string scenario)
        {
            var assembly = new PersistedAssemblyBuilder(new AssemblyName($"SpanReturns_{Guid.NewGuid():N}"), typeof(object).Assembly);
            TypeBuilder type = assembly.DefineDynamicModule("SpanReturns").DefineType("SpanReturnImage",
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
            MethodBuilder method = type.DefineMethod("SpanReturn", MethodAttributes.Public | MethodAttributes.Static,
                spanType, new[] { typeof(bool), typeof(int[]) });
            ILGenerator il = method.GetILGenerator();
            LocalBuilder result = il.DeclareLocal(spanType);
            LocalBuilder element = il.DeclareLocal(typeof(int));
            ConstructorInfo arrayConstructor = spanType.GetConstructor(new[] { typeof(int[]) });
            ConstructorInfo refConstructor = spanType.GetConstructor(new[] { typeof(int).MakeByRefType() });

            void ArrayValue()
            {
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Newobj, arrayConstructor);
            }
            void UnsafeValue()
            {
                il.Emit(OpCodes.Ldloca, element);
                il.Emit(OpCodes.Newobj, refConstructor);
            }
            void DefaultValue()
            {
                il.Emit(OpCodes.Ldloca, result);
                il.Emit(OpCodes.Initobj, spanType);
                il.Emit(OpCodes.Ldloc, result);
            }

            switch (scenario)
            {
                case "array": ArrayValue(); break;
                case "implicit":
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Call, spanType.GetMethod("op_Implicit", new[] { typeof(int[]) }));
                    break;
                case "range":
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Newobj, spanType.GetConstructor(new[] { typeof(int[]), typeof(int), typeof(int) }));
                    break;
                case "local": ArrayValue(); il.Emit(OpCodes.Stloc, result); il.Emit(OpCodes.Ldloc, result); break;
                case "default": DefaultValue(); break;
                case "initial": il.Emit(OpCodes.Ldloc, result); break;
                case "indirect":
                    ArrayValue(); il.Emit(OpCodes.Stloc, result);
                    il.Emit(OpCodes.Ldloca, result); il.Emit(OpCodes.Ldobj, spanType);
                    break;
                case "store-indirect":
                    il.Emit(OpCodes.Ldloca, result); ArrayValue(); il.Emit(OpCodes.Stobj, spanType);
                    il.Emit(OpCodes.Ldloc, result);
                    break;
                case "constructor-call":
                    il.Emit(OpCodes.Ldloca, result); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, arrayConstructor);
                    il.Emit(OpCodes.Ldloc, result);
                    break;
                case "constructor-call-unsafe":
                    il.Emit(OpCodes.Ldloca, result); il.Emit(OpCodes.Ldloca, element); il.Emit(OpCodes.Call, refConstructor);
                    il.Emit(OpCodes.Ldloc, result);
                    break;
                case "local-ref": UnsafeValue(); break;
                case "pointer":
                    il.Emit(OpCodes.Ldc_I4_4); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Localloc); il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Newobj, spanType.GetConstructor(new[] { typeof(void).MakePointerType(), typeof(int) }));
                    break;
                case "branch":
                case "branch-unsafe":
                case "stack-merge":
                case "stack-merge-unsafe":
                {
                    Label alternate = il.DefineLabel(), join = il.DefineLabel();
                    bool stackMerge = scenario.StartsWith("stack-merge", StringComparison.Ordinal);
                    il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Brfalse, alternate);
                    ArrayValue();
                    if (!stackMerge) il.Emit(OpCodes.Stloc, result);
                    il.Emit(OpCodes.Br, join);
                    il.MarkLabel(alternate);
                    if (scenario.EndsWith("unsafe", StringComparison.Ordinal)) UnsafeValue(); else DefaultValue();
                    if (!stackMerge) il.Emit(OpCodes.Stloc, result);
                    il.MarkLabel(join);
                    if (!stackMerge) il.Emit(OpCodes.Ldloc, result);
                    break;
                }
                case "loop":
                case "loop-unsafe":
                {
                    Label body = il.DefineLabel(), condition = il.DefineLabel();
                    ArrayValue(); il.Emit(OpCodes.Stloc, result); il.Emit(OpCodes.Br, condition);
                    il.MarkLabel(body);
                    if (scenario == "loop-unsafe") UnsafeValue(); else ArrayValue();
                    il.Emit(OpCodes.Stloc, result); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Starg_S, (byte)0);
                    il.MarkLabel(condition); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Brtrue, body);
                    il.Emit(OpCodes.Ldloc, result);
                    break;
                }
                case "finally-unsafe":
                    ArrayValue(); il.Emit(OpCodes.Stloc, result);
                    il.BeginExceptionBlock(); il.Emit(OpCodes.Nop);
                    il.BeginFinallyBlock(); UnsafeValue(); il.Emit(OpCodes.Stloc, result);
                    il.EndExceptionBlock(); il.Emit(OpCodes.Ldloc, result);
                    break;
                case "alias":
                {
                    MethodBuilder writer = type.DefineMethod("UnknownWriter", MethodAttributes.Public | MethodAttributes.Static,
                        typeof(void), new[] { spanType.MakeByRefType() });
                    ILGenerator writerIL = writer.GetILGenerator();
                    writerIL.Emit(OpCodes.Ldarg_0); writerIL.Emit(OpCodes.Initobj, spanType); writerIL.Emit(OpCodes.Ret);
                    ArrayValue(); il.Emit(OpCodes.Stloc, result); il.Emit(OpCodes.Ldloca, result);
                    il.Emit(OpCodes.Call, writer); il.Emit(OpCodes.Ldloc, result);
                    break;
                }
                case "byref-local-alias":
                {
                    LocalBuilder alias = il.DeclareLocal(spanType.MakeByRefType());
                    ArrayValue(); il.Emit(OpCodes.Stloc, result); il.Emit(OpCodes.Ldloca, result); il.Emit(OpCodes.Stloc, alias);
                    il.Emit(OpCodes.Ldloc, alias); UnsafeValue(); il.Emit(OpCodes.Stobj, spanType); il.Emit(OpCodes.Ldloc, result);
                    break;
                }
                default: throw new ArgumentException(scenario, nameof(scenario));
            }
            il.Emit(OpCodes.Ret);

            MethodBuilder run = type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static,
                typeof(int), new[] { typeof(bool), typeof(int[]) });
            ILGenerator runIL = run.GetILGenerator();
            LocalBuilder returned = runIL.DeclareLocal(spanType);
            runIL.Emit(OpCodes.Ldarg_0); runIL.Emit(OpCodes.Ldarg_1); runIL.Emit(OpCodes.Call, method);
            runIL.Emit(OpCodes.Stloc, returned); runIL.Emit(OpCodes.Ldloca, returned);
            runIL.Emit(OpCodes.Call, spanType.GetProperty("Length").GetMethod); runIL.Emit(OpCodes.Ret);
            type.CreateType();
            using var stream = new MemoryStream();
            assembly.Save(stream);
            return stream.ToArray();
        }

        private static byte[] CreateVerificationImage()
        {
            var assemblyBuilder = new PersistedAssemblyBuilder(
                new AssemblyName($"RuntimeAsyncVerificationImage_{Guid.NewGuid():N}"),
                typeof(object).Assembly);
            ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule("RuntimeAsyncVerificationImage");
            TypeBuilder typeBuilder = moduleBuilder.DefineType(
                "RuntimeAsyncVerificationImage",
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Abstract);

            DefineMethod(typeBuilder, ValidMethodName, isAsync: true);
            DefineMethod(typeBuilder, MissingAsyncMethodName, isAsync: false);
            typeBuilder.CreateType();

            using var stream = new MemoryStream();
            assemblyBuilder.Save(stream);
            return stream.ToArray();
        }

        private static void DefineMethod(TypeBuilder typeBuilder, string name, bool isAsync)
        {
            MethodBuilder method = typeBuilder.DefineMethod(
                name,
                MethodAttributes.Public | MethodAttributes.Static,
                typeof(Task<int>),
                Type.EmptyTypes);

            if (isAsync)
            {
                method.SetImplementationFlags(
                    method.GetMethodImplementationFlags() | MethodImplAttributes.Async);
            }

            ILGenerator il = method.GetILGenerator();
            il.DeclareLocal(typeof(int));
            il.BeginExceptionBlock();
            il.Emit(OpCodes.Ldc_I4_S, (sbyte)42);
            il.Emit(OpCodes.Stloc_0);
            il.BeginCatchBlock(typeof(Exception));
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldc_I4_M1);
            il.Emit(OpCodes.Stloc_0);
            il.EndExceptionBlock();
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ret);
        }

        private static void AssertPersistedMethodBody(MethodBody body)
        {
            LocalVariableInfo local = Assert.Single(body.LocalVariables);
            Assert.Equal(typeof(int), local.LocalType);

            ExceptionHandlingClause clause = Assert.Single(body.ExceptionHandlingClauses);
            Assert.Equal(ExceptionHandlingClauseOptions.Clause, clause.Flags);
            Assert.Equal(typeof(Exception), clause.CatchType);
        }

        private static MethodDefinitionHandle FindMethod(MetadataReader metadataReader, string methodName)
        {
            foreach (MethodDefinitionHandle handle in metadataReader.MethodDefinitions)
            {
                MethodDefinition definition = metadataReader.GetMethodDefinition(handle);
                if (metadataReader.GetString(definition.Name) == methodName)
                {
                    return handle;
                }
            }

            throw new InvalidOperationException($"Method '{methodName}' was not found.");
        }

        private sealed class ImageResolver : IResolver, IDisposable
        {
            private readonly Dictionary<string, PEReader> _readers = new(StringComparer.OrdinalIgnoreCase);

            public ImageResolver(byte[] image)
            {
                GeneratedAssembly = AddReader(new MemoryStream(image, writable: false));
                PEReader systemModule = AddReader(File.OpenRead(typeof(object).Assembly.Location));
                SystemModuleName = systemModule.GetMetadataReader().GetAssemblyDefinition().GetAssemblyNameInfo();

                Assembly systemRuntime = Assembly.Load(new AssemblyName("System.Runtime"));
                if (!_readers.ContainsKey(systemRuntime.GetName().Name))
                {
                    AddReader(File.OpenRead(systemRuntime.Location));
                }
            }

            public PEReader GeneratedAssembly { get; }

            public AssemblyNameInfo SystemModuleName { get; }

            public PEReader ResolveAssembly(AssemblyNameInfo assemblyName)
                => Resolve(assemblyName.Name);

            public PEReader ResolveModule(AssemblyNameInfo referencingAssembly, string fileName)
                => Resolve(Path.GetFileNameWithoutExtension(fileName));

            public void Dispose()
            {
                foreach (PEReader reader in _readers.Values)
                {
                    reader.Dispose();
                }
            }

            private PEReader AddReader(Stream stream)
            {
                var reader = new PEReader(stream);
                AssemblyNameInfo name = reader.GetMetadataReader().GetAssemblyDefinition().GetAssemblyNameInfo();
                _readers.Add(name.Name, reader);
                return reader;
            }

            private PEReader Resolve(string simpleName)
                => _readers.TryGetValue(simpleName, out PEReader reader) ? reader : null;
        }
        [Fact]
        public static void Roslyn_protected_ref_return_preserves_heap_lifetime()
        {
            int[] values = { 7 };
            ref int result = ref RoslynProtectedRefReturn(values);
            Assert.Equal(8, result);
            result = 19;
            Assert.Equal(19, values[0]);

            using var resolver = new ImageResolver(File.ReadAllBytes(typeof(PersistedAssemblyVerificationTests).Assembly.Location));
            var verifier = new Verifier(resolver, new VerifierOptions { SanityChecks = true });
            verifier.SetSystemModuleName(resolver.SystemModuleName);
            VerificationResult[] findings = verifier.Verify(resolver.GeneratedAssembly,
                FindMethod(resolver.GeneratedAssembly.GetMetadataReader(), nameof(RoslynProtectedRefReturn))).ToArray();
            Assert.Empty(findings);
        }
        // This method is compiled by Roslyn as part of the test assembly.
        private static ref int RoslynProtectedRefReturn(int[] values)
        {
            try
            {
                return ref values[0];
            }
            finally
            {
                values[0]++;
            }
        }
        [Theory]
        [InlineData("heap", true)]
        [InlineData("copy", true)]
        [InlineData("parameter", true)]
        [InlineData("default", true)]
        [InlineData("stack", false)]
        [InlineData("argument-reassigned", false)]
        [InlineData("mixed-stack", false)]
        [InlineData("mixed-stack-reversed", false)]
        [InlineData("handler-stack", false)]
        [InlineData("loop-stack", false)]
        [InlineData("argument-loop", false)]
        [InlineData("access-then-stack", false)]
        [InlineData("no-init", false)]
        public static void Byref_returns_require_proven_surviving_storage(string scenario, bool accepted)
        {
            var assembly = new PersistedAssemblyBuilder(new AssemblyName($"ByrefReturns_{Guid.NewGuid():N}"), typeof(object).Assembly);
            ModuleBuilder module = assembly.DefineDynamicModule("ByrefReturns");
            TypeBuilder hidden = module.DefineType("HiddenStorage", TypeAttributes.Public);
            FieldBuilder hiddenField = hidden.DefineField("Value", typeof(int), FieldAttributes.Private | FieldAttributes.Static);
            hidden.CreateType();
            TypeBuilder type = module.DefineType("ByrefReturnImage", TypeAttributes.Public);
            MethodBuilder method = type.DefineMethod("ReturnRef", MethodAttributes.Public | MethodAttributes.Static,
                typeof(int).MakeByRefType(), new[] { typeof(bool), typeof(int[]), typeof(int).MakeByRefType() });
            method.InitLocals = scenario != "no-init";
            ILGenerator il = method.GetILGenerator();
            LocalBuilder reference = il.DeclareLocal(typeof(int).MakeByRefType());
            LocalBuilder copy = il.DeclareLocal(typeof(int).MakeByRefType());
            LocalBuilder stack = il.DeclareLocal(typeof(int));
            void EmitHeapAddress()
            {
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ldelema, typeof(int));
            }
            if (scenario != "default")
            {
                EmitHeapAddress();
                il.Emit(OpCodes.Stloc, reference);
            }
            switch (scenario)
            {
                case "copy":
                    il.Emit(OpCodes.Ldloc, reference);
                    il.Emit(OpCodes.Stloc, copy);
                    il.Emit(OpCodes.Ldloc, copy);
                    il.Emit(OpCodes.Stloc, reference);
                    break;
                case "parameter":
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Stloc, reference);
                    break;
                case "stack":
                case "access-then-stack":
                    if (scenario == "access-then-stack")
                    {
                        il.Emit(OpCodes.Ldsfld, hiddenField);
                        il.Emit(OpCodes.Pop);
                    }
                    il.Emit(OpCodes.Ldloca, stack);
                    il.Emit(OpCodes.Stloc, reference);
                    break;
                case "argument-reassigned":
                    il.Emit(OpCodes.Ldloca, stack);
                    il.Emit(OpCodes.Starg_S, (byte)2);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Stloc, reference);
                    break;
                case "mixed-stack":
                case "mixed-stack-reversed":
                    Label other = il.DefineLabel();
                    Label merge = il.DefineLabel();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Brtrue, other);
                    if (scenario == "mixed-stack") EmitHeapAddress(); else il.Emit(OpCodes.Ldloca, stack);
                    il.Emit(OpCodes.Br, merge);
                    il.MarkLabel(other);
                    if (scenario == "mixed-stack") il.Emit(OpCodes.Ldloca, stack); else EmitHeapAddress();
                    il.MarkLabel(merge);
                    il.Emit(OpCodes.Stloc, reference);
                    break;
                case "handler-stack":
                    il.BeginExceptionBlock();
                    EmitHeapAddress();
                    il.Emit(OpCodes.Stloc, reference);
                    il.BeginFinallyBlock();
                    il.Emit(OpCodes.Ldloca, stack);
                    il.Emit(OpCodes.Stloc, reference);
                    il.EndExceptionBlock();
                    break;
                case "loop-stack":
                case "argument-loop":
                    Label head = il.DefineLabel();
                    Label exit = il.DefineLabel();
                    il.MarkLabel(head);
                    if (scenario == "argument-loop") il.Emit(OpCodes.Ldarg_2); else il.Emit(OpCodes.Ldloc, reference);
                    il.Emit(OpCodes.Stloc, copy);
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Brfalse, exit);
                    il.Emit(OpCodes.Ldloca, stack);
                    if (scenario == "argument-loop") il.Emit(OpCodes.Starg_S, (byte)2); else il.Emit(OpCodes.Stloc, reference);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Starg_S, (byte)0);
                    il.Emit(OpCodes.Br, head);
                    il.MarkLabel(exit);
                    il.Emit(OpCodes.Ldloc, copy);
                    il.Emit(OpCodes.Stloc, reference);
                    break;
            }
            il.Emit(OpCodes.Ldloc, reference);
            il.Emit(OpCodes.Ret);

            MethodBuilder run = type.DefineMethod("Run", MethodAttributes.Public | MethodAttributes.Static,
                typeof(int), new[] { typeof(bool), typeof(int[]) });
            ILGenerator runIl = run.GetILGenerator();
            runIl.Emit(OpCodes.Ldarg_0);
            runIl.Emit(OpCodes.Ldarg_1);
            runIl.Emit(OpCodes.Ldarg_1);
            runIl.Emit(OpCodes.Ldc_I4_0);
            runIl.Emit(OpCodes.Ldelema, typeof(int));
            runIl.Emit(OpCodes.Call, method);
            runIl.Emit(OpCodes.Dup);
            runIl.Emit(OpCodes.Ldc_I4, 21);
            runIl.Emit(OpCodes.Stind_I4);
            runIl.Emit(OpCodes.Ldind_I4);
            runIl.Emit(OpCodes.Ret);
            type.CreateType();
            using var image = new MemoryStream();
            assembly.Save(image);
            using var resolver = new ImageResolver(image.ToArray());
            var verifier = new Verifier(resolver, new VerifierOptions { SanityChecks = true });
            verifier.SetSystemModuleName(resolver.SystemModuleName);
            VerificationResult[] findings = verifier.Verify(resolver.GeneratedAssembly,
                FindMethod(resolver.GeneratedAssembly.GetMetadataReader(), "ReturnRef")).ToArray();
            if (accepted)
            {
                Assert.Empty(findings);
                if (scenario != "default")
                {
                    var execute = (Func<bool, int[], int>)Assembly.Load(image.ToArray()).GetType("ByrefReturnImage")
                        .GetMethod("Run").CreateDelegate(typeof(Func<bool, int[], int>));
                    int[] values = { 7 };
                    Assert.Equal(21, execute(false, values));
                    Assert.Equal(21, values[0]);
                }
            }
            else
            {
                Assert.Contains(findings, finding => finding.Code == VerifierError.ReturnPtrToStack);
                if (scenario == "access-then-stack")
                    Assert.Contains(findings, finding => finding.Code == VerifierError.FieldAccess);
                // Invalid lifetime images are never loaded or executed.
            }
        }
        [Fact]
        public static void Roslyn_protected_parameter_return_preserves_caller_lifetime()
        {
            int value = 7;
            ref int result = ref RoslynProtectedParameterReturn(ref value);
            Assert.Equal(8, result);
            result = 19;
            Assert.Equal(19, value);
            using var resolver = new ImageResolver(File.ReadAllBytes(typeof(PersistedAssemblyVerificationTests).Assembly.Location));
            var verifier = new Verifier(resolver, new VerifierOptions { SanityChecks = true });
            verifier.SetSystemModuleName(resolver.SystemModuleName);
            Assert.Empty(verifier.Verify(resolver.GeneratedAssembly,
                FindMethod(resolver.GeneratedAssembly.GetMetadataReader(), nameof(RoslynProtectedParameterReturn))));
        }
        private static ref int RoslynProtectedParameterReturn(ref int value)
        {
            try { return ref value; }
            finally { value++; }
        }
    }
}

