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
	private static readonly DiagnosticDescriptor UnsupportedMethodSignature = new(
		"NBJSONRPC001",
		"Unsupported JSON-RPC proxy method signature",
		"Method '{0}' cannot be generated as a JSON-RPC client proxy because {1}",
		"Usage",
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor UnsupportedInterface = new(
		"NBJSONRPC001",
		"Unsupported JSON-RPC proxy interface",
		"Interface '{0}' cannot be generated as a JSON-RPC client proxy because {1}",
		"Usage",
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true);

	private enum ProxyMethodKind
	{
		Unsupported,
		ValueTaskOfT,
		TaskOfT,
		ValueTask,
		Task,
		Notification,
	}

	/// <inheritdoc />
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		IncrementalValuesProvider<InterfaceInfo> proxyInterfaces = context.SyntaxProvider.ForAttributeWithMetadataName(
			"Nerdbank.JsonRpc.GenerateJsonRpcProxyAttribute",
			static (node, _) => node is InterfaceDeclarationSyntax,
			static (ctx, _) =>
			{
				INamedTypeSymbol symbol = (INamedTypeSymbol)ctx.TargetSymbol;
				bool isMarshalable = symbol.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "Nerdbank.JsonRpc.RpcMarshalableAttribute");
				return CreateInterfaceInfo(symbol, (InterfaceDeclarationSyntax)ctx.TargetNode, ctx.SemanticModel.Compilation, isMarshalable, hasGenerateProxyAttribute: true);
			});

		IncrementalValuesProvider<InterfaceInfo> marshalableInterfaces = context.SyntaxProvider.ForAttributeWithMetadataName(
			"Nerdbank.JsonRpc.RpcMarshalableAttribute",
			static (node, _) => node is InterfaceDeclarationSyntax,
			static (ctx, _) =>
			{
				INamedTypeSymbol symbol = (INamedTypeSymbol)ctx.TargetSymbol;
				bool hasGeneratedProxyAttribute = symbol.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "Nerdbank.JsonRpc.GenerateJsonRpcProxyAttribute");
				return CreateInterfaceInfo(symbol, (InterfaceDeclarationSyntax)ctx.TargetNode, ctx.SemanticModel.Compilation, isMarshalable: true, hasGenerateProxyAttribute: hasGeneratedProxyAttribute);
			}).Where(static info => !info.HasGenerateProxyAttribute);

		context.RegisterSourceOutput(proxyInterfaces, static (ctx, info) => EmitProxy(ctx, info));
		context.RegisterSourceOutput(marshalableInterfaces, static (ctx, info) => EmitProxy(ctx, info));
	}

	private static void EmitProxy(SourceProductionContext context, InterfaceInfo info)
	{
		foreach (Diagnostic diagnostic in info.Diagnostics)
		{
			context.ReportDiagnostic(diagnostic);
		}

		if (info.Diagnostics.Length > 0)
		{
			return;
		}

		context.AddSource(info.HintName, SourceText.From(RenderProxy(info), Encoding.UTF8));
	}

	private static InterfaceInfo CreateInterfaceInfo(INamedTypeSymbol interfaceSymbol, InterfaceDeclarationSyntax interfaceDeclaration, Compilation compilation, bool isMarshalable, bool hasGenerateProxyAttribute)
	{
		INamedTypeSymbol? methodShapeAttribute = compilation.GetTypeByMetadataName("PolyType.MethodShapeAttribute");
		ImmutableArray<MethodInfo>.Builder methods = ImmutableArray.CreateBuilder<MethodInfo>();
		ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
		if (!interfaceDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword))
		{
			diagnostics.Add(Diagnostic.Create(UnsupportedInterface, interfaceDeclaration.Identifier.GetLocation(), interfaceSymbol.ToDisplayString(), "annotated interfaces must be partial"));
			return new InterfaceInfo(interfaceSymbol, methods.ToImmutable(), HasStaticTypeShapeResolver(compilation), isMarshalable, hasGenerateProxyAttribute, diagnostics.ToImmutable());
		}

		if (GetUnsupportedInterfaceReason(interfaceSymbol, isMarshalable) is string interfaceReason)
		{
			diagnostics.Add(Diagnostic.Create(UnsupportedInterface, interfaceSymbol.Locations.FirstOrDefault(), interfaceSymbol.ToDisplayString(), interfaceReason));
			return new InterfaceInfo(interfaceSymbol, methods.ToImmutable(), HasStaticTypeShapeResolver(compilation), isMarshalable, hasGenerateProxyAttribute, diagnostics.ToImmutable());
		}

		foreach (IMethodSymbol method in GetProxyMethods(interfaceSymbol))
		{
			if (GetUnsupportedSignatureReason(method, isMarshalable) is string reason)
			{
				diagnostics.Add(Diagnostic.Create(UnsupportedMethodSignature, method.Locations.FirstOrDefault(), method.ToDisplayString(), reason));
				continue;
			}

			methods.Add(CreateMethodInfo(method, methodShapeAttribute));
		}

		return new InterfaceInfo(interfaceSymbol, methods.ToImmutable(), HasStaticTypeShapeResolver(compilation), isMarshalable, hasGenerateProxyAttribute, diagnostics.ToImmutable());
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

	private static string? GetUnsupportedInterfaceReason(INamedTypeSymbol interfaceSymbol, bool isMarshalable = false)
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
			if (!interfaceSymbol.AllInterfaces.Any(static baseInterface => baseInterface.ToDisplayString() == "System.IDisposable"))
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

	private static string? GetUnsupportedSignatureReason(IMethodSymbol method, bool isMarshalable)
	{
		if (method.ReturnsVoid && method.Parameters.Any(static parameter => ContainsRpcMarshalableInterface(parameter.Type)))
		{
			return "notification methods cannot accept RPC-marshalable interface parameters";
		}

		if (method.IsGenericMethod)
		{
			return "generic methods are not supported yet";
		}

		bool isDisposeMethod = method.Name == "Dispose" && method.ContainingType.ToDisplayString() == "System.IDisposable" && method.Parameters.Length == 0;
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

	private static bool ContainsRpcMarshalableInterface(ITypeSymbol type)
	{
		if (type is IArrayTypeSymbol arrayType)
		{
			return ContainsRpcMarshalableInterface(arrayType.ElementType);
		}

		if (type is not INamedTypeSymbol namedType)
		{
			return false;
		}

		return (namedType.TypeKind == TypeKind.Interface
			&& namedType.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "Nerdbank.JsonRpc.RpcMarshalableAttribute"))
			|| namedType.TypeArguments.Any(ContainsRpcMarshalableInterface);
	}

	private static bool IsCancellationToken(ITypeSymbol type)
		=> type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Threading.CancellationToken";

	private static bool HasStaticTypeShapeResolver(Compilation compilation)
	{
		INamedTypeSymbol? resolver = compilation.GetTypeByMetadataName("PolyType.Abstractions.TypeShapeResolver");
		return resolver?.GetMembers("Resolve")
			.OfType<IMethodSymbol>()
			.Any(static method => method.IsGenericMethod && method.TypeParameters.Length == 1 && method.ContainingAssembly.Name == "PolyType") is true;
	}

	private static MethodInfo CreateMethodInfo(IMethodSymbol method, INamedTypeSymbol? methodShapeAttribute)
	{
		bool hasCancellationToken = method.Parameters.LastOrDefault() is { } lastParameter && IsCancellationToken(lastParameter.Type);
		ImmutableArray<IParameterSymbol> payloadParameters = hasCancellationToken
			? method.Parameters.Take(method.Parameters.Length - 1).ToImmutableArray()
			: method.Parameters.ToImmutableArray();

		ProxyMethodKind methodKind = GetMethodKind(method.ReturnType, out string? resultTypeName);
		string? explicitRpcName = GetExplicitRpcName(method, methodShapeAttribute);

		return new MethodInfo(method, payloadParameters, hasCancellationToken, methodKind, resultTypeName, explicitRpcName);
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
				if (namedArgument.Key == "Name" && namedArgument.Value.Value is string explicitName)
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

		if (returnTypeName == "void")
		{
			return ProxyMethodKind.Notification;
		}

		if (returnType is INamedTypeSymbol namedReturnType && namedReturnType.IsGenericType)
		{
			string genericTypeName = namedReturnType.ConstructedFrom.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
			if (genericTypeName is "global::System.Threading.Tasks.ValueTask<TResult>" or "global::System.Threading.Tasks.Task<TResult>")
			{
				resultTypeName = namedReturnType.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
				return genericTypeName == "global::System.Threading.Tasks.ValueTask<TResult>" ? ProxyMethodKind.ValueTaskOfT : ProxyMethodKind.TaskOfT;
			}
		}

		return returnTypeName switch
		{
			"global::System.Threading.Tasks.ValueTask" => ProxyMethodKind.ValueTask,
			"global::System.Threading.Tasks.Task" => ProxyMethodKind.Task,
			_ => ProxyMethodKind.Unsupported,
		};
	}

	private static string RenderProxy(InterfaceInfo info)
	{
		StringBuilder builder = new();
		ImmutableArray<ShapeFieldInfo> shapeFields = GetShapeFields(info.Methods);
		bool needsMethodNameTransform = info.Methods.Any(m => m.ExplicitRpcName is null && m.Kind is not ProxyMethodKind.Unsupported && !(info.IsMarshalable && IsDisposeMethod(m)));
		string? methodNameTransformField = needsMethodNameTransform ? GetGeneratedMemberName(info, "NerdbankJsonRpc_MethodNameTransform") : null;
		ImmutableArray<string?> transformedRpcNameFields = info.Methods
			.Select((method, index) => method is { ExplicitRpcName: null, Kind: not ProxyMethodKind.Unsupported } && !(info.IsMarshalable && IsDisposeMethod(method)) ? GetGeneratedMemberName(info, $"NerdbankJsonRpc_TransformedRpcName{index}") : null)
			.ToImmutableArray();
		builder.AppendLine("#nullable enable");
		builder.AppendLine();
		if (!info.Symbol.ContainingNamespace.IsGlobalNamespace)
		{
			builder.Append("namespace ").Append(info.Symbol.ContainingNamespace.ToDisplayString()).AppendLine(";");
			builder.AppendLine();
		}

		builder.Append("[global::Nerdbank.JsonRpc.JsonRpcProxyImplementationAttribute(typeof(").Append(info.ProxyTypeName).AppendLine("))]");
		builder.Append(GetAccessibility(info.Symbol.DeclaredAccessibility)).Append(" partial interface ").Append(info.Symbol.Name).AppendLine();
		builder.AppendLine("{");
		builder.AppendLine("}");
		builder.AppendLine();

		builder.Append("internal sealed class ").Append(info.ProxyName).Append(" : ").Append(info.InterfaceName).AppendLine();
		builder.AppendLine("{");
		builder.AppendLine("\tprivate readonly global::Nerdbank.JsonRpc.IJsonRpcClient jsonRpc;");
		builder.AppendLine("\tprivate readonly bool useNamedArguments;");

		if (methodNameTransformField is string transformField)
		{
			builder.Append("\tprivate readonly global::System.Func<string, string> ").Append(transformField).AppendLine(";");
		}

		for (int i = 0; i < info.Methods.Length; i++)
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

		builder.Append("\tinternal ").Append(info.ProxyName).Append("(global::Nerdbank.JsonRpc.IJsonRpcClient jsonRpc, global::Nerdbank.JsonRpc.JsonRpcProxyOptions options)").AppendLine();
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tthis.jsonRpc = jsonRpc;");
		builder.AppendLine("\t\tthis.useNamedArguments = options.UseNamedArguments;");
		if (needsMethodNameTransform)
		{
			builder.Append("\t\tthis.").Append(methodNameTransformField).AppendLine(" = options.MethodNameTransform;");
		}

		if (shapeFields.Length > 0)
		{
			builder.Append("\t\tglobal::PolyType.ITypeShapeProvider typeShapeProvider = global::PolyType.Abstractions.TypeShapeResolver.")
				.Append(info.HasStaticTypeShapeResolver ? "Resolve" : "ResolveDynamicOrThrow")
				.Append('<')
				.Append(info.InterfaceName)
				.AppendLine(">().Provider;");
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

		builder.AppendLine("\t}");

		for (int i = 0; i < info.Methods.Length; i++)
		{
			builder.AppendLine();
			builder.Append(RenderMethod(info.Methods[i], shapeFields, transformedRpcNameFields[i], methodNameTransformField, info.IsMarshalable));
		}

		builder.AppendLine("}");
		return builder.ToString();
	}

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

	private static string RenderMethod(MethodInfo method, ImmutableArray<ShapeFieldInfo> shapeFields, string? transformedRpcNameField, string? methodNameTransformField, bool isMarshalable)
	{
		StringBuilder builder = new();
		string parameters = string.Join(", ", method.Symbol.Parameters.Select(static p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {EscapeIdentifier(p.Name)}"));
		string cancellationToken = method.HasCancellationToken ? EscapeIdentifier(method.Symbol.Parameters[^1].Name) : "global::System.Threading.CancellationToken.None";

		builder.Append("\tpublic ").Append(method.Symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append(' ').Append(EscapeIdentifier(method.Symbol.Name)).Append('(').Append(parameters).AppendLine(")");
		builder.AppendLine("\t{");

		if (method.Kind is not ProxyMethodKind.Unsupported)
		{
			builder.Append("\t\tusing global::Nerdbank.JsonRpc.JsonRpcArgumentsBuilder argumentsBuilder = this.jsonRpc.CreateArguments(").Append("this.useNamedArguments, ").Append(method.PayloadParameters.Length).Append(", ").Append(cancellationToken).AppendLine(");");
			foreach (IParameterSymbol parameter in method.PayloadParameters)
			{
				builder.Append("\t\targumentsBuilder.Add(");
				AppendQuoted(builder, parameter.Name);
				builder.Append(", ").Append(EscapeIdentifier(parameter.Name)).Append(", this.")
					.Append(GetShapeFieldName(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), shapeFields))
					.AppendLine(");");
			}

			builder.AppendLine("\t\tglobal::Nerdbank.JsonRpc.JsonRpcValue arguments = argumentsBuilder.Build();");

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

		builder.AppendLine("\t}");

		return builder.ToString();
	}

	private static bool IsDisposeMethod(MethodInfo method)
		=> method.Symbol.Name == "Dispose" && method.Symbol.ContainingType.ToDisplayString() == "System.IDisposable" && method.Symbol.Parameters.Length == 0;

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
	private static StringBuilder AppendRpcMethodName(StringBuilder builder, MethodInfo method, string? transformedRpcNameField, string? methodNameTransformField)
	{
		if (method.ExplicitRpcName is string explicitRpcName)
		{
			return AppendQuoted(builder, explicitRpcName);
		}

		builder.Append("(this.").Append(transformedRpcNameField).Append(" ??= this.").Append(methodNameTransformField).Append("(");
		AppendQuoted(builder, method.Symbol.Name);
		return builder.Append("))");
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

	private static StringBuilder AppendQuoted(StringBuilder builder, string value)
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

	private sealed record InterfaceInfo(INamedTypeSymbol Symbol, ImmutableArray<MethodInfo> Methods, bool HasStaticTypeShapeResolver, bool IsMarshalable, bool HasGenerateProxyAttribute, ImmutableArray<Diagnostic> Diagnostics)
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
		string? ExplicitRpcName);

	private sealed record ShapeFieldInfo(string TypeName, string FieldName);
}
