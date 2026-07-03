// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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

	private enum ProxyArgumentMatch
	{
		Named,
		Positional,
	}

	/// <inheritdoc />
	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		IncrementalValuesProvider<InterfaceInfo> proxyInterfaces = context.SyntaxProvider.ForAttributeWithMetadataName(
			"Nerdbank.JsonRpc.GenerateJsonRpcProxyAttribute",
			static (node, _) => node is InterfaceDeclarationSyntax,
			static (ctx, _) => CreateInterfaceInfo((INamedTypeSymbol)ctx.TargetSymbol, ctx.SemanticModel.Compilation));

		context.RegisterSourceOutput(proxyInterfaces, static (ctx, info) =>
		{
			foreach (Diagnostic diagnostic in info.Diagnostics)
			{
				ctx.ReportDiagnostic(diagnostic);
			}

			if (info.Diagnostics.Length > 0)
			{
				return;
			}

			ctx.AddSource(info.HintName, SourceText.From(RenderProxy(info), Encoding.UTF8));
		});
	}

	private static InterfaceInfo CreateInterfaceInfo(INamedTypeSymbol interfaceSymbol, Compilation compilation)
	{
		ProxyArgumentMatch defaultArgumentMatch = GetArgumentMatch(interfaceSymbol.GetAttributes(), ProxyArgumentMatch.Positional);
		ImmutableArray<MethodInfo>.Builder methods = ImmutableArray.CreateBuilder<MethodInfo>();
		ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
		if (GetUnsupportedInterfaceReason(interfaceSymbol) is string interfaceReason)
		{
			diagnostics.Add(Diagnostic.Create(UnsupportedInterface, interfaceSymbol.Locations.FirstOrDefault(), interfaceSymbol.ToDisplayString(), interfaceReason));
			return new InterfaceInfo(interfaceSymbol, methods.ToImmutable(), HasStaticTypeShapeResolver(compilation), diagnostics.ToImmutable());
		}

		foreach (IMethodSymbol method in interfaceSymbol.GetMembers().OfType<IMethodSymbol>().Where(static method => method.MethodKind == MethodKind.Ordinary))
		{
			if (GetUnsupportedSignatureReason(method) is string reason)
			{
				diagnostics.Add(Diagnostic.Create(UnsupportedMethodSignature, method.Locations.FirstOrDefault(), method.ToDisplayString(), reason));
				continue;
			}

			methods.Add(CreateMethodInfo(method, defaultArgumentMatch));
		}

		return new InterfaceInfo(interfaceSymbol, methods.ToImmutable(), HasStaticTypeShapeResolver(compilation), diagnostics.ToImmutable());
	}

	private static string? GetUnsupportedInterfaceReason(INamedTypeSymbol interfaceSymbol)
	{
		if (interfaceSymbol.TypeParameters.Length > 0)
		{
			return "generic interfaces are not supported yet";
		}

		if (interfaceSymbol.ContainingType is not null)
		{
			return "nested interfaces are not supported yet";
		}

		return null;
	}

	private static string? GetUnsupportedSignatureReason(IMethodSymbol method)
	{
		if (method.IsGenericMethod)
		{
			return "generic methods are not supported yet";
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

	private static bool IsCancellationToken(ITypeSymbol type)
		=> type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Threading.CancellationToken";

	private static bool HasStaticTypeShapeResolver(Compilation compilation)
	{
		INamedTypeSymbol? resolver = compilation.GetTypeByMetadataName("PolyType.Abstractions.TypeShapeResolver");
		return resolver?.GetMembers("Resolve")
			.OfType<IMethodSymbol>()
			.Any(static method => method.IsGenericMethod && method.TypeParameters.Length == 1 && method.ContainingAssembly.Name == "PolyType") is true;
	}

	private static MethodInfo CreateMethodInfo(IMethodSymbol method, ProxyArgumentMatch argumentMatch)
	{
		bool hasCancellationToken = method.Parameters.LastOrDefault() is { } lastParameter && IsCancellationToken(lastParameter.Type);
		ImmutableArray<IParameterSymbol> payloadParameters = hasCancellationToken
			? method.Parameters.Take(method.Parameters.Length - 1).ToImmutableArray()
			: method.Parameters.ToImmutableArray();

		ProxyMethodKind methodKind = GetMethodKind(method.ReturnType, out string? resultTypeName);

		return new MethodInfo(method, payloadParameters, hasCancellationToken, methodKind, argumentMatch, resultTypeName);
	}

	private static ProxyArgumentMatch GetArgumentMatch(ImmutableArray<AttributeData> attributes, ProxyArgumentMatch defaultValue)
	{
		foreach (AttributeData attribute in attributes)
		{
			if (attribute.AttributeClass?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::Nerdbank.JsonRpc.GenerateJsonRpcProxyAttribute")
			{
				foreach (KeyValuePair<string, TypedConstant> namedArgument in attribute.NamedArguments)
				{
					if (namedArgument.Key == "UseNamedArguments" && namedArgument.Value.Value is bool useNamedArguments)
					{
						return useNamedArguments ? ProxyArgumentMatch.Named : ProxyArgumentMatch.Positional;
					}
				}

				return defaultValue;
			}
		}

		return defaultValue;
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
		builder.AppendLine("\tprivate readonly global::Nerdbank.JsonRpc.JsonRpc jsonRpc;");
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

		builder.Append("\tinternal ").Append(info.ProxyName).Append("(global::Nerdbank.JsonRpc.JsonRpc jsonRpc)").AppendLine();
		builder.AppendLine("\t{");
		builder.AppendLine("\t\tthis.jsonRpc = jsonRpc;");
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

		foreach (MethodInfo method in info.Methods)
		{
			builder.AppendLine();
			builder.Append(RenderMethod(method, shapeFields));
		}

		builder.AppendLine("}");
		return builder.ToString();
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

	private static string RenderMethod(MethodInfo method, ImmutableArray<ShapeFieldInfo> shapeFields)
	{
		StringBuilder builder = new();
		string parameters = string.Join(", ", method.Symbol.Parameters.Select(static p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {EscapeIdentifier(p.Name)}"));
		string cancellationToken = method.HasCancellationToken ? EscapeIdentifier(method.Symbol.Parameters[^1].Name) : "global::System.Threading.CancellationToken.None";

		builder.Append("\tpublic ").Append(method.Symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append(' ').Append(EscapeIdentifier(method.Symbol.Name)).Append('(').Append(parameters).AppendLine(")");
		builder.AppendLine("\t{");

		if (method.Kind is not ProxyMethodKind.Unsupported)
		{
			builder.AppendLine("\t\tusing global::Nerdbank.Streams.Sequence<byte> argumentsBuffer = new();");
			builder.AppendLine("\t\tglobal::Nerdbank.MessagePack.MessagePackWriter argumentsWriter = new(argumentsBuffer);");
			builder.Append("\t\targumentsWriter.Write").Append(method.ArgumentMatch == ProxyArgumentMatch.Positional ? "Array" : "Map").Append("Header(").Append(method.PayloadParameters.Length).AppendLine(");");

			foreach (IParameterSymbol parameter in method.PayloadParameters)
			{
				if (method.ArgumentMatch == ProxyArgumentMatch.Named)
				{
					builder.Append("\t\targumentsWriter.Write(");
					AppendQuoted(builder, parameter.Name).AppendLine(");");
				}

				builder.Append("\t\tthis.jsonRpc.Serializer.Serialize(ref argumentsWriter, ").Append(EscapeIdentifier(parameter.Name)).Append(", ");
				builder.Append("this.").Append(GetShapeFieldName(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), shapeFields)).Append(", ");
				builder.Append(cancellationToken).AppendLine(");");
			}

			builder.AppendLine("\t\targumentsWriter.Flush();");
			builder.AppendLine("\t\tglobal::System.Buffers.ReadOnlySequence<byte> writtenSequence = argumentsBuffer.AsReadOnlySequence;");
			builder.AppendLine("\t\tbyte[] serializedArguments = new byte[checked((int)writtenSequence.Length)];");
			builder.AppendLine("\t\tint copiedLength = 0;");
			builder.AppendLine("\t\tforeach (global::System.ReadOnlyMemory<byte> segment in writtenSequence)");
			builder.AppendLine("\t\t{");
			builder.AppendLine("\t\t\tsegment.Span.CopyTo(serializedArguments.AsSpan(copiedLength));");
			builder.AppendLine("\t\t\tcopiedLength += segment.Length;");
			builder.AppendLine("\t\t}");
			builder.AppendLine("\t\tglobal::Nerdbank.MessagePack.RawMessagePack arguments = (global::Nerdbank.MessagePack.RawMessagePack)serializedArguments;");

			switch (method.Kind)
			{
				case ProxyMethodKind.ValueTaskOfT:
					builder.Append("\t\treturn this.jsonRpc.RequestAsync(");
					AppendQuoted(builder, method.Symbol.Name).Append(", arguments, ");
					builder.Append("this.").Append(GetShapeFieldName(method.ResultTypeName!, shapeFields)).Append(", ");
					builder.Append(cancellationToken).AppendLine(");");
					break;
				case ProxyMethodKind.TaskOfT:
					builder.Append("\t\treturn this.jsonRpc.RequestAsync(");
					AppendQuoted(builder, method.Symbol.Name).Append(", arguments, ");
					builder.Append("this.").Append(GetShapeFieldName(method.ResultTypeName!, shapeFields)).Append(", ");
					builder.Append(cancellationToken).AppendLine(").AsTask();");
					break;
				case ProxyMethodKind.ValueTask:
					builder.Append("\t\treturn this.jsonRpc.RequestAsync(");
					AppendQuoted(builder, method.Symbol.Name).Append(", arguments, ");
					builder.Append(cancellationToken).AppendLine(");");
					break;
				case ProxyMethodKind.Task:
					builder.Append("\t\treturn this.jsonRpc.RequestAsync(");
					AppendQuoted(builder, method.Symbol.Name).Append(", arguments, ");
					builder.Append(cancellationToken).AppendLine(").AsTask();");
					break;
				case ProxyMethodKind.Notification:
					builder.Append("\t\tthis.jsonRpc.Notify(");
					AppendQuoted(builder, method.Symbol.Name).Append(", arguments, ");
					builder.Append(cancellationToken).AppendLine(");");
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
		=> builder.Append('"').Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');

	private static string EscapeIdentifier(string identifier)
		=> SyntaxFacts.GetKeywordKind(identifier) == SyntaxKind.None && SyntaxFacts.GetContextualKeywordKind(identifier) == SyntaxKind.None ? identifier : "@" + identifier;

	private static string GetAccessibility(Accessibility accessibility)
		=> accessibility switch
		{
			Accessibility.Public => "public",
			_ => "internal",
		};

	private sealed record InterfaceInfo(INamedTypeSymbol Symbol, ImmutableArray<MethodInfo> Methods, bool HasStaticTypeShapeResolver, ImmutableArray<Diagnostic> Diagnostics)
	{
		internal string HintName => this.ProxyName + ".g.cs";

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
		ProxyArgumentMatch ArgumentMatch,
		string? ResultTypeName);

	private sealed record ShapeFieldInfo(string TypeName, string FieldName);
}
