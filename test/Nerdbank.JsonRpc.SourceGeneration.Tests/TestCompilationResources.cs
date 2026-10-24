// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.IO;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Testing;
using Nerdbank.JsonRpc;
using Nerdbank.MessagePack;
using Nerdbank.Streams;
using PolyType;

internal static class TestCompilationResources
{
	internal static ReferenceAssemblies Net10 { get; } = new(
		"net10.0",
		new PackageIdentity("Microsoft.NETCore.App.Ref", "10.0.0"),
		Path.Combine("ref", "net10.0"));

	internal static CSharpParseOptions ParseOptions { get; } = new(LanguageVersion.Preview);

	internal static CSharpCompilationOptions CompilationOptions { get; } = new(OutputKind.DynamicallyLinkedLibrary);

	internal static MetadataReference JsonRpcReference { get; } = MetadataReference.CreateFromFile(typeof(JsonRpcContractAttribute).Assembly.Location);

	internal static MetadataReference PolyTypeReference { get; } = MetadataReference.CreateFromFile(typeof(ITypeShape<>).Assembly.Location);

	internal static ImmutableArray<MetadataReference> GeneratorAdditionalReferences { get; } =
	[
		JsonRpcReference,
		MetadataReference.CreateFromFile(typeof(MessagePackSerializer).Assembly.Location),
		MetadataReference.CreateFromFile(typeof(Sequence<>).Assembly.Location),
		PolyTypeReference,
	];

	private static Task<ImmutableArray<MetadataReference>> FrameworkReferences { get; } = Net10.ResolveAsync(LanguageNames.CSharp, CancellationToken.None);

	private static Task<ImmutableArray<MetadataReference>> ContractReferences { get; } = CreateContractReferencesAsync();

	private static Task<ImmutableArray<MetadataReference>> GeneratorReferences { get; } = CreateGeneratorReferencesAsync();

	internal static Task<ImmutableArray<MetadataReference>> GetContractReferencesAsync() => ContractReferences;

	internal static Task<ImmutableArray<MetadataReference>> GetGeneratorReferencesAsync() => GeneratorReferences;

	private static async Task<ImmutableArray<MetadataReference>> CreateContractReferencesAsync()
		=> (await FrameworkReferences.ConfigureAwait(false)).Add(JsonRpcReference);

	private static async Task<ImmutableArray<MetadataReference>> CreateGeneratorReferencesAsync()
		=> (await FrameworkReferences.ConfigureAwait(false)).AddRange(GeneratorAdditionalReferences);
}
