// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis;

namespace Nerdbank.JsonRpc.Analyzers;

[Generator(LanguageNames.CSharp)]
public class ProxyGenerator : IIncrementalGenerator
{
	/// <summary>
	/// The namespace under which proxies (and interceptors) are generated.
	/// </summary>
	public const string GenerationNamespace = "Nerdbank.Json.Generated";

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		IncrementalValuesProvider<ImmutableEquatableArray<ProxyModel>> rpcContractProxyProvider = context.SyntaxProvider.ForAttributeWithMetadataName(
			Types.RpcProxyAttribute.FullName,
			(node, cancellationToken) => true,
			PrepareProxy);
	}
}
