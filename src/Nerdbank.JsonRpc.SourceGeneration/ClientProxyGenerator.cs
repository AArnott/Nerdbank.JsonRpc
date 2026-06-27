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

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
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
		ProxyArgumentMatch defaultArgumentMatch = GetArgumentMatch(interfaceSymbol.GetAttributes(), ProxyArgumentMatch.Positional);
		ImmutableArray<MethodInfo> methods = interfaceSymbol
			.GetMembers()
			.OfType<IMethodSymbol>()
			.Where(static method => method.MethodKind == MethodKind.Ordinary)
			.Select(method => CreateMethodInfo(method, defaultArgumentMatch))
			.ToImmutableArray();

		return new InterfaceInfo(interfaceSymbol, methods);
	}

	private static MethodInfo CreateMethodInfo(IMethodSymbol method, ProxyArgumentMatch argumentMatch)
	{
		bool hasCancellationToken = method.Parameters.LastOrDefault()?.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::System.Threading.CancellationToken";
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

		if (method.Kind is not ProxyMethodKind.Unsupported)
		{
			builder.AppendLine("\t\tglobal::System.Buffers.ArrayBufferWriter<byte> argumentsBuffer = new();");
			builder.AppendLine("\t\tglobal::Nerdbank.MessagePack.MessagePackWriter argumentsWriter = new(argumentsBuffer);");
			builder.Append("\t\targumentsWriter.Write").Append(method.ArgumentMatch == ProxyArgumentMatch.Positional ? "Array" : "Map").Append("Header(").Append(method.PayloadParameters.Length).AppendLine(");");

			foreach (IParameterSymbol parameter in method.PayloadParameters)
			{
				if (method.ArgumentMatch == ProxyArgumentMatch.Named)
				{
					builder.Append("\t\targumentsWriter.Write(");
					AppendQuoted(builder, parameter.Name).AppendLine(");");
				}

				builder.Append("\t\tthis.jsonRpc.Serializer.Serialize(ref argumentsWriter, ").Append(parameter.Name).Append(", ");
				builder.Append("global::PolyType.TypeShapeProviderExtensions.GetTypeShapeOrThrow<");
				builder.Append(parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)).Append(">(this.typeShapeProvider), ");
				builder.Append(cancellationToken).AppendLine(");");
			}

			builder.AppendLine("\t\targumentsWriter.Flush();");
			builder.AppendLine("\t\tglobal::Nerdbank.MessagePack.RawMessagePack arguments = (global::Nerdbank.MessagePack.RawMessagePack)argumentsBuffer.WrittenMemory;");

			switch (method.Kind)
			{
				case ProxyMethodKind.ValueTaskOfT:
					builder.Append("\t\treturn this.jsonRpc.RequestAsync(");
					AppendQuoted(builder, method.Symbol.Name).Append(", arguments, ");
					builder.Append("global::PolyType.TypeShapeProviderExtensions.GetTypeShapeOrThrow<").Append(method.ResultTypeName).Append(">(this.typeShapeProvider), ");
					builder.Append(cancellationToken).AppendLine(");");
					break;
				case ProxyMethodKind.TaskOfT:
					builder.Append("\t\treturn this.jsonRpc.RequestAsync(");
					AppendQuoted(builder, method.Symbol.Name).Append(", arguments, ");
					builder.Append("global::PolyType.TypeShapeProviderExtensions.GetTypeShapeOrThrow<").Append(method.ResultTypeName).Append(">(this.typeShapeProvider), ");
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
		ProxyMethodKind Kind,
		ProxyArgumentMatch ArgumentMatch,
		string? ResultTypeName);
}
