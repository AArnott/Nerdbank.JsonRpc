// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static Nerdbank.JsonRpc.SourceGeneration.ContractUtilities;

namespace Nerdbank.JsonRpc.SourceGeneration;

/// <summary>
/// Generates JSON-RPC client proxy implementations for interfaces annotated with <c>JsonRpcContractAttribute</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ClientProxyGenerator : IIncrementalGenerator
{
	private const string DiagnosticHelpLink = "https://aarnott.github.io/Nerdbank.JsonRpc/analyzers/NBJSONRPC001.html";

	private static readonly DiagnosticDescriptor UnsupportedMethodSignature = new(
		"NBJSONRPC001",
		"Unsupported JSON-RPC proxy method signature",
		"Method '{0}' cannot be generated as a JSON-RPC client proxy because {1}",
		"Usage",
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		helpLinkUri: DiagnosticHelpLink);

	private static readonly DiagnosticDescriptor UnsupportedInterface = new(
		"NBJSONRPC001",
		"Unsupported JSON-RPC proxy interface",
		"Interface '{0}' cannot be generated as a JSON-RPC client proxy because {1}",
		"Usage",
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		helpLinkUri: DiagnosticHelpLink);

	private enum ProxyMethodKind
	{
		Unsupported,
		ValueTaskOfT,
		TaskOfT,
		ValueTask,
		Task,
		AsyncEnumerableOfT,
		Notification,
	}

	/// <inheritdoc />
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		IncrementalValuesProvider<ProxyOutput> proxyInterfaces = context.SyntaxProvider.ForAttributeWithMetadataName(
			KnownApis.JsonRpcContractAttribute,
			static (node, _) => node is InterfaceDeclarationSyntax,
			static (ctx, cancellationToken) => CreateProxyOutput(ctx, hasContractAttribute: true, cancellationToken))
			.Where(static output => output is not null)
			.Select(static (output, _) => output!)
			.WithTrackingName("JsonRpcContractModels")
			.WithComparer(ProxyOutputComparer.Instance)
			.WithTrackingName("JsonRpcContractOutputs");

		IncrementalValuesProvider<ProxyOutput> marshalableInterfaces = context.SyntaxProvider.ForAttributeWithMetadataName(
			KnownApis.RpcMarshalableAttribute,
			static (node, _) => node is InterfaceDeclarationSyntax,
			static (ctx, cancellationToken) => CreateProxyOutput(ctx, hasContractAttribute: false, cancellationToken))
			.Where(static output => output is not null)
			.Select(static (output, _) => output!)
			.WithTrackingName("RpcMarshalableProxyModels")
			.WithComparer(ProxyOutputComparer.Instance)
			.WithTrackingName("RpcMarshalableProxyOutputs");

		context.RegisterSourceOutput(proxyInterfaces, static (ctx, output) => EmitProxy(ctx, output));
		context.RegisterSourceOutput(marshalableInterfaces, static (ctx, output) => EmitProxy(ctx, output));
		IncrementalValueProvider<bool> publicExtensions = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
			options.GlobalOptions.TryGetValue("build_property.PublicRpcMarshalableInterfaceExtensions", out string? value) && bool.TryParse(value, out bool enabled) && enabled);
		context.RegisterSourceOutput(proxyInterfaces.Collect().Combine(marshalableInterfaces.Collect()).Combine(publicExtensions), static (ctx, outputs) =>
			EmitInterfaceExtensions(ctx, outputs.Left.Left.Concat(outputs.Left.Right).SelectMany(static output => output.Extensions), outputs.Right));
	}

	private static bool GetCallScopedLifetime(INamedTypeSymbol interfaceSymbol, INamedTypeSymbol? marshalableAttribute)
	{
		if (marshalableAttribute is null)
		{
			return false;
		}

		foreach (AttributeData attribute in interfaceSymbol.GetAttributes())
		{
			if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, marshalableAttribute))
			{
				return attribute.NamedArguments.Any(static argument => argument.Key == KnownApis.CallScopedLifetime && argument.Value.Value is true);
			}
		}

		return false;
	}

	private static ProxyOutput? CreateProxyOutput(GeneratorAttributeSyntaxContext context, bool hasContractAttribute, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		INamedTypeSymbol interfaceSymbol = (INamedTypeSymbol)context.TargetSymbol;
		Compilation compilation = context.SemanticModel.Compilation;
		INamedTypeSymbol? contractAttribute = compilation.GetTypeByMetadataName(KnownApis.JsonRpcContractAttribute);
		INamedTypeSymbol? rpcMarshalableAttribute = compilation.GetTypeByMetadataName(KnownApis.RpcMarshalableAttribute);
		INamedTypeSymbol? optionalInterfaceAttribute = compilation.GetTypeByMetadataName(KnownApis.RpcMarshalableOptionalInterfaceAttribute);
		bool hasContract = hasContractAttribute || HasAttribute(interfaceSymbol, contractAttribute);
		if (interfaceSymbol.GetAttributes().Any(attribute =>
			SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, contractAttribute)
			&& attribute.NamedArguments.Any(static argument => argument.Key == KnownApis.GenerateProxy && argument.Value.Value is false)))
		{
			return null;
		}

		if (!hasContractAttribute && hasContract)
		{
			return null;
		}

		bool isMarshalable = !hasContractAttribute || HasAttribute(interfaceSymbol, rpcMarshalableAttribute);
		bool isCallScoped = isMarshalable && GetCallScopedLifetime(interfaceSymbol, rpcMarshalableAttribute);
		InterfaceInfo info = CreateInterfaceInfo(
			interfaceSymbol,
			(InterfaceDeclarationSyntax)context.TargetNode,
			compilation,
			isMarshalable,
			isCallScoped,
			hasContract,
			rpcMarshalableAttribute,
			optionalInterfaceAttribute,
			cancellationToken);
		ImmutableArray<DiagnosticInfo> diagnostics = info.Diagnostics;
		string? source = diagnostics.IsEmpty ? RenderProxy(info) : null;
		return new ProxyOutput(info.HintName, source, diagnostics)
		{
			Extensions = source is null ? [] : info.OptionalInterfaces.Concat(info.ProxyGroups.SelectMany(static group => group))
				.Select(static item => item.Symbol).Concat([info.Symbol])
				.GroupBy(static symbol => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), StringComparer.Ordinal)
				.Select(static group => group.First())
				.Select(symbol => new ExtensionInfo(symbol.ContainingNamespace.IsGlobalNamespace ? string.Empty : symbol.ContainingNamespace.ToDisplayString(), symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), symbol.DeclaredAccessibility == Accessibility.Public && SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, compilation.Assembly) ? "public" : "internal"))
				.OrderBy(static extension => extension.TypeName, StringComparer.Ordinal).ToImmutableArray(),
		};
	}

	private static void EmitProxy(SourceProductionContext context, ProxyOutput output)
	{
		foreach (DiagnosticInfo diagnostic in output.Diagnostics)
		{
			context.ReportDiagnostic(diagnostic.ToDiagnostic());
		}

		if (output.Source is not null)
		{
			context.AddSource(output.HintName, SourceText.From(output.Source, Encoding.UTF8));
		}
	}

	private static void EmitInterfaceExtensions(SourceProductionContext context, IEnumerable<ExtensionInfo> extensions, bool publicExtensions)
	{
		foreach (IGrouping<string, ExtensionInfo> group in extensions.Distinct().OrderBy(static item => item.TypeName, StringComparer.Ordinal).GroupBy(static item => item.Namespace))
		{
			SourceWriter builder = new();
			builder.AppendLine("#nullable enable");
			builder.AppendLine();
			if (group.Key.Length > 0)
			{
				builder.Append("namespace ").Append(group.Key).AppendLine(";");
				builder.AppendLine();
			}

			bool publicClass = publicExtensions && group.Any(static extension => extension.Visibility == "public");
			if (publicClass)
			{
				builder.AppendLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
			}

			builder.Append(publicClass ? "public" : "internal").AppendLine(" static class NerdbankJsonRpcInterfaceExtensions");
			builder.OpenBlock();
			foreach (ExtensionInfo extension in group)
			{
				builder.AppendLine("/// <inheritdoc cref=\"global::Nerdbank.JsonRpc.IClientProxy.Is(global::System.Type)\"/>");
				builder.Append(extension.Visibility).Append(" static bool Is(this ").Append(extension.TypeName).AppendLine("? self, global::System.Type type)");
				builder.OpenBlock();
				builder.AppendLine("if (type is null)");
				builder.OpenBlock();
				builder.AppendLine("throw new global::System.ArgumentNullException(nameof(type));");
				builder.CloseBlock();
				builder.AppendLine("return self is global::Nerdbank.JsonRpc.IClientProxy proxy ? proxy.Is(type) : type.IsInstanceOfType(self);");
				builder.CloseBlock();
				builder.AppendLine();
				builder.AppendLine("/// <summary>Returns the object as an interface when it is enabled on the remote proxy, or implemented by a local object.</summary>");
				builder.Append(extension.Visibility).Append(" static T? As<T>(this ").Append(extension.TypeName).AppendLine("? self) where T : class");
				using (builder.Indent())
				{
					builder.AppendLine("=> self is global::Nerdbank.JsonRpc.IClientProxy proxy ? global::Nerdbank.JsonRpc.JsonRpcExtensions.As<T>(proxy) : self as T;");
				}

				builder.AppendLine();
			}

			builder.CloseBlock();
			context.AddSource("NerdbankJsonRpcInterfaceExtensions" + (group.Key.Length == 0 ? string.Empty : "." + group.Key) + ".g.cs", SourceText.From(builder.ToString(), Encoding.UTF8));
		}
	}

	private static InterfaceInfo CreateInterfaceInfo(INamedTypeSymbol interfaceSymbol, InterfaceDeclarationSyntax interfaceDeclaration, Compilation compilation, bool isMarshalable, bool isCallScoped, bool hasContractAttribute, INamedTypeSymbol? marshalableAttribute, INamedTypeSymbol? optionalInterfaceAttribute, CancellationToken cancellationToken)
	{
		INamedTypeSymbol? methodShapeAttribute = compilation.GetTypeByMetadataName(KnownApis.MethodShapeAttribute);
		INamedTypeSymbol? eventShapeAttribute = compilation.GetTypeByMetadataName(KnownApis.EventShapeAttribute);
		ImmutableArray<MethodInfo>.Builder methods = ImmutableArray.CreateBuilder<MethodInfo>();
		ImmutableArray<DiagnosticInfo>.Builder diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
		ImmutableArray<OptionalInterfaceInfo>.Builder optionalInterfaces = ImmutableArray.CreateBuilder<OptionalInterfaceInfo>();
		if (!interfaceDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword))
		{
			diagnostics.Add(DiagnosticInfo.Create(isMethodDiagnostic: false, interfaceDeclaration.Identifier.GetLocation(), interfaceSymbol.ToDisplayString(), "annotated interfaces must be partial"));
			return new InterfaceInfo(interfaceSymbol, eventShapeAttribute, methods.ToImmutable(), optionalInterfaces.ToImmutable(), HasStaticTypeShapeResolver(compilation), isMarshalable, isCallScoped, hasContractAttribute, diagnostics.ToImmutable());
		}

		if (GetUnsupportedInterfaceReason(interfaceSymbol, isMarshalable, isCallScoped) is string interfaceReason)
		{
			diagnostics.Add(DiagnosticInfo.Create(isMethodDiagnostic: false, interfaceSymbol.Locations.FirstOrDefault(), interfaceSymbol.ToDisplayString(), interfaceReason));
			return new InterfaceInfo(interfaceSymbol, eventShapeAttribute, methods.ToImmutable(), optionalInterfaces.ToImmutable(), HasStaticTypeShapeResolver(compilation), isMarshalable, isCallScoped, hasContractAttribute, diagnostics.ToImmutable());
		}

		ValidateProxyEvents(interfaceSymbol, marshalableAttribute, diagnostics);

		foreach (IMethodSymbol method in GetProxyMethods(interfaceSymbol))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (GetUnsupportedSignatureReason(method, isMarshalable, marshalableAttribute) is string reason)
			{
				diagnostics.Add(DiagnosticInfo.Create(isMethodDiagnostic: true, method.Locations.FirstOrDefault(), method.ToDisplayString(), reason));
				continue;
			}

			methods.Add(CreateMethodInfo(method, methodShapeAttribute, interfaceSymbol, optionalInterfaceId: null));
		}

		if (isMarshalable && optionalInterfaceAttribute is not null)
		{
			ImmutableArray<AttributeData> optionalInterfaceAttributes = interfaceSymbol.GetAttributes().Where(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, optionalInterfaceAttribute)).ToImmutableArray();
			HashSet<int> interfaceIds = [];
			foreach (AttributeData attribute in optionalInterfaceAttributes)
			{
				if (attribute.ConstructorArguments is not [{ Value: int interfaceId }, { Value: INamedTypeSymbol optionalInterface }])
				{
					continue;
				}

				if (optionalInterface is not { TypeKind: TypeKind.Interface })
				{
					diagnostics.Add(DiagnosticInfo.Create(isMethodDiagnostic: false, interfaceSymbol.Locations.FirstOrDefault(), optionalInterface.ToDisplayString(), "optional marshalable types must be interfaces"));
					continue;
				}

				if (!interfaceIds.Add(interfaceId))
				{
					diagnostics.Add(DiagnosticInfo.Create(isMethodDiagnostic: false, interfaceSymbol.Locations.FirstOrDefault(), interfaceSymbol.ToDisplayString(), $"optional interface ID {interfaceId} is declared more than once"));
					continue;
				}

				ImmutableArray<MethodInfo>.Builder optionalMethods = ImmutableArray.CreateBuilder<MethodInfo>();
				if (GetUnsupportedOptionalInterfaceReason(optionalInterface) is string optionalReason)
				{
					diagnostics.Add(DiagnosticInfo.Create(isMethodDiagnostic: false, interfaceSymbol.Locations.FirstOrDefault(), optionalInterface.ToDisplayString(), optionalReason));
					continue;
				}

				foreach (IMethodSymbol method in GetProxyMethods(optionalInterface, preserveDeclaringInterfaces: true))
				{
					if (GetUnsupportedSignatureReason(method, isMarshalable: true, marshalableAttribute) is string reason)
					{
						diagnostics.Add(DiagnosticInfo.Create(isMethodDiagnostic: true, method.Locations.FirstOrDefault(), method.ToDisplayString(), reason));
						continue;
					}

					optionalMethods.Add(CreateMethodInfo(method, methodShapeAttribute, GetShapeProvider(optionalInterface, interfaceSymbol), interfaceId));
				}

				optionalInterfaces.Add(new(interfaceId, optionalInterface, optionalMethods.ToImmutable()));
			}
		}

		ImmutableArray<ImmutableArray<OptionalInterfaceInfo>>.Builder groups = ImmutableArray.CreateBuilder<ImmutableArray<OptionalInterfaceInfo>>();
		INamedTypeSymbol? groupAttribute = compilation.GetTypeByMetadataName(KnownApis.JsonRpcProxyInterfaceGroupAttribute);
		foreach (AttributeData attribute in interfaceSymbol.GetAttributes().Where(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, groupAttribute)))
		{
			ImmutableArray<OptionalInterfaceInfo>.Builder group = ImmutableArray.CreateBuilder<OptionalInterfaceInfo>();
			if (attribute.ConstructorArguments is not [{ Kind: TypedConstantKind.Array, IsNull: false } argument])
			{
				diagnostics.Add(DiagnosticInfo.Create(isMethodDiagnostic: false, interfaceSymbol.Locations.FirstOrDefault(), interfaceSymbol.ToDisplayString(), "proxy groups must contain non-null interface types"));
				continue;
			}

			foreach (TypedConstant additionalType in argument.Values)
			{
				if (additionalType.Value is not INamedTypeSymbol { TypeKind: TypeKind.Interface } additionalInterface)
				{
					diagnostics.Add(DiagnosticInfo.Create(isMethodDiagnostic: false, interfaceSymbol.Locations.FirstOrDefault(), interfaceSymbol.ToDisplayString(), "proxy groups must contain non-null interface types"));
					continue;
				}

				if (SymbolEqualityComparer.Default.Equals(additionalInterface, interfaceSymbol) || group.Any(item => SymbolEqualityComparer.Default.Equals(item.Symbol, additionalInterface)))
				{
					continue;
				}

				OptionalInterfaceInfo? optional = optionalInterfaces.FirstOrDefault(item => SymbolEqualityComparer.Default.Equals(item.Symbol, additionalInterface));
				if (optional is not null)
				{
					group.Add(optional);
				}
				else if (isMarshalable)
				{
					diagnostics.Add(DiagnosticInfo.Create(isMethodDiagnostic: false, interfaceSymbol.Locations.FirstOrDefault(), interfaceSymbol.ToDisplayString(), $"proxy group interface '{additionalInterface.ToDisplayString()}' must be declared as an optional marshalable interface"));
				}
				else if (GetUnsupportedInterfaceReason(additionalInterface, isMarshalable: false, isCallScoped: false) is string groupReason)
				{
					diagnostics.Add(DiagnosticInfo.Create(isMethodDiagnostic: false, interfaceSymbol.Locations.FirstOrDefault(), additionalInterface.ToDisplayString(), groupReason));
				}
				else
				{
					ValidateProxyEvents(additionalInterface, marshalableAttribute, diagnostics);
					ImmutableArray<MethodInfo>.Builder groupMethods = ImmutableArray.CreateBuilder<MethodInfo>();
					foreach (IMethodSymbol method in GetProxyMethods(additionalInterface, preserveDeclaringInterfaces: true))
					{
						if (GetUnsupportedSignatureReason(method, isMarshalable: false, marshalableAttribute) is string reason)
						{
							diagnostics.Add(DiagnosticInfo.Create(isMethodDiagnostic: true, method.Locations.FirstOrDefault(), method.ToDisplayString(), reason));
						}
						else
						{
							groupMethods.Add(CreateMethodInfo(method, methodShapeAttribute, GetShapeProvider(additionalInterface, interfaceSymbol), optionalInterfaceId: null) with { ExplicitImplementation = true });
						}
					}

					group.Add(new(null, additionalInterface, groupMethods.ToImmutable()));
				}
			}

			if (isMarshalable)
			{
				foreach (OptionalInterfaceInfo inherited in optionalInterfaces.Where(optional => group.Any(item => item.Symbol.AllInterfaces.Contains(optional.Symbol, SymbolEqualityComparer.Default))).ToArray())
				{
					if (!group.Any(item => SymbolEqualityComparer.Default.Equals(item.Symbol, inherited.Symbol)))
					{
						group.Add(inherited);
					}
				}
			}

			ImmutableArray<OptionalInterfaceInfo> sorted = group.OrderBy(static item => item.Symbol.ToDisplayString(), StringComparer.Ordinal).ToImmutableArray();
			if (!groups.Any(existing => existing.Select(static item => item.Symbol.ToDisplayString()).SequenceEqual(sorted.Select(static item => item.Symbol.ToDisplayString()))))
			{
				groups.Add(sorted);
			}
		}

		if (groups.Count == 0 && !interfaceSymbol.GetAttributes().Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, groupAttribute)))
		{
			groups.Add([]);
			if (optionalInterfaces.Count > 0)
			{
				groups.Add(optionalInterfaces.OrderBy(static item => item.InterfaceId).ToImmutableArray());
			}
		}

		return new InterfaceInfo(interfaceSymbol, eventShapeAttribute, methods.ToImmutable(), optionalInterfaces.ToImmutable(), HasStaticTypeShapeResolver(compilation), isMarshalable, isCallScoped, hasContractAttribute, diagnostics.ToImmutable())
		{
			ProxyGroups = groups.ToImmutable(),
		};
	}

	private static void ValidateProxyEvents(INamedTypeSymbol interfaceSymbol, INamedTypeSymbol? marshalableAttribute, ImmutableArray<DiagnosticInfo>.Builder diagnostics)
	{
		foreach (IEventSymbol @event in GetProxyEvents(interfaceSymbol))
		{
			if (GetUnsupportedEventReason(@event, marshalableAttribute) is string reason)
			{
				diagnostics.Add(DiagnosticInfo.Create(isMethodDiagnostic: false, @event.Locations.FirstOrDefault(), interfaceSymbol.ToDisplayString(), reason));
			}
		}
	}

	private static IEnumerable<IEventSymbol> GetProxyEvents(INamedTypeSymbol interfaceSymbol)
		=> interfaceSymbol.AllInterfaces.Concat([interfaceSymbol]).SelectMany(static type => type.GetMembers().OfType<IEventSymbol>()).Where(static @event => @event.DeclaredAccessibility != Accessibility.Private);

	private static IEnumerable<IMethodSymbol> GetProxyMethods(INamedTypeSymbol interfaceSymbol, bool preserveDeclaringInterfaces = false)
	{
		HashSet<string> seenMethods = new(System.StringComparer.Ordinal);
		foreach (INamedTypeSymbol currentInterface in interfaceSymbol.AllInterfaces.Concat([interfaceSymbol]))
		{
			if (IsShapeableInterface(currentInterface))
			{
				continue;
			}

			foreach (IMethodSymbol method in currentInterface.GetMembers().OfType<IMethodSymbol>().Where(static method =>
				method.MethodKind == MethodKind.Ordinary && method.DeclaredAccessibility != Accessibility.Private
				&& (!method.IsStatic || method.IsAbstract)))
			{
				if (method.ExplicitInterfaceImplementations.Any(static implementation => IsShapeableInterface(implementation.ContainingType)))
				{
					continue;
				}

				if (seenMethods.Add(GetMethodSignatureKey(method) + (preserveDeclaringInterfaces ? "|" + method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : string.Empty)))
				{
					yield return method;
				}
			}
		}
	}

	private static INamedTypeSymbol GetShapeProvider(INamedTypeSymbol contract, INamedTypeSymbol primaryContract)
		=> contract.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == KnownApis.GenerateShapeAttribute
				|| (attribute.AttributeClass is { Name: KnownApis.TypeShapeProviderAttributeName } providerAttribute && providerAttribute.ContainingAssembly.Name == KnownApis.PolyTypeAssembly))
			|| contract.AllInterfaces.Any(type => IsShapeableInterface(type) && SymbolEqualityComparer.Default.Equals(type.TypeArguments[0], contract))
			? contract
			: primaryContract;

	private static bool IsShapeableInterface(INamedTypeSymbol type)
		=> type.MetadataName == KnownApis.IShapeableMetadataName && type.ContainingNamespace.ToDisplayString() == KnownApis.PolyTypeNamespace && type.ContainingAssembly.Name == KnownApis.PolyTypeAssembly;

	private static string GetMethodSignatureKey(IMethodSymbol method)
		=> string.Join(
			"|",
			[
				method.Name,
				method.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
				.. method.Parameters.Select(static parameter => $"{parameter.RefKind}:{parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}"),
			]);

	private static string? GetUnsupportedInterfaceReason(INamedTypeSymbol interfaceSymbol, bool isMarshalable, bool isCallScoped)
	{
		if (interfaceSymbol.TypeParameters.Length > 0)
		{
			return "generic interfaces are not supported yet";
		}

		if (interfaceSymbol.ContainingType is not null)
		{
			return "nested interfaces are not supported yet";
		}

		if (isMarshalable)
		{
			if (!isCallScoped && !interfaceSymbol.AllInterfaces.Any(static baseInterface => baseInterface.ToDisplayString() == KnownApis.IDisposable))
			{
				return "marshalable interfaces must extend IDisposable";
			}

			if (interfaceSymbol.AllInterfaces.Concat([interfaceSymbol]).Any(static type => type.GetMembers().Any(static member => member is IPropertySymbol or IEventSymbol)))
			{
				return "marshalable interfaces cannot declare properties or events";
			}
		}

		return null;
	}

	private static string? GetUnsupportedOptionalInterfaceReason(INamedTypeSymbol interfaceSymbol)
	{
		if (interfaceSymbol.TypeParameters.Length > 0)
		{
			return "generic interfaces are not supported yet";
		}

		if (interfaceSymbol.ContainingType is not null)
		{
			return "nested interfaces are not supported yet";
		}

		if (interfaceSymbol.AllInterfaces.Concat([interfaceSymbol]).Any(static type => type.GetMembers().Any(static member => member is IPropertySymbol or IEventSymbol)))
		{
			return "optional marshalable interfaces cannot declare properties or events";
		}

		return null;
	}

	private static bool HasStaticTypeShapeResolver(Compilation compilation)
	{
		INamedTypeSymbol? resolver = compilation.GetTypeByMetadataName(KnownApis.TypeShapeResolver);
		return resolver?.GetMembers(KnownApis.TypeShapeResolve)
			.OfType<IMethodSymbol>()
			.Any(static method => method.IsGenericMethod && method.TypeParameters.Length == 1 && method.ContainingAssembly.Name == KnownApis.PolyTypeAssembly) is true;
	}

	private static MethodInfo CreateMethodInfo(IMethodSymbol method, INamedTypeSymbol? methodShapeAttribute, INamedTypeSymbol shapeProvider, int? optionalInterfaceId)
	{
		bool hasCancellationToken = method.Parameters.LastOrDefault() is { } lastParameter && IsCancellationToken(lastParameter.Type);
		ImmutableArray<IParameterSymbol> payloadParameters = hasCancellationToken
			? method.Parameters.Take(method.Parameters.Length - 1).ToImmutableArray()
			: method.Parameters.ToImmutableArray();

		ProxyMethodKind methodKind = GetMethodKind(method.ReturnType, out string? resultTypeName);
		string? explicitRpcName = GetExplicitRpcName(method, methodShapeAttribute);

		return new MethodInfo(method, payloadParameters, hasCancellationToken, methodKind, resultTypeName, explicitRpcName, optionalInterfaceId, shapeProvider.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
	}

	private static ProxyMethodKind GetMethodKind(ITypeSymbol returnType, out string? resultTypeName)
	{
		resultTypeName = null;
		string returnTypeName = returnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

		if (returnTypeName == KnownApis.Void)
		{
			return ProxyMethodKind.Notification;
		}

		if (returnType is INamedTypeSymbol namedReturnType && namedReturnType.IsGenericType)
		{
			string genericTypeName = namedReturnType.ConstructedFrom.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			if (genericTypeName is KnownApis.ValueTaskOfT or KnownApis.TaskOfT)
			{
				resultTypeName = namedReturnType.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
				return genericTypeName == KnownApis.ValueTaskOfT ? ProxyMethodKind.ValueTaskOfT : ProxyMethodKind.TaskOfT;
			}

			if (genericTypeName == KnownApis.IAsyncEnumerableOfT)
			{
				// The shape is required for the sequence type itself, not its element type.
				resultTypeName = returnTypeName;
				return ProxyMethodKind.AsyncEnumerableOfT;
			}
		}

		return returnTypeName switch
		{
			KnownApis.ValueTask => ProxyMethodKind.ValueTask,
			KnownApis.Task => ProxyMethodKind.Task,
			_ => ProxyMethodKind.Unsupported,
		};
	}

	private static string RenderProxy(InterfaceInfo info)
	{
		SourceWriter builder = new();
		builder.AppendLine("#nullable enable");
		builder.AppendLine();
		if (!info.Symbol.ContainingNamespace.IsGlobalNamespace)
		{
			builder.Append("namespace ").Append(info.Symbol.ContainingNamespace.ToDisplayString()).AppendLine(";");
			builder.AppendLine();
		}

		string factoryName = GetGeneratedMemberName(info, "NerdbankJsonRpc_ProxyFactoryAttribute");
		for (int index = 0; index < info.ProxyGroups.Length; index++)
		{
			string proxyTypeName = GetGroupProxyName(info, index);
			builder.Append('[').Append(proxyTypeName).Append('.').Append(factoryName).AppendLine("]");
			if (info.IsMarshalable)
			{
				builder.Append('[').Append(proxyTypeName).Append('.').Append(GetGeneratedMemberName(info, factoryName + "_Marshaled")).AppendLine("]");
			}

			builder.Append('[').Append(proxyTypeName).AppendLine("_Service.Factory]");
		}

		builder.Append(GetAccessibility(info.Symbol.DeclaredAccessibility)).Append(" partial interface ").Append(info.Symbol.Name).AppendLine();
		builder.OpenBlock();
		builder.CloseBlock();

		for (int index = 0; index < info.ProxyGroups.Length; index++)
		{
			ImmutableArray<OptionalInterfaceInfo> implemented = info.ProxyGroups[index];
			ImmutableArray<MethodInfo>.Builder methods = ImmutableArray.CreateBuilder<MethodInfo>();
			HashSet<string> methodSignatures = new(StringComparer.Ordinal);

			// Prefer a declared optional base's wire ID for inherited methods, even in a derived-only group.
			foreach (MethodInfo method in info.Methods.Concat(implemented.OrderBy(static additional => additional.Symbol.AllInterfaces.Length).SelectMany(static additional => additional.Methods)))
			{
				string signature = GetMethodSignatureKey(method.Symbol);
				if (method.ExplicitImplementation)
				{
					signature += "|" + method.Symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
				}

				if (method.OptionalInterfaceId is not null)
				{
					if (info.Methods.Any(baseMethod => GetMethodSignatureKey(baseMethod.Symbol) == signature))
					{
						continue;
					}

					signature += "|" + method.Symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
				}

				if (methodSignatures.Add(signature))
				{
					methods.Add(method.OptionalInterfaceId is null ? method : method with
					{
						OptionalRoutes = [
							.. implemented.Where(item => item.Methods.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate.Symbol, method.Symbol)))
								.Select(static item => item.InterfaceId!.Value),
						],
					});
				}
			}

			builder.AppendLine();
			RenderProxyClass(builder, info, GetGroupProxyName(info, index), factoryName, methods.ToImmutable(), implemented);
			builder.AppendLine();
			RenderServiceProxy(builder, info, GetGroupProxyName(info, index), methods.ToImmutable(), implemented);
		}

		return builder.ToString();
	}

	private static void RenderProxyClass(SourceWriter builder, InterfaceInfo info, string proxyName, string factoryName, ImmutableArray<MethodInfo> methods, ImmutableArray<OptionalInterfaceInfo> optionalInterfaces)
	{
		ImmutableArray<ShapeFieldInfo> shapeFields = GetShapeFields(methods);
		ImmutableArray<IEventSymbol> events = GetProxyEvents(info.Symbol)
			.Concat(optionalInterfaces.SelectMany(static item => GetProxyEvents(item.Symbol)))
			.GroupBy(static @event => @event.ContainingType.ToDisplayString() + "." + @event.Name, StringComparer.Ordinal)
			.Select(static group => group.First()).ToImmutableArray();
		string enabledInterfacesField = GetGeneratedMemberName(info, "NerdbankJsonRpc_EnabledInterfaces");
		string lifetimeField = GetGeneratedMemberName(info, "NerdbankJsonRpc_Lifetime");
		bool needsMethodNameTransform = methods.Any(m => m.ExplicitRpcName is null && m.Kind is not ProxyMethodKind.Unsupported && !(info.IsMarshalable && IsDisposeMethod(m)));
		string? methodNameTransformField = needsMethodNameTransform ? GetGeneratedMemberName(info, "NerdbankJsonRpc_MethodNameTransform") : null;
		ImmutableArray<string?> transformedRpcNameFields = methods
			.Select((method, index) => method is { ExplicitRpcName: null, Kind: not ProxyMethodKind.Unsupported } && !(info.IsMarshalable && IsDisposeMethod(method)) ? GetGeneratedMemberName(info, $"NerdbankJsonRpc_TransformedRpcName{index}") : null)
			.ToImmutableArray();

		builder.Append("internal sealed class ").Append(proxyName).Append(" : ").Append(info.InterfaceName);
		foreach (OptionalInterfaceInfo optionalInterface in optionalInterfaces)
		{
			builder.Append(", ").Append(optionalInterface.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
		}

		builder.Append(", global::Nerdbank.JsonRpc.IClientProxy, global::Nerdbank.JsonRpc.BrokeredServices.IJsonRpcServiceProxy");
		if (lifetimeField is not null)
		{
			builder.Append(", global::System.IDisposable, global::System.IAsyncDisposable");
		}

		builder.AppendLine();
		builder.OpenBlock();
		if (lifetimeField is not null)
		{
			builder.Append("private global::System.IDisposable? ").Append(lifetimeField).AppendLine(";");
			builder.Append("private readonly bool ").Append(lifetimeField).AppendLine("Owned;");
		}

		for (int i = 0; i < events.Length; i++)
		{
			string handlerType = events[i].Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
			builder.Append("private static readonly global::Nerdbank.JsonRpc.JsonRpcProxyEventRegistration ")
				.Append(GetGeneratedMemberName(info, "NerdbankJsonRpc_EventRegistration" + i))
				.Append(" = new(global::PolyType.TypeShapeProviderExtensions.GetTypeShapeOrThrow<")
				.Append(handlerType)
				.Append(">(global::PolyType.Abstractions.TypeShapeResolver.")
				.Append(info.HasStaticTypeShapeResolver ? KnownApis.TypeShapeResolve : KnownApis.TypeShapeResolveDynamicOrThrow)
				.Append('<').Append(GetEventShapeProvider(info, optionalInterfaces, events[i])).AppendLine(">().Provider));");
		}

		builder.AppendLine("private readonly global::Nerdbank.JsonRpc.IJsonRpcClient jsonRpc;");
		builder.AppendLine("private readonly bool useNamedArguments;");
		builder.Append("private readonly global::System.Collections.Generic.IReadOnlyList<global::System.Type>? ").Append(enabledInterfacesField).AppendLine(";");

		if (methodNameTransformField is string transformField)
		{
			builder.Append("private readonly global::System.Func<string, string> ").Append(transformField).AppendLine(";");
		}

		for (int i = 0; i < methods.Length; i++)
		{
			if (transformedRpcNameFields[i] is string fieldName)
			{
				builder.Append("private string? ").Append(fieldName).AppendLine(";");
			}
		}

		builder.AppendLine();
		foreach (ShapeFieldInfo shapeField in shapeFields)
		{
			builder.Append("private readonly global::PolyType.ITypeShape<")
				.Append(shapeField.TypeName)
				.Append("> ")
				.Append(shapeField.FieldName)
				.AppendLine(";");
		}

		if (shapeFields.Length > 0)
		{
			builder.AppendLine();
		}

		builder.Append("internal ").Append(proxyName).Append("(global::Nerdbank.JsonRpc.IJsonRpcClient jsonRpc, global::Nerdbank.JsonRpc.JsonRpcProxyOptions options)").AppendLine();
		builder.OpenBlock();
		builder.AppendLine("this.jsonRpc = jsonRpc;");
		if (lifetimeField is not null)
		{
			builder.Append("this.").Append(lifetimeField).AppendLine(" = options.Lifetime;");
			builder.Append("this.").Append(lifetimeField).AppendLine("Owned = options.Lifetime is not null;");
		}

		builder.AppendLine("this.useNamedArguments = options.UseNamedArguments;");
		builder.Append("this.").Append(enabledInterfacesField).AppendLine(" = options.EnabledInterfaces;");
		if (needsMethodNameTransform)
		{
			builder.Append("this.").Append(methodNameTransformField).AppendLine(" = options.MethodNameTransform;");
		}

		string[] shapeProviders = shapeFields.Select(static field => field.ProviderName).Distinct(StringComparer.Ordinal).ToArray();
		for (int i = 0; i < shapeProviders.Length; i++)
		{
			builder.Append("global::PolyType.ITypeShapeProvider typeShapeProvider").Append(i == 0 ? string.Empty : i.ToString())
				.Append(" = global::PolyType.Abstractions.TypeShapeResolver.")
				.Append(info.HasStaticTypeShapeResolver ? KnownApis.TypeShapeResolve : KnownApis.TypeShapeResolveDynamicOrThrow)
				.Append('<').Append(shapeProviders[i]).AppendLine(">().Provider;");
		}

		foreach (ShapeFieldInfo shapeField in shapeFields)
		{
			int providerIndex = Array.IndexOf(shapeProviders, shapeField.ProviderName);
			builder.Append("this.")
				.Append(shapeField.FieldName)
				.Append(" = global::PolyType.TypeShapeProviderExtensions.GetTypeShapeOrThrow<")
				.Append(shapeField.TypeName)
				.Append(">(typeShapeProvider").Append(providerIndex == 0 ? string.Empty : providerIndex.ToString()).AppendLine(");");
		}

		if (events.Length > 0)
		{
			builder.AppendLine("global::Nerdbank.JsonRpc.JsonRpc.RegisterProxyEvents(jsonRpc, new (string, global::System.Delegate, global::Nerdbank.JsonRpc.JsonRpcProxyEventRegistration)[]");
			builder.OpenBlock();
			for (int i = 0; i < events.Length; i++)
			{
				IEventSymbol @event = events[i];
				string? explicitName = @event.GetAttributes().FirstOrDefault(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, info.EventShapeAttribute))?.NamedArguments.FirstOrDefault(static argument => argument.Key == "Name").Value.Value as string;
				builder.Append("(");
				if (explicitName is not null)
				{
					AppendQuoted(builder, explicitName);
				}
				else
				{
					builder.Append("options.EventNameTransform(");
					AppendQuoted(builder, @event.Name);
					builder.Append(")");
				}

				string handlerType = @event.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
				IMethodSymbol invoke = ((INamedTypeSymbol)@event.Type).DelegateInvokeMethod!;
				bool omitSender = IsBclEventHandler((INamedTypeSymbol)@event.Type);
				builder.Append(", new ").Append(handlerType).Append("((").Append(string.Join(", ", invoke.Parameters.Select((_, index) => "p" + index))).Append(") => this.").Append(GetGeneratedMemberName(info, "NerdbankJsonRpc_Event" + i)).Append("?.Invoke(");
				builder.Append(string.Join(", ", invoke.Parameters.Select((_, index) => omitSender && index == 0 ? "this" : "p" + index)));
				builder.Append(")), ").Append(GetGeneratedMemberName(info, "NerdbankJsonRpc_EventRegistration" + i)).AppendLine("),");
			}

			builder.CloseBlock();
			builder.AppendLine(");");
		}

		builder.CloseBlock();
		builder.AppendLine();
		builder.AppendLine("global::Nerdbank.JsonRpc.IJsonRpcClient global::Nerdbank.JsonRpc.BrokeredServices.IJsonRpcServiceProxy.RpcClient => this.jsonRpc;");
		builder.AppendLine("bool global::Nerdbank.JsonRpc.IClientProxy.Is(global::System.Type type)");
		builder.OpenBlock();
		builder.AppendLine("if (type is null)");
		builder.OpenBlock();
		builder.AppendLine("throw new global::System.ArgumentNullException(nameof(type));");
		builder.CloseBlock();
		builder.AppendLine();
		builder.AppendLine("if (!type.IsInterface || type == typeof(global::Nerdbank.JsonRpc.IClientProxy) || !type.IsInstanceOfType(this))");
		builder.OpenBlock();
		builder.AppendLine("return false;");
		builder.CloseBlock();
		builder.AppendLine();
		builder.Append("if (type.IsAssignableFrom(typeof(").Append(info.InterfaceName).AppendLine(")))");
		builder.OpenBlock();
		builder.AppendLine("return true;");
		builder.CloseBlock();
		builder.AppendLine();
		builder.AppendLine("if (this.jsonRpc is global::Nerdbank.JsonRpc.IClientProxy remoteProxy && !remoteProxy.Is(type))");
		builder.OpenBlock();
		builder.AppendLine("return false;");
		builder.CloseBlock();
		builder.AppendLine();
		builder.Append("if (this.").Append(enabledInterfacesField).AppendLine(" is null)");
		builder.OpenBlock();
		builder.AppendLine("return true;");
		builder.CloseBlock();
		builder.AppendLine();
		builder.Append("foreach (global::System.Type enabledInterface in this.").Append(enabledInterfacesField).AppendLine(")");
		builder.OpenBlock();
		builder.AppendLine("if (type.IsAssignableFrom(enabledInterface))");
		builder.OpenBlock();
		builder.AppendLine("return true;");
		builder.CloseBlock();
		builder.CloseBlock();
		builder.AppendLine();
		builder.AppendLine("return false;");
		builder.CloseBlock();
		builder.AppendLine();
		RenderProxyFactory(builder, proxyName, factoryName, optionalInterfaces, isMarshalable: false);
		if (info.IsMarshalable)
		{
			builder.AppendLine();
			RenderProxyFactory(builder, proxyName, GetGeneratedMemberName(info, factoryName + "_Marshaled"), optionalInterfaces, isMarshalable: true);
		}

		for (int i = 0; i < methods.Length; i++)
		{
			builder.AppendLine();
			RenderMethod(builder, methods[i], shapeFields, transformedRpcNameFields[i], methodNameTransformField, info.IsMarshalable, lifetimeField);
		}

		if (lifetimeField is not null)
		{
			if (!methods.Any(IsDisposeMethod))
			{
				builder.Append("void global::System.IDisposable.Dispose() => global::System.Threading.Interlocked.Exchange(ref this.").Append(lifetimeField).AppendLine(", null)?.Dispose();");
			}

			builder.AppendLine("async global::System.Threading.Tasks.ValueTask global::System.IAsyncDisposable.DisposeAsync()");
			builder.OpenBlock();
			if (methods.Any(IsDisposeMethod))
			{
				builder.Append("if (!this.").Append(lifetimeField).AppendLine("Owned)");
				builder.OpenBlock();
				builder.AppendLine("((global::System.IDisposable)this).Dispose();");
				builder.AppendLine("return;");
				builder.CloseBlock();
			}

			builder.Append("global::System.IDisposable? lifetime = global::System.Threading.Interlocked.Exchange(ref this.").Append(lifetimeField).AppendLine(", null);");
			builder.AppendLine("if (lifetime is global::System.IAsyncDisposable asynchronous)");
			builder.OpenBlock();
			builder.AppendLine("await asynchronous.DisposeAsync().ConfigureAwait(false);");
			builder.CloseBlock();
			builder.AppendLine("else");
			builder.OpenBlock();
			builder.AppendLine("lifetime?.Dispose();");
			builder.CloseBlock();
			builder.CloseBlock();
		}

		for (int i = 0; i < events.Length; i++)
		{
			IEventSymbol @event = events[i];
			string handlerType = @event.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
			string field = GetGeneratedMemberName(info, "NerdbankJsonRpc_Event" + i);
			builder.AppendLine();
			builder.Append("private event ").Append(handlerType).Append("? ").Append(field).AppendLine(";");
			builder.Append("event ").Append(@event.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier))).Append(" ").Append(@event.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier))).Append(".@").Append(@event.Name).AppendLine();
			builder.OpenBlock();
			builder.Append("add => this.").Append(field).AppendLine(" += value;");
			builder.Append("remove => this.").Append(field).AppendLine(" -= value;");
			builder.CloseBlock();
		}

		builder.CloseBlock();
	}

	/// <summary>
	/// Renders a nested attribute that creates the proxy with a direct constructor call,
	/// so the runtime can activate the proxy without constructor reflection.
	/// </summary>
	private static void RenderProxyFactory(SourceWriter builder, string proxyName, string factoryName, ImmutableArray<OptionalInterfaceInfo> optionalInterfaces, bool isMarshalable)
	{
		if (isMarshalable)
		{
			optionalInterfaces = optionalInterfaces.OrderBy(static item => item.InterfaceId).ToImmutableArray();
		}

		string[] additionalTypes = optionalInterfaces.SelectMany(static item => item.Symbol.AllInterfaces.Concat([item.Symbol]))
			.Where(static type => !IsShapeableInterface(type))
			.Select(static type => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
			.Distinct(StringComparer.Ordinal).OrderBy(static type => type, StringComparer.Ordinal).ToArray();
		int interfaceCount = isMarshalable ? optionalInterfaces.Length : additionalTypes.Length;

		builder.Append("internal sealed class ").Append(factoryName).Append(" : global::Nerdbank.JsonRpc.");
		builder.AppendLine(isMarshalable ? "JsonRpcOptionalProxyFactoryAttribute" : "JsonRpcProxyFactoryAttribute");
		builder.OpenBlock();
		if (interfaceCount > 0)
		{
			builder.Append("public ").Append(factoryName).Append("()").AppendLine();
			using (builder.Indent())
			{
				builder.Append(": base(");
				for (int i = 0; i < interfaceCount; i++)
				{
					if (i > 0)
					{
						builder.Append(", ");
					}

					if (isMarshalable)
					{
						builder.Append(optionalInterfaces[i].InterfaceId!.Value);
					}
					else
					{
						builder.Append("typeof(").Append(additionalTypes[i]).Append(")");
					}
				}

				builder.AppendLine(")");
			}

			builder.OpenBlock();
			builder.CloseBlock();
			builder.AppendLine();
		}

		builder.Append("public override object CreateProxy(global::Nerdbank.JsonRpc.IJsonRpcClient client, global::Nerdbank.JsonRpc.JsonRpcProxyOptions options) => new ")
			.Append(proxyName)
			.AppendLine("(client, options);");
		builder.CloseBlock();
	}

	private static string? GetLocalResultRetainer(ITypeSymbol? type)
	{
		if (type is not INamedTypeSymbol named)
		{
			return null;
		}

		if (named.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString() is "System.IO.Stream" or "System.IO.Pipelines.PipeReader" or "System.IO.Pipelines.PipeWriter" or "System.IO.Pipelines.IDuplexPipe")
		{
			return "Retain";
		}

		if (named.ConstructedFrom.ToDisplayString() == "System.IObserver<T>")
		{
			return "Retain";
		}

		if (named.ConstructedFrom.ToDisplayString() == "System.IProgress<T>")
		{
			return "RejectArgumentOnlyResult";
		}

		AttributeData? marshalable = GetLocalMarshalableAttribute(named);
		return marshalable is null ? null : IsLocalCallScoped(marshalable) ? "RejectArgumentOnlyResult" : "RetainProxy";
	}

	private static AttributeData? GetLocalMarshalableAttribute(ITypeSymbol type)
		=> type.GetAttributes().FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() == KnownApis.RpcMarshalableAttribute);

	private static bool IsLocalCallScoped(AttributeData attribute)
		=> attribute.NamedArguments.Any(static argument => argument.Key == "CallScopedLifetime" && argument.Value.Value is true);

	private static void RenderLocalCallbackCleanup(SourceWriter builder, IMethodSymbol method)
	{
		for (int parameterIndex = 0; parameterIndex < method.Parameters.Length; parameterIndex++)
		{
			ITypeSymbol parameterType = method.Parameters[parameterIndex].Type;
			if (GetLocalMarshalableAttribute(parameterType) is not null
				|| (parameterType is INamedTypeSymbol namedParameter && namedParameter.ConstructedFrom.ToDisplayString() is "System.IObserver<T>" or "System.IProgress<T>"))
			{
				builder.Append("__boundary").Append(parameterIndex).AppendLine("?.Dispose();");
			}
		}
	}

	private static void RenderServiceProxy(SourceWriter builder, InterfaceInfo info, string rpcProxyName, ImmutableArray<MethodInfo> methods, ImmutableArray<OptionalInterfaceInfo> optionalInterfaces)
	{
		string proxyName = rpcProxyName + "_Service";
		builder.Append("internal sealed class ").Append(proxyName).Append(" : global::Nerdbank.JsonRpc.BrokeredServices.ServiceProxy, ").Append(info.InterfaceName);
		foreach (OptionalInterfaceInfo optional in optionalInterfaces)
		{
			builder.Append(", ").Append(optional.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
		}

		builder.AppendLine();
		builder.OpenBlock();
		builder.Append("internal ").Append(proxyName).AppendLine("(global::Nerdbank.JsonRpc.BrokeredServices.ServiceProxyContext context, global::System.Collections.Generic.IReadOnlyList<global::System.Type> enabled)");
		using (builder.Indent())
		{
			builder.Append(": base(context, typeof(").Append(info.InterfaceName).AppendLine("), enabled)");
		}

		builder.OpenBlock();
		builder.CloseBlock();
		builder.AppendLine("internal sealed class Factory : global::Nerdbank.JsonRpc.BrokeredServices.ServiceProxyFactoryAttribute");
		builder.OpenBlock();
		builder.Append("public Factory() : base(");
		builder.Append(string.Join(", ", optionalInterfaces.Select(static item => "typeof(" + item.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ")")));
		builder.AppendLine(")");
		builder.OpenBlock();
		builder.CloseBlock();
		builder.Append("public override object CreateProxy(global::Nerdbank.JsonRpc.BrokeredServices.ServiceProxyContext context, global::System.Collections.Generic.IReadOnlyList<global::System.Type> enabled) => new ").Append(proxyName).AppendLine("(context, enabled);");
		builder.CloseBlock();

		foreach (MethodInfo method in methods)
		{
			if (IsDisposeMethod(method))
			{
				continue;
			}

			bool notification = method.Kind == ProxyMethodKind.Notification;
			bool enumerable = method.Kind == ProxyMethodKind.AsyncEnumerableOfT;
			string returnType = method.Symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
			string contract = method.Symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			string parameters = string.Join(", ", method.Symbol.Parameters.Select(static (p, index) => $"{GetTypeName(p.Type, p.NullableAnnotation)} __arg{index}"));
			string arguments = string.Join(", ", method.Symbol.Parameters.Select(static (_, index) => "__arg" + index));
			string targetArguments = string.Join(", ", method.Symbol.Parameters.Select((p, index) =>
				p.Type is INamedTypeSymbol named && named.IsGenericType && named.ConstructedFrom.ToDisplayString() is "System.IObserver<T>" or "System.IProgress<T>"
					? "__callback" + index
					: GetLocalMarshalableAttribute(p.Type) is not null ? "__callback" + index : "__arg" + index));
			string token = method.HasCancellationToken ? "__arg" + (method.Symbol.Parameters.Length - 1)
				: enumerable ? "__enumerationCancellation" : "global::System.Threading.CancellationToken.None";
			bool subscription = method.Kind is ProxyMethodKind.TaskOfT or ProxyMethodKind.ValueTaskOfT
				&& method.Symbol.ReturnType is INamedTypeSymbol { IsGenericType: true } resultType
				&& resultType.TypeArguments[0].WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString() == "System.IDisposable";
			ITypeSymbol? localResultType = method.Symbol.ReturnType is INamedTypeSymbol { IsGenericType: true } awaitable ? awaitable.TypeArguments[0] : null;
			string? resultRetainer = GetLocalResultRetainer(localResultType);
			string helper = GetGeneratedMemberName(info, "NerdbankJsonRpc_Local" + methods.IndexOf(method));
			string? elementType = enumerable ? ((INamedTypeSymbol)method.Symbol.ReturnType).TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) : null;
			builder.AppendLine();
			if (!notification && !enumerable)
			{
				builder.Append("async ");
			}

			builder.Append(returnType).Append(" ").Append(contract).Append(".");
			builder.Append(EscapeIdentifier(method.Symbol.Name)).Append("(").Append(parameters).AppendLine(")");
			builder.OpenBlock();
			if (notification)
			{
				builder.Append("this.Context.ObserveNotification(this.").Append(helper).Append("(").Append(arguments).AppendLine("));");
				builder.CloseBlock();
				builder.Append("private async global::System.Threading.Tasks.ValueTask ").Append(helper).Append("(").Append(parameters).AppendLine(")");
				builder.OpenBlock();
			}
			else if (enumerable)
			{
				builder.Append("return this.").Append(helper).Append("(").Append(arguments).AppendLine(");");
				builder.CloseBlock();
				string iteratorParameters = string.Join(", ", method.Symbol.Parameters.Select((p, index) =>
					(p.Equals(method.Symbol.Parameters.Last(), SymbolEqualityComparer.Default) && method.HasCancellationToken ? "[global::System.Runtime.CompilerServices.EnumeratorCancellation] " : string.Empty)
					+ $"{GetTypeName(p.Type, p.NullableAnnotation)} __arg{index}"));
				if (!method.HasCancellationToken)
				{
					iteratorParameters += (iteratorParameters.Length == 0 ? string.Empty : ", ") + "[global::System.Runtime.CompilerServices.EnumeratorCancellation] global::System.Threading.CancellationToken __enumerationCancellation = default";
				}

				builder.Append("private async ").Append(returnType).Append(" ").Append(helper).Append("(").Append(iteratorParameters).AppendLine(")");
				builder.OpenBlock();
			}

			builder.Append("using global::Nerdbank.JsonRpc.BrokeredServices.ServiceProxyLease __lease = await this.Context.RentAsync(").Append(token).AppendLine(").ConfigureAwait(false);");
			for (int parameterIndex = 0; parameterIndex < method.Symbol.Parameters.Length; parameterIndex++)
			{
				ITypeSymbol parameterType = method.Symbol.Parameters[parameterIndex].Type;
				string? builtIn = parameterType is INamedTypeSymbol namedParameter ? namedParameter.ConstructedFrom.ToDisplayString() : null;
				AttributeData? attribute = GetLocalMarshalableAttribute(parameterType);
				if (attribute is not null || builtIn is "System.IObserver<T>" or "System.IProgress<T>")
				{
					builder.Append("var __callback").Append(parameterIndex).Append(" = this.Context.").Append(attribute is null ? "Wrap" : "WrapProxy").Append("(__arg").Append(parameterIndex).Append(", out var __boundary").Append(parameterIndex).AppendLine(");");
					bool scoped = builtIn == "System.IProgress<T>" || (attribute is not null && IsLocalCallScoped(attribute));
					if (scoped)
					{
						builder.Append("using global::System.IDisposable? __callbackLifetime").Append(parameterIndex).Append(" = __boundary").Append(parameterIndex).AppendLine(";");
					}
				}
			}

			if (method.ExplicitImplementation || method.OptionalInterfaceId is not null)
			{
				builder.Append("if (!this.Is(typeof(").Append(contract).AppendLine(")))");
				builder.OpenBlock();
				builder.AppendLine("throw new global::System.NotSupportedException(\"This service implementation does not enable the requested interface.\");");
				builder.CloseBlock();
			}

			if (enumerable)
			{
				builder.Append("global::System.Collections.Generic.IAsyncEnumerator<").Append(elementType!).AppendLine("> __enumerator;");
			}

			builder.AppendLine("try");
			builder.OpenBlock();
			if (enumerable)
			{
				builder.Append("__enumerator = ");
			}
			else if (method.Kind is ProxyMethodKind.TaskOfT or ProxyMethodKind.ValueTaskOfT)
			{
				builder.Append(subscription || resultRetainer is not null ? "var __subscription = await " : "return await ");
			}
			else if (!notification)
			{
				builder.Append("await ");
			}

			builder.Append("((").Append(contract).Append(")__lease.Target).").Append(EscapeIdentifier(method.Symbol.Name)).Append("(").Append(targetArguments).Append(")");
			if (enumerable)
			{
				builder.Append(".GetAsyncEnumerator(").Append(token).Append(")");
			}
			else if (!notification)
			{
				builder.Append(".ConfigureAwait(false)");
			}

			builder.AppendLine(";");
			if (subscription)
			{
				builder.Append("return this.Context.RetainSubscription(__subscription, __lease");
				if (method.Symbol.Parameters.Any(static parameter => parameter.Type is INamedTypeSymbol named && named.ConstructedFrom.ToDisplayString() == "System.IObserver<T>")
					&& !method.Symbol.Parameters.Any(static parameter => GetLocalMarshalableAttribute(parameter.Type) is { } attribute && IsLocalCallScoped(attribute)))
				{
					string replayArguments = string.Join(", ", method.Symbol.Parameters.Select((parameter, index) =>
						method.HasCancellationToken && index == method.Symbol.Parameters.Length - 1 ? "__cancellationToken"
							: parameter.Type is INamedTypeSymbol named && named.ConstructedFrom.ToDisplayString() == "System.IObserver<T>"
								? "this.Context.Wrap(__arg" + index + ")"
								: GetLocalMarshalableAttribute(parameter.Type) is not null ? "this.Context.WrapProxy(__arg" + index + ")" : "__arg" + index));
					builder.Append(", async (__target, __cancellationToken) => await ((").Append(contract).Append(")__target).").Append(EscapeIdentifier(method.Symbol.Name)).Append("(").Append(replayArguments).Append(").ConfigureAwait(false)");
				}

				builder.AppendLine(");");
			}
			else if (resultRetainer is not null && !enumerable)
			{
				builder.Append("return this.Context.").Append(resultRetainer).AppendLine("(__subscription, __lease);");
			}

			builder.CloseBlock();
			builder.AppendLine("catch (global::System.Exception __exception)");
			builder.OpenBlock();
			RenderLocalCallbackCleanup(builder, method.Symbol);
			builder.AppendLine("throw this.Context.TranslateException(__exception);");
			builder.CloseBlock();
			if (enumerable)
			{
				builder.AppendLine("try");
				builder.OpenBlock();
				builder.AppendLine("while (true)");
				builder.OpenBlock();
				builder.Append(elementType!).AppendLine(" __current;");
				builder.AppendLine("try");
				builder.OpenBlock();
				builder.AppendLine("if (!await __enumerator.MoveNextAsync().ConfigureAwait(false)) break;");
				builder.AppendLine("__current = __enumerator.Current;");
				builder.CloseBlock();
				builder.AppendLine("catch (global::System.Exception __exception)");
				builder.OpenBlock();
				RenderLocalCallbackCleanup(builder, method.Symbol);
				builder.AppendLine("throw this.Context.TranslateException(__exception);");
				builder.CloseBlock();
				builder.Append("yield return ").Append(resultRetainer is null ? "__current" : "this.Context." + resultRetainer + "(__current, __lease)").AppendLine(";");
				builder.CloseBlock();
				builder.CloseBlock();
				builder.AppendLine("finally");
				builder.OpenBlock();
				builder.AppendLine("try");
				builder.OpenBlock();
				builder.AppendLine("await __enumerator.DisposeAsync().ConfigureAwait(false);");
				builder.CloseBlock();
				builder.AppendLine("catch (global::System.Exception __exception)");
				builder.OpenBlock();
				RenderLocalCallbackCleanup(builder, method.Symbol);
				builder.AppendLine("throw this.Context.TranslateException(__exception);");
				builder.CloseBlock();
				builder.CloseBlock();
			}

			builder.CloseBlock();
		}

		foreach (IEventSymbol @event in GetProxyEvents(info.Symbol).Concat(optionalInterfaces.SelectMany(static item => GetProxyEvents(item.Symbol))).Distinct(SymbolEqualityComparer.Default).OfType<IEventSymbol>())
		{
			string contract = @event.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
			string handlerType = @event.Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
			IMethodSymbol invoke = ((INamedTypeSymbol)@event.Type).DelegateInvokeMethod!;
			string eventParameters = string.Join(", ", invoke.Parameters.Select((_, index) => "__arg" + index));
			string eventArguments = string.Join(", ", invoke.Parameters.Select((_, index) => index == 0 && IsBclEventHandler((INamedTypeSymbol)@event.Type) ? "this" : "__arg" + index));
			builder.Append("event ").Append(handlerType).Append("? ").Append(contract).Append(".").Append(EscapeIdentifier(@event.Name)).AppendLine();
			builder.OpenBlock();
			foreach (bool add in new[] { true, false })
			{
				builder.Append(add ? "add" : "remove").Append(" => this.Context.UpdateEvent(");
				AppendQuoted(builder, contract + "." + @event.Name);
				builder.Append(", value, static (target, handler) => ((").Append(contract).Append(")target).").Append(EscapeIdentifier(@event.Name)).Append(" += handler, static (target, handler) => ((").Append(contract).Append(")target).").Append(EscapeIdentifier(@event.Name)).Append(" -= handler, ").Append(add ? "true" : "false");
				builder.Append(", handler => (").Append(eventParameters).Append(") => { try { handler(").Append(eventArguments).AppendLine("); } catch (global::System.Exception __exception) { this.Context.ReportCallbackException(__exception); } });");
			}

			builder.CloseBlock();
		}

		builder.CloseBlock();
	}

	private static string GetEventShapeProvider(InterfaceInfo info, ImmutableArray<OptionalInterfaceInfo> additionalInterfaces, IEventSymbol @event)
	{
		INamedTypeSymbol provider = GetProxyEvents(info.Symbol).Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, @event))
			? info.Symbol
			: additionalInterfaces.First(item => GetProxyEvents(item.Symbol).Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, @event))).Symbol;
		return GetShapeProvider(provider, info.Symbol).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
	}

	private static bool IsBclEventHandler(INamedTypeSymbol type)
		=> type.OriginalDefinition.ToDisplayString() is "System.EventHandler" or "System.EventHandler<TEventArgs>";

	private static string GetGroupProxyName(InterfaceInfo info, int index) => index == 0 ? info.ProxyName : $"{info.ProxyName}_Group{index}";

	private static string GetGeneratedMemberName(InterfaceInfo info, string baseName)
	{
		string name = baseName;
		IEnumerable<INamedTypeSymbol> interfaces = info.ProxyGroups.SelectMany(static group => group).Select(static item => item.Symbol)
			.Concat([info.Symbol]).SelectMany(static symbol => symbol.AllInterfaces.Concat([symbol]));
		while (interfaces.Any(interfaceSymbol => interfaceSymbol.GetMembers(name).Length > 0))
		{
			name += "_";
		}

		return name;
	}

	private static ImmutableArray<ShapeFieldInfo> GetShapeFields(ImmutableArray<MethodInfo> methods)
	{
		HashSet<string> seenTypeNames = new(System.StringComparer.Ordinal);
		ImmutableArray<ShapeFieldInfo>.Builder shapeFields = ImmutableArray.CreateBuilder<ShapeFieldInfo>();

		foreach (MethodInfo method in methods)
		{
			foreach (IParameterSymbol parameter in method.PayloadParameters)
			{
				AddShapeField(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), method.ShapeProvider, seenTypeNames, shapeFields);
			}

			if (method.ResultTypeName is not null)
			{
				AddShapeField(method.ResultTypeName, method.ShapeProvider, seenTypeNames, shapeFields);
			}
		}

		return shapeFields.ToImmutable();
	}

	private static void AddShapeField(string typeName, string providerName, HashSet<string> seenTypeNames, ImmutableArray<ShapeFieldInfo>.Builder shapeFields)
	{
		if (seenTypeNames.Add(typeName + "|" + providerName))
		{
			shapeFields.Add(new(typeName, $"shape{shapeFields.Count}", providerName));
		}
	}

	private static string GetTypeName(ITypeSymbol type, NullableAnnotation nullableAnnotation)
	{
		string typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
		return nullableAnnotation == NullableAnnotation.Annotated ? typeName + "?" : typeName;
	}

	private static string? GetNullableResultTypeName(MethodInfo method)
		=> method.Symbol.ReturnType is INamedTypeSymbol { TypeArguments: [{ NullableAnnotation: NullableAnnotation.Annotated, IsReferenceType: true } resultType] }
			? resultType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "?"
			: null;

	private static void AppendResultRequestStart(SourceWriter builder, MethodInfo method)
	{
		builder.Append("return this.jsonRpc.RequestAsync");
		if (GetNullableResultTypeName(method) is { } nullableResultTypeName)
		{
			builder.Append("<").Append(nullableResultTypeName).Append(">");
		}

		builder.Append("(");
	}

	private static void AppendResultShape(SourceWriter builder, MethodInfo method, ImmutableArray<ShapeFieldInfo> shapeFields)
	{
		// The shape is keyed by the unannotated type, so an explicit cast bridges the nullability difference.
		if (GetNullableResultTypeName(method) is { } nullableResultTypeName)
		{
			builder.Append("(global::PolyType.ITypeShape<").Append(nullableResultTypeName).Append(">)");
		}

		builder.Append("this.").Append(GetShapeFieldName(method.ResultTypeName!, method.ShapeProvider, shapeFields)).Append(", ");
	}

	private static void RenderMethod(SourceWriter builder, MethodInfo method, ImmutableArray<ShapeFieldInfo> shapeFields, string? transformedRpcNameField, string? methodNameTransformField, bool isMarshalable, string? lifetimeField)
	{
		string parameters = string.Join(", ", method.Symbol.Parameters.Select(static p => $"{GetTypeName(p.Type, p.NullableAnnotation)} {EscapeIdentifier(p.Name)}"));
		string cancellationToken = method.HasCancellationToken ? EscapeIdentifier(method.Symbol.Parameters[^1].Name) : KnownApis.CancellationToken + ".None";

		if (method.OptionalInterfaceId is null && !method.ExplicitImplementation)
		{
			builder.Append("public ");
		}

		builder.Append(method.Symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier))).Append(' ');
		if (method.OptionalInterfaceId is not null || method.ExplicitImplementation)
		{
			builder.Append(method.Symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append('.');
		}

		builder.Append(EscapeIdentifier(method.Symbol.Name)).Append('(').Append(parameters).AppendLine(")");
		builder.OpenBlock();

		if (lifetimeField is not null && IsDisposeMethod(method))
		{
			builder.Append("if (this.").Append(lifetimeField).AppendLine("Owned)");
			builder.OpenBlock();
			builder.Append("global::System.Threading.Interlocked.Exchange(ref this.").Append(lifetimeField).AppendLine(", null)?.Dispose();");
			builder.AppendLine("return;");
			builder.CloseBlock();
		}

		if (isMarshalable && IsDisposeMethod(method))
		{
			builder.AppendLine("this.jsonRpc.NotifyAsync(\"dispose\", default, global::System.Threading.CancellationToken.None).Preserve();");
			builder.AppendLine("return;");
			builder.CloseBlock();
			return;
		}

		if (method.Kind is not ProxyMethodKind.Unsupported)
		{
			builder.Append("using global::Nerdbank.JsonRpc.JsonRpcArgumentsBuilder __argumentsBuilder = this.jsonRpc.CreateArguments(").Append("this.useNamedArguments, ").Append(method.PayloadParameters.Length).Append(", ").Append(cancellationToken).AppendLine(");");
			foreach (IParameterSymbol parameter in method.PayloadParameters)
			{
				builder.Append("__argumentsBuilder.Add(");
				AppendQuoted(builder, parameter.Name);
				builder.Append(", ").Append(EscapeIdentifier(parameter.Name));
				if (parameter.NullableAnnotation == NullableAnnotation.Annotated && parameter.Type.IsReferenceType)
				{
					builder.Append('!');
				}

				builder.Append(", this.")
					.Append(GetShapeFieldName(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), method.ShapeProvider, shapeFields))
					.AppendLine(");");
			}

			builder.AppendLine("global::Nerdbank.JsonRpc.JsonRpcValue __arguments = __argumentsBuilder.BuildForSingleUse();");

			switch (method.Kind)
			{
				case ProxyMethodKind.ValueTaskOfT:
					AppendResultRequestStart(builder, method);
					AppendRpcMethodName(builder, method, transformedRpcNameField, methodNameTransformField).Append(", __arguments, ");
					AppendResultShape(builder, method, shapeFields);
					builder.Append(cancellationToken).AppendLine(");");
					break;
				case ProxyMethodKind.TaskOfT:
					AppendResultRequestStart(builder, method);
					AppendRpcMethodName(builder, method, transformedRpcNameField, methodNameTransformField).Append(", __arguments, ");
					AppendResultShape(builder, method, shapeFields);
					builder.Append(cancellationToken).AppendLine(").AsTask();");
					break;
				case ProxyMethodKind.ValueTask:
					builder.Append("return this.jsonRpc.RequestAsync(");
					AppendRpcMethodName(builder, method, transformedRpcNameField, methodNameTransformField).Append(", __arguments, ");
					builder.Append(cancellationToken).AppendLine(");");
					break;
				case ProxyMethodKind.Task:
					builder.Append("return this.jsonRpc.RequestAsync(");
					AppendRpcMethodName(builder, method, transformedRpcNameField, methodNameTransformField).Append(", __arguments, ");
					builder.Append(cancellationToken).AppendLine(").AsTask();");
					break;
				case ProxyMethodKind.AsyncEnumerableOfT:
					builder.Append("return global::Nerdbank.JsonRpc.JsonRpcEnumerableExtensions.RequestEnumerable(this.jsonRpc, ");
					AppendRpcMethodName(builder, method, transformedRpcNameField, methodNameTransformField).Append(", __arguments, ");
					builder.Append("this.").Append(GetShapeFieldName(method.ResultTypeName!, method.ShapeProvider, shapeFields)).Append(", ");
					builder.Append(cancellationToken).AppendLine(");");
					break;
				case ProxyMethodKind.Notification:
					builder.Append("this.jsonRpc.NotifyAsync(");
					if (isMarshalable && IsDisposeMethod(method))
					{
						AppendQuoted(builder, "dispose");
					}
					else
					{
						AppendRpcMethodName(builder, method, transformedRpcNameField, methodNameTransformField);
					}

					builder.Append(", __arguments, ");
					builder.Append(cancellationToken).AppendLine(").Preserve();");
					builder.AppendLine("return;");
					break;
			}
		}
		else
		{
			builder.Append("throw new global::System.NotSupportedException(");
			AppendQuoted(builder, $"Generated proxies currently support only ValueTask<T>, Task<T>, ValueTask, Task, and void methods. Unsupported method: {method.Symbol.Name}.");
			builder.AppendLine(");");
		}

		builder.CloseBlock();
	}

	private static bool IsDisposeMethod(MethodInfo method)
		=> method.Symbol.Name == KnownApis.Dispose && method.Symbol.ContainingType.ToDisplayString() == KnownApis.IDisposable && method.Symbol.Parameters.Length == 0;

	/// <summary>
	/// Appends a C# expression that evaluates to the JSON-RPC wire name for the given method: the exact
	/// <see cref="MethodInfo.ExplicitRpcName"/> when set, or otherwise a cached, once-computed application of
	/// the proxy's configured method name transform to the CLR method name.
	/// </summary>
	/// <param name="builder">The builder to append the expression to.</param>
	/// <param name="method">The method whose wire name expression is being emitted.</param>
	/// <param name="transformedRpcNameField">The generated field used to cache the transformed name, or <see langword="null"/> for explicit names.</param>
	/// <param name="methodNameTransformField">The generated field containing the configured transform.</param>
	/// <returns><paramref name="builder"/>, for chaining.</returns>
	private static SourceWriter AppendRpcMethodName(SourceWriter builder, MethodInfo method, string? transformedRpcNameField, string? methodNameTransformField)
	{
		if (method.OptionalInterfaceId is int interfaceId)
		{
			builder.Append("((");
			foreach (int route in method.OptionalRoutes.Where(route => route != interfaceId))
			{
				builder.Append("global::Nerdbank.JsonRpc.JsonRpcExtensions.IsOptionalInterfaceAdvertised(this.jsonRpc, ").Append(route).Append(") ? ");
				AppendQuoted(builder, route.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
				builder.Append(" : ");
			}

			AppendQuoted(builder, interfaceId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
			builder.Append(") + ");
		}

		if (method.ExplicitRpcName is string explicitRpcName)
		{
			AppendQuoted(builder, explicitRpcName);
			return method.OptionalInterfaceId is null ? builder : builder.Append(")");
		}

		builder.Append("(this.").Append(transformedRpcNameField).Append(" ??= this.").Append(methodNameTransformField).Append("(");
		AppendQuoted(builder, method.Symbol.Name);
		builder.Append("))");
		return method.OptionalInterfaceId is null ? builder : builder.Append(")");
	}

	private static string GetShapeFieldName(string typeName, string providerName, ImmutableArray<ShapeFieldInfo> shapeFields)
	{
		foreach (ShapeFieldInfo shapeField in shapeFields)
		{
			if (shapeField.TypeName == typeName && shapeField.ProviderName == providerName)
			{
				return shapeField.FieldName;
			}
		}

		throw new InvalidOperationException($"No cached shape field found for type '{typeName}'.");
	}

	private static SourceWriter AppendQuoted(SourceWriter builder, string value)
		=> builder.Append(Microsoft.CodeAnalysis.CSharp.SyntaxFactory.Literal(value).ToFullString());

	private static string EscapeIdentifier(string identifier)
		=> SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None && SyntaxFacts.GetContextualKeywordKind(identifier) == SyntaxKind.None ? identifier : "@" + identifier;

	private static string GetAccessibility(Accessibility accessibility)
		=> accessibility switch
		{
			Accessibility.Public => "public",
			Accessibility.Private => "private",
			Accessibility.Protected => "protected",
			Accessibility.Internal => "internal",
			Accessibility.ProtectedOrInternal => "protected internal",
			Accessibility.ProtectedAndInternal => "private protected",
			_ => "internal",
		};

	private sealed record ExtensionInfo(string Namespace, string TypeName, string Visibility);

	private sealed record ProxyOutput(string HintName, string? Source, ImmutableArray<DiagnosticInfo> Diagnostics)
	{
		internal ImmutableArray<ExtensionInfo> Extensions { get; init; } = [];
	}

	private sealed class ProxyOutputComparer : IEqualityComparer<ProxyOutput>
	{
		internal static readonly ProxyOutputComparer Instance = new();

		public bool Equals(ProxyOutput? x, ProxyOutput? y)
			=> ReferenceEquals(x, y) || (x is not null && y is not null
				&& x.HintName == y.HintName
				&& x.Source == y.Source
				&& x.Diagnostics.SequenceEqual(y.Diagnostics)
				&& x.Extensions.SequenceEqual(y.Extensions));

		public int GetHashCode(ProxyOutput obj)
		{
			int hash = 17;
			hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(obj.HintName);
			hash = (hash * 31) + (obj.Source is null ? 0 : StringComparer.Ordinal.GetHashCode(obj.Source));
			foreach (DiagnosticInfo diagnostic in obj.Diagnostics)
			{
				hash = (hash * 31) + diagnostic.GetHashCode();
			}

			return hash;
		}
	}

	private sealed record DiagnosticInfo(
		bool IsMethodDiagnostic,
		string FilePath,
		int SpanStart,
		int SpanLength,
		int StartLine,
		int StartCharacter,
		int EndLine,
		int EndCharacter,
		string Argument0,
		string Argument1)
	{
		internal static DiagnosticInfo Create(bool isMethodDiagnostic, Location? location, string argument0, string argument1)
		{
			FileLinePositionSpan lineSpan = location?.GetLineSpan() ?? default;
			return new DiagnosticInfo(
				isMethodDiagnostic,
				location?.SourceTree?.FilePath ?? string.Empty,
				location?.SourceSpan.Start ?? 0,
				location?.SourceSpan.Length ?? 0,
				lineSpan.StartLinePosition.Line,
				lineSpan.StartLinePosition.Character,
				lineSpan.EndLinePosition.Line,
				lineSpan.EndLinePosition.Character,
				argument0,
				argument1);
		}

		internal Diagnostic ToDiagnostic()
		{
			DiagnosticDescriptor descriptor = this.IsMethodDiagnostic ? UnsupportedMethodSignature : UnsupportedInterface;
			Location location = this.FilePath.Length == 0
				? Location.None
				: Location.Create(
					this.FilePath,
					new TextSpan(this.SpanStart, this.SpanLength),
					new LinePositionSpan(new LinePosition(this.StartLine, this.StartCharacter), new LinePosition(this.EndLine, this.EndCharacter)));
			return Diagnostic.Create(descriptor, location, this.Argument0, this.Argument1);
		}
	}

	private sealed record InterfaceInfo(INamedTypeSymbol Symbol, INamedTypeSymbol? EventShapeAttribute, ImmutableArray<MethodInfo> Methods, ImmutableArray<OptionalInterfaceInfo> OptionalInterfaces, bool HasStaticTypeShapeResolver, bool IsMarshalable, bool IsCallScoped, bool HasContractAttribute, ImmutableArray<DiagnosticInfo> Diagnostics)
	{
		internal ImmutableArray<ImmutableArray<OptionalInterfaceInfo>> ProxyGroups { get; init; } = [];

		internal string HintName => this.Symbol.ContainingNamespace.IsGlobalNamespace ? this.ProxyName + ".g.cs" : this.Symbol.ContainingNamespace.ToDisplayString() + "." + this.ProxyName + ".g.cs";

		internal string InterfaceName => this.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

		internal string ProxyTypeName => this.Symbol.ContainingNamespace.IsGlobalNamespace
			? "global::" + this.ProxyName
			: "global::" + this.Symbol.ContainingNamespace.ToDisplayString() + "." + this.ProxyName;

		internal string ProxyName => this.Symbol.Name.StartsWith("I", System.StringComparison.Ordinal) && this.Symbol.Name.Length > 1 && char.IsUpper(this.Symbol.Name[1])
			? this.Symbol.Name.Substring(1) + "Proxy"
			: this.Symbol.Name + "Proxy";
	}

	private sealed record MethodInfo(
		IMethodSymbol Symbol,
		ImmutableArray<IParameterSymbol> PayloadParameters,
		bool HasCancellationToken,
		ProxyMethodKind Kind,
		string? ResultTypeName,
		string? ExplicitRpcName,
		int? OptionalInterfaceId,
		string ShapeProvider,
		bool ExplicitImplementation = false)
	{
		internal ImmutableArray<int> OptionalRoutes { get; init; } = [];
	}

	private sealed record OptionalInterfaceInfo(int? InterfaceId, INamedTypeSymbol Symbol, ImmutableArray<MethodInfo> Methods);

	private sealed record ShapeFieldInfo(string TypeName, string FieldName, string ProviderName);
}
