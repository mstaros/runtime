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
    }
}
