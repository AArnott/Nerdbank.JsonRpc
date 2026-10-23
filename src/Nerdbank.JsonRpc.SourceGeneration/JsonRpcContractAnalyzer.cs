// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using static Nerdbank.JsonRpc.SourceGeneration.ContractUtilities;

namespace Nerdbank.JsonRpc.SourceGeneration;

/// <summary>Checks annotated JSON-RPC interfaces independently of proxy generation.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class JsonRpcContractAnalyzer : DiagnosticAnalyzer
{
	private static readonly DiagnosticDescriptor CollidingMethodNames = new(
		"NBJSONRPC002",
		"JSON-RPC method names must be unique",
		"Methods '{0}' and '{1}' on contract '{2}' both map to JSON-RPC method name '{3}' under the default naming policy",
		"Usage",
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		helpLinkUri: "https://aarnott.github.io/Nerdbank.JsonRpc/analyzers/NBJSONRPC002.html");

	private static readonly DiagnosticDescriptor UnsupportedContractMember = new(
		"NBJSONRPC003",
		"Unsupported JSON-RPC contract member",
		"Member '{0}' cannot be used in a JSON-RPC contract because {1}",
		"Usage",
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		helpLinkUri: "https://aarnott.github.io/Nerdbank.JsonRpc/analyzers/NBJSONRPC003.html");

	/// <inheritdoc />
	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CollidingMethodNames, UnsupportedContractMember];

	/// <inheritdoc />
	public override void Initialize(AnalysisContext context)
	{
		if (context is null)
		{
			throw new ArgumentNullException(nameof(context));
		}

		context.EnableConcurrentExecution();
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.RegisterCompilationStartAction(static context =>
		{
			INamedTypeSymbol? contractAttribute = context.Compilation.GetTypeByMetadataName(KnownApis.JsonRpcContractAttribute);
			INamedTypeSymbol? marshalableAttribute = context.Compilation.GetTypeByMetadataName(KnownApis.RpcMarshalableAttribute);
			if (contractAttribute is null && marshalableAttribute is null)
			{
				return;
			}

			INamedTypeSymbol? methodShapeAttribute = context.Compilation.GetTypeByMetadataName(KnownApis.MethodShapeAttribute);
			context.RegisterSymbolAction(context => AnalyzeContract(context, contractAttribute, marshalableAttribute, methodShapeAttribute), SymbolKind.NamedType);
		});
	}

	private static void AnalyzeContract(SymbolAnalysisContext context, INamedTypeSymbol? contractAttribute, INamedTypeSymbol? marshalableAttribute, INamedTypeSymbol? methodShapeAttribute)
	{
		INamedTypeSymbol contract = (INamedTypeSymbol)context.Symbol;
		if (contract.TypeKind != TypeKind.Interface || (!HasAttribute(contract, contractAttribute) && !HasAttribute(contract, marshalableAttribute)))
		{
			return;
		}

		bool isMarshalable = HasAttribute(contract, marshalableAttribute);
		bool generateProxy = !contract.GetAttributes().Any(attribute =>
			SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, contractAttribute)
			&& attribute.NamedArguments.Any(static argument => argument.Key == KnownApis.GenerateProxy && argument.Value.Value is false));
		Dictionary<string, IMethodSymbol> methodsByName = new(StringComparer.Ordinal);
		HashSet<string> signatures = new(StringComparer.Ordinal);
		foreach (INamedTypeSymbol type in new[] { contract }.Concat(contract.AllInterfaces))
		{
			context.CancellationToken.ThrowIfCancellationRequested();
			if (type.MetadataName == KnownApis.IShapeableMetadataName && type.ContainingNamespace.ToDisplayString() == KnownApis.PolyTypeNamespace)
			{
				continue;
			}

			foreach (ISymbol member in type.GetMembers())
			{
				if (member.DeclaredAccessibility == Accessibility.Private)
				{
					continue;
				}

				Location location = SymbolEqualityComparer.Default.Equals(type, contract)
					? member.Locations.FirstOrDefault(static location => location.IsInSource) ?? Location.None
					: contract.Locations.FirstOrDefault(static location => location.IsInSource) ?? Location.None;
				if (member is IPropertySymbol { IsAbstract: true })
				{
					ReportUnsupported(context, member, location, "properties and indexers are not supported; use asynchronous methods instead");
				}

				if (member is IEventSymbol @event && !generateProxy)
				{
					if (isMarshalable)
					{
						ReportUnsupported(context, member, location, "marshalable interfaces cannot declare properties or events");
					}
					else if (GetUnsupportedEventReason(@event, marshalableAttribute) is string eventReason)
					{
						ReportUnsupported(context, member, location, eventReason);
					}
				}

				if (member is not IMethodSymbol { MethodKind: MethodKind.Ordinary } method
					|| (isMarshalable && type.ToDisplayString() == KnownApis.IDisposable)
					|| method.ExplicitInterfaceImplementations.Any(static implementation => implementation.ContainingType.MetadataName == KnownApis.IShapeableMetadataName && implementation.ContainingNamespace.ToDisplayString() == KnownApis.PolyTypeNamespace))
				{
					continue;
				}

				if (!generateProxy && GetUnsupportedSignatureReason(method, isMarshalable, marshalableAttribute) is string reason)
				{
					ReportUnsupported(context, member, location, reason);
				}

				if (method.IsStatic)
				{
					continue;
				}

				string rpcName = GetExplicitRpcName(method, methodShapeAttribute) ?? GetDefaultRpcName(method.Name);
				string signature = string.Join("|", new[] { rpcName, method.Name, method.ReturnType.ToDisplayString() }
					.Concat(method.Parameters.Select(static parameter => $"{parameter.RefKind}:{parameter.Type.ToDisplayString()}")));
				if (!signatures.Add(signature))
				{
					continue;
				}

				if (methodsByName.TryGetValue(rpcName, out IMethodSymbol? other))
				{
					context.ReportDiagnostic(Diagnostic.Create(CollidingMethodNames, location, method.ToDisplayString(), other.ToDisplayString(), contract.ToDisplayString(), rpcName));
				}
				else
				{
					methodsByName.Add(rpcName, method);
				}
			}
		}
	}

	private static void ReportUnsupported(SymbolAnalysisContext context, ISymbol member, Location location, string reason)
		=> context.ReportDiagnostic(Diagnostic.Create(UnsupportedContractMember, location, member.ToDisplayString(), reason));
}
