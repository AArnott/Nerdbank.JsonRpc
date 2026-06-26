// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Nerdbank.JsonRpc.SourceGeneration;

[Generator(LanguageNames.CSharp)]
public sealed class ClientProxyGenerator : IIncrementalGenerator
{
	private const string AttributeSource = """
namespace Nerdbank.JsonRpc;

[global::System.AttributeUsage(global::System.AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
internal sealed class GenerateJsonRpcProxyAttribute : global::System.Attribute
{
}
""";

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		context.RegisterPostInitializationOutput(static ctx =>
		{
			ctx.AddSource("GenerateJsonRpcProxyAttribute.g.cs", SourceText.From(AttributeSource, Encoding.UTF8));
		});

		IncrementalValuesProvider<InterfaceInfo> proxyInterfaces = context.SyntaxProvider.ForAttributeWithMetadataName(
			"Nerdbank.JsonRpc.GenerateJsonRpcProxyAttribute",
			static (node, _) => node is InterfaceDeclarationSyntax,
			static (ctx, _) => CreateInterfaceInfo((INamedTypeSymbol)ctx.TargetSymbol));

		context.RegisterSourceOutput(proxyInterfaces, static (ctx, info) =>
		{
			ctx.AddSource(info.HintName, SourceText.From(RenderProxy(info), Encoding.UTF8));
		});
	}

	private static InterfaceInfo CreateInterfaceInfo(INamedTypeSymbol interfaceSymbol)
	{
		ImmutableArray<MethodInfo> methods = interfaceSymbol
			.GetMembers()
			.OfType<IMethodSymbol>()
			.Where(static method => method.MethodKind == MethodKind.Ordinary)
			.Select(CreateMethodInfo)
			.ToImmutableArray();

		return new InterfaceInfo(interfaceSymbol, methods);
	}

	private static MethodInfo CreateMethodInfo(IMethodSymbol method)
	{
		bool hasCancellationToken = method.Parameters.LastOrDefault()?.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Threading.CancellationToken";
		ImmutableArray<IParameterSymbol> payloadParameters = hasCancellationToken
			? method.Parameters.Take(method.Parameters.Length - 1).ToImmutableArray()
			: method.Parameters.ToImmutableArray();

		bool isSupported = method.ReturnType is INamedTypeSymbol namedReturnType
			&& namedReturnType.IsGenericType
			&& namedReturnType.ConstructedFrom.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Threading.Tasks.ValueTask<TResult>";

		string? resultTypeName = isSupported
			? ((INamedTypeSymbol)method.ReturnType).TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
			: null;

		return new MethodInfo(method, payloadParameters, hasCancellationToken, isSupported, resultTypeName);
	}

	private static string RenderProxy(InterfaceInfo info)
	{
		StringBuilder builder = new();
		if (!info.Symbol.ContainingNamespace.IsGlobalNamespace)
		{
			builder.Append("namespace ").Append(info.Symbol.ContainingNamespace.ToDisplayString()).AppendLine(";");
			builder.AppendLine();
		}

		builder.Append("internal sealed class ").Append(info.ProxyName).Append("(global::Nerdbank.JsonRpc.JsonRpc jsonRpc, global::PolyType.ITypeShapeProvider typeShapeProvider) : ").Append(info.InterfaceName).AppendLine();
		builder.AppendLine("{");
		builder.AppendLine("\tprivate readonly global::Nerdbank.JsonRpc.JsonRpc jsonRpc = jsonRpc;");
		builder.AppendLine("\tprivate readonly global::PolyType.ITypeShapeProvider typeShapeProvider = typeShapeProvider;");

		foreach (MethodInfo method in info.Methods)
		{
			builder.AppendLine();
			builder.Append(RenderMethod(method));
		}

		builder.AppendLine("}");
		return builder.ToString();
	}

	private static string RenderMethod(MethodInfo method)
	{
		StringBuilder builder = new();
		string parameters = string.Join(", ", method.Symbol.Parameters.Select(static p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} {p.Name}"));
		string cancellationToken = method.HasCancellationToken ? method.Symbol.Parameters[^1].Name : "global::System.Threading.CancellationToken.None";

		builder.Append("\tpublic ").Append(method.Symbol.ReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append(' ').Append(method.Symbol.Name).Append('(').Append(parameters).AppendLine(")");
		builder.AppendLine("\t{");

		if (method.IsSupported)
		{
			builder.AppendLine("\t\tglobal::System.Buffers.ArrayBufferWriter<byte> argumentsBuffer = new();");
			builder.AppendLine("\t\tglobal::Nerdbank.MessagePack.MessagePackWriter argumentsWriter = new(argumentsBuffer);");
			builder.Append("\t\targumentsWriter.WriteMapHeader(").Append(method.PayloadParameters.Length).AppendLine(");");

			foreach (IParameterSymbol parameter in method.PayloadParameters)
			{
				builder.Append("\t\targumentsWriter.Write(");
				AppendQuoted(builder, parameter.Name).AppendLine(");");
				builder.Append("\t\tthis.jsonRpc.Serializer.Serialize(ref argumentsWriter, ").Append(parameter.Name).Append(", ");
				builder.Append("global::PolyType.TypeShapeProviderExtensions.GetTypeShapeOrThrow<");
				builder.Append(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append(">(this.typeShapeProvider), ");
				builder.Append(cancellationToken).AppendLine(");");
			}

			builder.AppendLine("\t\targumentsWriter.Flush();");
			builder.AppendLine("\t\tglobal::Nerdbank.MessagePack.RawMessagePack arguments = (global::Nerdbank.MessagePack.RawMessagePack)argumentsBuffer.WrittenMemory;");

			builder.Append("\t\treturn this.jsonRpc.RequestAsync(");
			AppendQuoted(builder, method.Symbol.Name).Append(", arguments, ");
			builder.Append("global::PolyType.TypeShapeProviderExtensions.GetTypeShapeOrThrow<").Append(method.ResultTypeName).Append(">(this.typeShapeProvider), ");
			builder.Append(cancellationToken).AppendLine(");");
		}
		else
		{
			builder.Append("\t\tthrow new global::System.NotSupportedException(");
			AppendQuoted(builder, $"Generated proxies currently support only ValueTask<T> methods. Unsupported method: {method.Symbol.Name}.");
			builder.AppendLine(");");
		}

		builder.AppendLine("\t}");

		return builder.ToString();
	}

	private static StringBuilder AppendQuoted(StringBuilder builder, string value)
		=> builder.Append('"').Append(value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');

	private sealed record InterfaceInfo(INamedTypeSymbol Symbol, ImmutableArray<MethodInfo> Methods)
	{
		internal string HintName => this.ProxyName + ".g.cs";

		internal string InterfaceName => this.Symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

		internal string ProxyName => this.Symbol.Name.StartsWith("I", System.StringComparison.Ordinal) && this.Symbol.Name.Length > 1 && char.IsUpper(this.Symbol.Name[1])
			? this.Symbol.Name.Substring(1) + "Proxy"
			: this.Symbol.Name + "Proxy";
	}

	private sealed record MethodInfo(
		IMethodSymbol Symbol,
		ImmutableArray<IParameterSymbol> PayloadParameters,
		bool HasCancellationToken,
		bool IsSupported,
		string? ResultTypeName);
}
