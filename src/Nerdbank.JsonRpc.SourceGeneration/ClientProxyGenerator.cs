// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Nerdbank.JsonRpc.SourceGeneration;

/// <summary>
/// Generates JSON-RPC client proxy implementations for interfaces annotated with <c>GenerateJsonRpcProxyAttribute</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ClientProxyGenerator : IIncrementalGenerator
{
	private const string DiagnosticHelpLink = "https://aarnott.github.io/Nerdbank.JsonRpc/analyzers/NBJSONRPC001.html";
	private const int MaxOptionalInterfaces = 8;

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
			KnownApis.GenerateJsonRpcProxyAttribute,
			static (node, _) => node is InterfaceDeclarationSyntax,
			static (ctx, cancellationToken) => CreateProxyOutput(ctx, hasGenerateProxyAttribute: true, cancellationToken)!)
			.WithTrackingName("GenerateJsonRpcProxyModels")
			.WithComparer(ProxyOutputComparer.Instance)
			.WithTrackingName("GenerateJsonRpcProxyOutputs");

		IncrementalValuesProvider<ProxyOutput> marshalableInterfaces = context.SyntaxProvider.ForAttributeWithMetadataName(
			KnownApis.RpcMarshalableAttribute,
			static (node, _) => node is InterfaceDeclarationSyntax,
			static (ctx, cancellationToken) => CreateProxyOutput(ctx, hasGenerateProxyAttribute: false, cancellationToken))
			.Where(static output => output is not null)
			.Select(static (output, _) => output!)
			.WithTrackingName("RpcMarshalableProxyModels")
			.WithComparer(ProxyOutputComparer.Instance)
			.WithTrackingName("RpcMarshalableProxyOutputs");

		context.RegisterSourceOutput(proxyInterfaces, static (ctx, output) => EmitProxy(ctx, output));
		context.RegisterSourceOutput(marshalableInterfaces, static (ctx, output) => EmitProxy(ctx, output));
	}

	private static bool HasAttribute(INamedTypeSymbol symbol, INamedTypeSymbol? attributeSymbol)
		=> attributeSymbol is not null && symbol.GetAttributes().Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeSymbol));

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

	private static ProxyOutput? CreateProxyOutput(GeneratorAttributeSyntaxContext context, bool hasGenerateProxyAttribute, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		INamedTypeSymbol interfaceSymbol = (INamedTypeSymbol)context.TargetSymbol;
		Compilation compilation = context.SemanticModel.Compilation;
		INamedTypeSymbol? generateProxyAttribute = compilation.GetTypeByMetadataName(KnownApis.GenerateJsonRpcProxyAttribute);
		INamedTypeSymbol? rpcMarshalableAttribute = compilation.GetTypeByMetadataName(KnownApis.RpcMarshalableAttribute);
		INamedTypeSymbol? optionalInterfaceAttribute = compilation.GetTypeByMetadataName(KnownApis.RpcMarshalableOptionalInterfaceAttribute);
		bool hasGeneratedProxyAttribute = hasGenerateProxyAttribute || HasAttribute(interfaceSymbol, generateProxyAttribute);
		if (!hasGenerateProxyAttribute && hasGeneratedProxyAttribute)
		{
			return null;
		}

		bool isMarshalable = !hasGenerateProxyAttribute || HasAttribute(interfaceSymbol, rpcMarshalableAttribute);
		bool isCallScoped = isMarshalable && GetCallScopedLifetime(interfaceSymbol, rpcMarshalableAttribute);
		InterfaceInfo info = CreateInterfaceInfo(
			interfaceSymbol,
			(InterfaceDeclarationSyntax)context.TargetNode,
			compilation,
			isMarshalable,
			isCallScoped,
			hasGeneratedProxyAttribute,
			rpcMarshalableAttribute,
			optionalInterfaceAttribute,
			cancellationToken);
		ImmutableArray<DiagnosticInfo> diagnostics = info.Diagnostics;
		string? source = diagnostics.IsEmpty ? RenderProxy(info) : null;
		return new ProxyOutput(info.HintName, source, diagnostics);
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

	private static InterfaceInfo CreateInterfaceInfo(INamedTypeSymbol interfaceSymbol, InterfaceDeclarationSyntax interfaceDeclaration, Compilation compilation, bool isMarshalable, bool isCallScoped, bool hasGenerateProxyAttribute, INamedTypeSymbol? marshalableAttribute, INamedTypeSymbol? optionalInterfaceAttribute, CancellationToken cancellationToken)
	{
		INamedTypeSymbol? methodShapeAttribute = compilation.GetTypeByMetadataName(KnownApis.MethodShapeAttribute);
		ImmutableArray<MethodInfo>.Builder methods = ImmutableArray.CreateBuilder<MethodInfo>();
		ImmutableArray<DiagnosticInfo>.Builder diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
		ImmutableArray<OptionalInterfaceInfo>.Builder optionalInterfaces = ImmutableArray.CreateBuilder<OptionalInterfaceInfo>();
		if (!interfaceDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword))
		{
			diagnostics.Add(DiagnosticInfo.Create(isMethodDiagnostic: false, interfaceDeclaration.Identifier.GetLocation(), interfaceSymbol.ToDisplayString(), "annotated interfaces must be partial"));
			return new InterfaceInfo(interfaceSymbol, methods.ToImmutable(), optionalInterfaces.ToImmutable(), HasStaticTypeShapeResolver(compilation), isMarshalable, isCallScoped, hasGenerateProxyAttribute, diagnostics.ToImmutable());
		}

		if (GetUnsupportedInterfaceReason(interfaceSymbol, isMarshalable, isCallScoped) is string interfaceReason)
		{
			diagnostics.Add(DiagnosticInfo.Create(isMethodDiagnostic: false, interfaceSymbol.Locations.FirstOrDefault(), interfaceSymbol.ToDisplayString(), interfaceReason));
			return new InterfaceInfo(interfaceSymbol, methods.ToImmutable(), optionalInterfaces.ToImmutable(), HasStaticTypeShapeResolver(compilation), isMarshalable, isCallScoped, hasGenerateProxyAttribute, diagnostics.ToImmutable());
		}

		foreach (IMethodSymbol method in GetProxyMethods(interfaceSymbol))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (GetUnsupportedSignatureReason(method, isMarshalable, marshalableAttribute) is string reason)
			{
				diagnostics.Add(DiagnosticInfo.Create(isMethodDiagnostic: true, method.Locations.FirstOrDefault(), method.ToDisplayString(), reason));
				continue;
			}

			methods.Add(CreateMethodInfo(method, methodShapeAttribute, optionalInterfaceId: null));
		}

		if (isMarshalable && optionalInterfaceAttribute is not null)
		{
			ImmutableArray<AttributeData> optionalInterfaceAttributes = interfaceSymbol.GetAttributes().Where(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, optionalInterfaceAttribute)).ToImmutableArray();
			if (optionalInterfaceAttributes.Length > MaxOptionalInterfaces)
			{
				diagnostics.Add(DiagnosticInfo.Create(isMethodDiagnostic: false, interfaceSymbol.Locations.FirstOrDefault(), interfaceSymbol.ToDisplayString(), $"at most {MaxOptionalInterfaces} optional interfaces are supported"));
			}

			HashSet<int> interfaceIds = [];
			foreach (AttributeData attribute in optionalInterfaceAttributes.Take(MaxOptionalInterfaces))
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

				foreach (IMethodSymbol method in GetProxyMethods(optionalInterface))
				{
					if (GetUnsupportedSignatureReason(method, isMarshalable: true, marshalableAttribute) is string reason)
					{
						diagnostics.Add(DiagnosticInfo.Create(isMethodDiagnostic: true, method.Locations.FirstOrDefault(), method.ToDisplayString(), reason));
						continue;
					}

					optionalMethods.Add(CreateMethodInfo(method, methodShapeAttribute, interfaceId));
				}

				optionalInterfaces.Add(new(interfaceId, optionalInterface, optionalMethods.ToImmutable()));
			}
		}

		return new InterfaceInfo(interfaceSymbol, methods.ToImmutable(), optionalInterfaces.ToImmutable(), HasStaticTypeShapeResolver(compilation), isMarshalable, isCallScoped, hasGenerateProxyAttribute, diagnostics.ToImmutable());
	}

	private static IEnumerable<IMethodSymbol> GetProxyMethods(INamedTypeSymbol interfaceSymbol)
	{
		HashSet<string> seenMethods = new(System.StringComparer.Ordinal);
		foreach (INamedTypeSymbol currentInterface in interfaceSymbol.AllInterfaces.Concat([interfaceSymbol]))
		{
			foreach (IMethodSymbol method in currentInterface.GetMembers().OfType<IMethodSymbol>().Where(static method => method.MethodKind == MethodKind.Ordinary))
			{
				if (seenMethods.Add(GetMethodSignatureKey(method)))
				{
					yield return method;
				}
			}
		}
	}

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

	private static string? GetUnsupportedSignatureReason(IMethodSymbol method, bool isMarshalable, INamedTypeSymbol? marshalableAttribute)
	{
		if (method.ReturnsVoid && method.Parameters.Any(parameter => ContainsRpcMarshalableInterface(parameter.Type, marshalableAttribute)))
		{
			return "notification methods cannot accept RPC-marshalable interface parameters";
		}

		if (method.IsGenericMethod)
		{
			return "generic methods are not supported yet";
		}

		bool isDisposeMethod = method.Name == KnownApis.Dispose && method.ContainingType.ToDisplayString() == KnownApis.IDisposable && method.Parameters.Length == 0;
		if (isMarshalable && !isDisposeMethod && method.ReturnsVoid)
		{
			return "marshalable interface methods must return Task or ValueTask";
		}

		for (int parameterIndex = 0; parameterIndex < method.Parameters.Length; parameterIndex++)
		{
			IParameterSymbol parameter = method.Parameters[parameterIndex];
			if (parameter.RefKind is not RefKind.None)
			{
				return "ref, out, and in parameters are not supported yet";
			}

			if (IsCancellationToken(parameter.Type) && parameterIndex != method.Parameters.Length - 1)
			{
				return "CancellationToken parameters must appear last";
			}

			if (parameter.IsParams)
			{
				return "params parameters are not supported yet";
			}

			if (parameter.HasExplicitDefaultValue)
			{
				return "optional parameters with default values are not supported yet";
			}
		}

		if (GetMethodKind(method.ReturnType, out _) is ProxyMethodKind.Unsupported)
		{
			return $"return type '{method.ReturnType.ToDisplayString()}' is not supported yet";
		}

		return null;
	}

	private static bool ContainsRpcMarshalableInterface(ITypeSymbol type, INamedTypeSymbol? marshalableAttribute)
	{
		if (type is IArrayTypeSymbol arrayType)
		{
			return ContainsRpcMarshalableInterface(arrayType.ElementType, marshalableAttribute);
		}

		if (type is not INamedTypeSymbol namedType)
		{
			return false;
		}

		return (namedType.TypeKind == TypeKind.Interface && HasAttribute(namedType, marshalableAttribute))
			|| namedType.TypeArguments.Any(argument => ContainsRpcMarshalableInterface(argument, marshalableAttribute));
	}

	private static bool IsCancellationToken(ITypeSymbol type)
		=> type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == KnownApis.CancellationToken;

	private static bool HasStaticTypeShapeResolver(Compilation compilation)
	{
		INamedTypeSymbol? resolver = compilation.GetTypeByMetadataName(KnownApis.TypeShapeResolver);
		return resolver?.GetMembers(KnownApis.TypeShapeResolve)
			.OfType<IMethodSymbol>()
			.Any(static method => method.IsGenericMethod && method.TypeParameters.Length == 1 && method.ContainingAssembly.Name == KnownApis.PolyTypeAssembly) is true;
	}

	private static MethodInfo CreateMethodInfo(IMethodSymbol method, INamedTypeSymbol? methodShapeAttribute, int? optionalInterfaceId)
	{
		bool hasCancellationToken = method.Parameters.LastOrDefault() is { } lastParameter && IsCancellationToken(lastParameter.Type);
		ImmutableArray<IParameterSymbol> payloadParameters = hasCancellationToken
			? method.Parameters.Take(method.Parameters.Length - 1).ToImmutableArray()
			: method.Parameters.ToImmutableArray();

		ProxyMethodKind methodKind = GetMethodKind(method.ReturnType, out string? resultTypeName);
		string? explicitRpcName = GetExplicitRpcName(method, methodShapeAttribute);

		return new MethodInfo(method, payloadParameters, hasCancellationToken, methodKind, resultTypeName, explicitRpcName, optionalInterfaceId);
	}

	/// <summary>
	/// Gets the RPC method name explicitly assigned via <c>[MethodShape(Name = "...")]</c> on the given method, if any.
	/// </summary>
	/// <param name="method">The method to inspect.</param>
	/// <param name="methodShapeAttribute">The symbol for the PolyType method shape attribute, if available.</param>
	/// <returns>The explicit name, or <see langword="null"/> if the method has no explicit <c>MethodShapeAttribute.Name</c>.</returns>
	private static string? GetExplicitRpcName(IMethodSymbol method, INamedTypeSymbol? methodShapeAttribute)
	{
		foreach (AttributeData attribute in method.GetAttributes())
		{
			if (methodShapeAttribute is null || !SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, methodShapeAttribute))
			{
				continue;
			}

			foreach (KeyValuePair<string, TypedConstant> namedArgument in attribute.NamedArguments)
			{
				if (namedArgument.Key == KnownApis.MethodShapeName && namedArgument.Value.Value is string explicitName)
				{
					return explicitName;
				}
			}
		}

		return null;
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

		builder.Append("[global::Nerdbank.JsonRpc.JsonRpcProxyImplementationAttribute(typeof(").Append(info.ProxyTypeName).AppendLine("))]");
		ImmutableArray<OptionalInterfaceInfo> optionalInterfaces = info.OptionalInterfaces.OrderBy(static optional => optional.InterfaceId).ToImmutableArray();
		int variantCount = 1 << optionalInterfaces.Length;
		for (int mask = 1; mask < variantCount; mask++)
		{
			string proxyTypeName = info.Symbol.ContainingNamespace.IsGlobalNamespace
				? "global::" + GetVariantProxyName(info, mask)
				: "global::" + info.Symbol.ContainingNamespace.ToDisplayString() + "." + GetVariantProxyName(info, mask);
			builder.Append("[global::Nerdbank.JsonRpc.JsonRpcOptionalProxyImplementationAttribute(typeof(").Append(proxyTypeName).Append(')');
			for (int index = 0; index < optionalInterfaces.Length; index++)
			{
				if ((mask & (1 << index)) != 0)
				{
					builder.Append(", ").Append(optionalInterfaces[index].InterfaceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
				}
			}

			builder.AppendLine(")]");
		}

		builder.Append(GetAccessibility(info.Symbol.DeclaredAccessibility)).Append(" partial interface ").Append(info.Symbol.Name).AppendLine();
		builder.OpenBlock();
		builder.CloseBlock();
		builder.AppendLine();

		RenderProxyClass(builder, info, info.ProxyName, info.Methods, []);
		for (int mask = 1; mask < variantCount; mask++)
		{
			ImmutableArray<OptionalInterfaceInfo> implemented = optionalInterfaces.Where((_, index) => (mask & (1 << index)) != 0).ToImmutableArray();
			ImmutableArray<MethodInfo>.Builder methods = ImmutableArray.CreateBuilder<MethodInfo>();
			HashSet<string> baseMethodSignatures = new(StringComparer.Ordinal);
			foreach (MethodInfo method in info.Methods)
			{
				baseMethodSignatures.Add(GetMethodSignatureKey(method.Symbol));
				methods.Add(method);
			}

			foreach (MethodInfo method in implemented.SelectMany(static optional => optional.Methods))
			{
				// A base method can implement an overlapping optional interface member. Keep distinct optional
				// interface methods because they use explicit implementations and distinct wire prefixes.
				if (!baseMethodSignatures.Contains(GetMethodSignatureKey(method.Symbol)))
				{
					methods.Add(method);
				}
			}

			builder.AppendLine();
			RenderProxyClass(builder, info, GetVariantProxyName(info, mask), methods.ToImmutable(), implemented);
		}

		return builder.ToString();
	}

	private static void RenderProxyClass(SourceWriter builder, InterfaceInfo info, string proxyName, ImmutableArray<MethodInfo> methods, ImmutableArray<OptionalInterfaceInfo> optionalInterfaces)
	{
		ImmutableArray<ShapeFieldInfo> shapeFields = GetShapeFields(methods);
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

		builder.AppendLine();
		builder.OpenBlock();
		builder.AppendLine("\tprivate readonly global::Nerdbank.JsonRpc.IJsonRpcClient jsonRpc;");
		builder.AppendLine("\tprivate readonly bool useNamedArguments;");

		if (methodNameTransformField is string transformField)
		{
			builder.Append("\tprivate readonly global::System.Func<string, string> ").Append(transformField).AppendLine(";");
		}

		for (int i = 0; i < methods.Length; i++)
		{
			if (transformedRpcNameFields[i] is string fieldName)
			{
				builder.Append("\tprivate string? ").Append(fieldName).AppendLine(";");
			}
		}

		builder.AppendLine();
		foreach (ShapeFieldInfo shapeField in shapeFields)
		{
			builder.Append("\tprivate readonly global::PolyType.ITypeShape<")
				.Append(shapeField.TypeName)
				.Append("> ")
				.Append(shapeField.FieldName)
				.AppendLine(";");
		}

		if (shapeFields.Length > 0)
		{
			builder.AppendLine();
		}

		if (shapeFields.Length > 0)
		{
			// This overload resolves the type shape provider automatically.
			builder.Append("\tinternal ").Append(proxyName).Append("(global::Nerdbank.JsonRpc.IJsonRpcClient jsonRpc, global::Nerdbank.JsonRpc.JsonRpcProxyOptions options)").AppendLine();
			builder.Append("\t\t: this(jsonRpc, options, global::PolyType.Abstractions.TypeShapeResolver.")
				.Append(info.HasStaticTypeShapeResolver ? KnownApis.TypeShapeResolve : KnownApis.TypeShapeResolveDynamicOrThrow)
				.Append('<')
				.Append(info.InterfaceName)
				.AppendLine(">().Provider)");
			builder.OpenBlock("\t");
			builder.CloseBlock("\t");
			builder.AppendLine();

			// This overload lets callers supply the provider explicitly, for targets (such as NetWasm) where it cannot be resolved automatically.
			builder.Append("\tinternal ").Append(proxyName).Append("(global::Nerdbank.JsonRpc.IJsonRpcClient jsonRpc, global::Nerdbank.JsonRpc.JsonRpcProxyOptions options, global::PolyType.ITypeShapeProvider typeShapeProvider)").AppendLine();
		}
		else
		{
			builder.Append("\tinternal ").Append(proxyName).Append("(global::Nerdbank.JsonRpc.IJsonRpcClient jsonRpc, global::Nerdbank.JsonRpc.JsonRpcProxyOptions options)").AppendLine();
		}

		builder.OpenBlock("\t");
		builder.AppendLine("\t\tthis.jsonRpc = jsonRpc;");
		builder.AppendLine("\t\tthis.useNamedArguments = options.UseNamedArguments;");
		if (needsMethodNameTransform)
		{
			builder.Append("\t\tthis.").Append(methodNameTransformField).AppendLine(" = options.MethodNameTransform;");
		}

		foreach (ShapeFieldInfo shapeField in shapeFields)
		{
			builder.Append("\t\tthis.")
				.Append(shapeField.FieldName)
				.Append(" = global::PolyType.TypeShapeProviderExtensions.GetTypeShapeOrThrow<")
				.Append(shapeField.TypeName)
				.Append(">(typeShapeProvider);")
				.AppendLine();
		}

		builder.CloseBlock("\t");
		for (int i = 0; i < methods.Length; i++)
		{
			builder.AppendLine();
			builder.Append(RenderMethod(methods[i], shapeFields, transformedRpcNameFields[i], methodNameTransformField, info.IsMarshalable));
		}

		builder.CloseBlock();
	}

	private static string GetVariantProxyName(InterfaceInfo info, int mask) => $"{info.ProxyName}_Optional{mask}";

	private static string GetGeneratedMemberName(InterfaceInfo info, string baseName)
	{
		string name = baseName;
		while (info.Symbol.GetMembers(name).Length > 0 || info.Symbol.AllInterfaces.Any(interfaceSymbol => interfaceSymbol.GetMembers(name).Length > 0))
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
				AddShapeField(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), seenTypeNames, shapeFields);
			}

			if (method.ResultTypeName is not null)
			{
				AddShapeField(method.ResultTypeName, seenTypeNames, shapeFields);
			}
		}

		return shapeFields.ToImmutable();
	}

	private static void AddShapeField(string typeName, HashSet<string> seenTypeNames, ImmutableArray<ShapeFieldInfo>.Builder shapeFields)
	{
		if (seenTypeNames.Add(typeName))
		{
			shapeFields.Add(new(typeName, $"shape{shapeFields.Count}"));
		}
	}

	private static string GetTypeName(ITypeSymbol type, NullableAnnotation nullableAnnotation)
	{
		string typeName = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
		return nullableAnnotation == NullableAnnotation.Annotated ? typeName + "?" : typeName;
	}

	private static string RenderMethod(MethodInfo method, ImmutableArray<ShapeFieldInfo> shapeFields, string? transformedRpcNameField, string? methodNameTransformField, bool isMarshalable)
	{
		SourceWriter builder = new();
		string parameters = string.Join(", ", method.Symbol.Parameters.Select(static p => $"{GetTypeName(p.Type, p.NullableAnnotation)} {EscapeIdentifier(p.Name)}"));
		string cancellationToken = method.HasCancellationToken ? EscapeIdentifier(method.Symbol.Parameters[^1].Name) : KnownApis.CancellationToken + ".None";

		if (method.OptionalInterfaceId is null)
		{
			builder.Append("\tpublic ");
		}
		else
		{
			builder.Append("\t");
		}

		builder.Append(method.Symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append(' ');
		if (method.OptionalInterfaceId is not null)
		{
			builder.Append(method.Symbol.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append('.');
		}

		builder.Append(EscapeIdentifier(method.Symbol.Name)).Append('(').Append(parameters).AppendLine(")");
		builder.OpenBlock("\t");

		if (isMarshalable && IsDisposeMethod(method))
		{
			builder.AppendLine("\t\tthis.jsonRpc.NotifyAsync(\"dispose\", default, global::System.Threading.CancellationToken.None).Preserve();");
			builder.AppendLine("\t\treturn;");
			builder.CloseBlock("\t");
			return builder.ToString();
		}

		if (method.Kind is not ProxyMethodKind.Unsupported)
		{
			builder.Append("\t\tusing global::Nerdbank.JsonRpc.JsonRpcArgumentsBuilder argumentsBuilder = this.jsonRpc.CreateArguments(").Append("this.useNamedArguments, ").Append(method.PayloadParameters.Length).Append(", ").Append(cancellationToken).AppendLine(");");
			foreach (IParameterSymbol parameter in method.PayloadParameters)
			{
				builder.Append("\t\targumentsBuilder.Add(");
				AppendQuoted(builder, parameter.Name);
				builder.Append(", ").Append(EscapeIdentifier(parameter.Name));
				if (parameter.NullableAnnotation == NullableAnnotation.Annotated && parameter.Type.IsReferenceType)
				{
					builder.Append('!');
				}

				builder.Append(", this.")
					.Append(GetShapeFieldName(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), shapeFields))
					.AppendLine(");");
			}

			builder.AppendLine("\t\tglobal::Nerdbank.JsonRpc.JsonRpcValue arguments = argumentsBuilder.BuildForSingleUse();");

			switch (method.Kind)
			{
				case ProxyMethodKind.ValueTaskOfT:
					builder.Append("\t\treturn this.jsonRpc.RequestAsync(");
					AppendRpcMethodName(builder, method, transformedRpcNameField, methodNameTransformField).Append(", arguments, ");
					builder.Append("this.").Append(GetShapeFieldName(method.ResultTypeName!, shapeFields)).Append(", ");
					builder.Append(cancellationToken).AppendLine(");");
					break;
				case ProxyMethodKind.TaskOfT:
					builder.Append("\t\treturn this.jsonRpc.RequestAsync(");
					AppendRpcMethodName(builder, method, transformedRpcNameField, methodNameTransformField).Append(", arguments, ");
					builder.Append("this.").Append(GetShapeFieldName(method.ResultTypeName!, shapeFields)).Append(", ");
					builder.Append(cancellationToken).AppendLine(").AsTask();");
					break;
				case ProxyMethodKind.ValueTask:
					builder.Append("\t\treturn this.jsonRpc.RequestAsync(");
					AppendRpcMethodName(builder, method, transformedRpcNameField, methodNameTransformField).Append(", arguments, ");
					builder.Append(cancellationToken).AppendLine(");");
					break;
				case ProxyMethodKind.Task:
					builder.Append("\t\treturn this.jsonRpc.RequestAsync(");
					AppendRpcMethodName(builder, method, transformedRpcNameField, methodNameTransformField).Append(", arguments, ");
					builder.Append(cancellationToken).AppendLine(").AsTask();");
					break;
				case ProxyMethodKind.AsyncEnumerableOfT:
					builder.Append("\t\treturn global::Nerdbank.JsonRpc.JsonRpcEnumerableExtensions.RequestEnumerable(this.jsonRpc, ");
					AppendRpcMethodName(builder, method, transformedRpcNameField, methodNameTransformField).Append(", arguments, ");
					builder.Append("this.").Append(GetShapeFieldName(method.ResultTypeName!, shapeFields)).Append(", ");
					builder.Append(cancellationToken).AppendLine(");");
					break;
				case ProxyMethodKind.Notification:
					builder.Append("\t\tthis.jsonRpc.NotifyAsync(");
					if (isMarshalable && IsDisposeMethod(method))
					{
						AppendQuoted(builder, "dispose");
					}
					else
					{
						AppendRpcMethodName(builder, method, transformedRpcNameField, methodNameTransformField);
					}

					builder.Append(", arguments, ");
					builder.Append(cancellationToken).AppendLine(").Preserve();");
					builder.AppendLine("\t\treturn;");
					break;
			}
		}
		else
		{
			builder.Append("\t\tthrow new global::System.NotSupportedException(");
			AppendQuoted(builder, $"Generated proxies currently support only ValueTask<T>, Task<T>, ValueTask, Task, and void methods. Unsupported method: {method.Symbol.Name}.");
			builder.AppendLine(");");
		}

		builder.CloseBlock("\t");

		return builder.ToString();
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
			builder.Append("(");
			AppendQuoted(builder, interfaceId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
			builder.Append(" + ");
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

	private static string GetShapeFieldName(string typeName, ImmutableArray<ShapeFieldInfo> shapeFields)
	{
		foreach (ShapeFieldInfo shapeField in shapeFields)
		{
			if (shapeField.TypeName == typeName)
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

	private sealed record ProxyOutput(string HintName, string? Source, ImmutableArray<DiagnosticInfo> Diagnostics);

	private sealed class ProxyOutputComparer : IEqualityComparer<ProxyOutput>
	{
		internal static readonly ProxyOutputComparer Instance = new();

		public bool Equals(ProxyOutput? x, ProxyOutput? y)
			=> ReferenceEquals(x, y) || (x is not null && y is not null
				&& x.HintName == y.HintName
				&& x.Source == y.Source
				&& x.Diagnostics.SequenceEqual(y.Diagnostics));

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

	private static class KnownApis
	{
		internal const string GenerateJsonRpcProxyAttribute = "Nerdbank.JsonRpc.GenerateJsonRpcProxyAttribute";
		internal const string RpcMarshalableAttribute = "Nerdbank.JsonRpc.RpcMarshalableAttribute";
		internal const string RpcMarshalableOptionalInterfaceAttribute = "Nerdbank.JsonRpc.RpcMarshalableOptionalInterfaceAttribute";
		internal const string MethodShapeAttribute = "PolyType.MethodShapeAttribute";
		internal const string TypeShapeResolver = "PolyType.Abstractions.TypeShapeResolver";
		internal const string CallScopedLifetime = "CallScopedLifetime";
		internal const string MethodShapeName = "Name";
		internal const string TypeShapeResolve = "Resolve";
		internal const string TypeShapeResolveDynamicOrThrow = "ResolveDynamicOrThrow";
		internal const string IDisposable = "System.IDisposable";
		internal const string PolyTypeAssembly = "PolyType";
		internal const string Dispose = "Dispose";
		internal const string CancellationToken = "global::System.Threading.CancellationToken";
		internal const string ValueTaskOfT = "global::System.Threading.Tasks.ValueTask<TResult>";
		internal const string IAsyncEnumerableOfT = "global::System.Collections.Generic.IAsyncEnumerable<T>";
		internal const string TaskOfT = "global::System.Threading.Tasks.Task<TResult>";
		internal const string ValueTask = "global::System.Threading.Tasks.ValueTask";
		internal const string Task = "global::System.Threading.Tasks.Task";
		internal const string Void = "void";
	}

	private sealed record InterfaceInfo(INamedTypeSymbol Symbol, ImmutableArray<MethodInfo> Methods, ImmutableArray<OptionalInterfaceInfo> OptionalInterfaces, bool HasStaticTypeShapeResolver, bool IsMarshalable, bool IsCallScoped, bool HasGenerateProxyAttribute, ImmutableArray<DiagnosticInfo> Diagnostics)
	{
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
		int? OptionalInterfaceId);

	private sealed record OptionalInterfaceInfo(int InterfaceId, INamedTypeSymbol Symbol, ImmutableArray<MethodInfo> Methods);

	private sealed record ShapeFieldInfo(string TypeName, string FieldName);
}
