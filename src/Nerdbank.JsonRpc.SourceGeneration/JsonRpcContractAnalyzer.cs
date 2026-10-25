// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
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

	private static readonly DiagnosticDescriptor AttachRequiresContract = new(
		"NBJSONRPC010",
		"Annotate RPC proxy interfaces",
		"Interface '{0}' passed to Attach must be annotated with JsonRpcContractAttribute or RpcMarshalableAttribute",
		"Usage",
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		helpLinkUri: "https://aarnott.github.io/Nerdbank.JsonRpc/analyzers/NBJSONRPC010.html");

	private static readonly DiagnosticDescriptor AttachRequiresSupportedInterface = new(
		"NBJSONRPC011",
		"Use supported interfaces for RPC proxies",
		"Type '{0}' passed to Attach must be a non-generic interface supported by generated proxies",
		"Usage",
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		helpLinkUri: "https://aarnott.github.io/Nerdbank.JsonRpc/analyzers/NBJSONRPC011.html");

	private static readonly DiagnosticDescriptor PreferClientProxyTypeCheck = new(
		"NBJSONRPC050",
		"Use IClientProxy for marshalable interface checks",
		"Use IClientProxy.Is(Type) or JsonRpcExtensions.As<T> instead of directly casting or type checking between marshalable interfaces",
		"Usage",
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		helpLinkUri: "https://aarnott.github.io/Nerdbank.JsonRpc/analyzers/NBJSONRPC050.html");

	/// <inheritdoc />
	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [CollidingMethodNames, UnsupportedContractMember, AttachRequiresContract, AttachRequiresSupportedInterface, PreferClientProxyTypeCheck];

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

			INamedTypeSymbol? jsonRpc = context.Compilation.GetTypeByMetadataName(KnownApis.JsonRpc);
			INamedTypeSymbol? jsonRpcBatch = context.Compilation.GetTypeByMetadataName(KnownApis.JsonRpcBatch);
			if (jsonRpc is not null || jsonRpcBatch is not null)
			{
				context.RegisterOperationAction(context => AnalyzeAttach(context, jsonRpc, jsonRpcBatch, contractAttribute, marshalableAttribute), OperationKind.Invocation);
			}

			context.RegisterOperationAction(context => AnalyzeMarshalableInterfaceConversion(context, marshalableAttribute), OperationKind.Conversion);
			context.RegisterOperationAction(context => AnalyzeMarshalableInterfaceTypeCheck(context, marshalableAttribute), OperationKind.IsType);
			context.RegisterOperationAction(context => AnalyzeMarshalableInterfacePattern(context, marshalableAttribute), OperationKind.IsPattern);
			context.RegisterOperationAction(context => AnalyzeMarshalableInterfaceSwitch(context, marshalableAttribute), OperationKind.Switch);
			context.RegisterOperationAction(context => AnalyzeMarshalableInterfaceSwitchExpression(context, marshalableAttribute), OperationKind.SwitchExpression);
		});
	}

	private static void AnalyzeAttach(OperationAnalysisContext context, INamedTypeSymbol? jsonRpc, INamedTypeSymbol? jsonRpcBatch, INamedTypeSymbol? contractAttribute, INamedTypeSymbol? marshalableAttribute)
	{
		IInvocationOperation invocation = (IInvocationOperation)context.Operation;
		IMethodSymbol method = invocation.TargetMethod;
		if (method.Name != KnownApis.Attach
			|| (!SymbolEqualityComparer.Default.Equals(method.ContainingType, jsonRpc)
				&& !SymbolEqualityComparer.Default.Equals(method.ContainingType, jsonRpcBatch)))
		{
			return;
		}

		ITypeSymbol? proxyType;
		if (method.IsGenericMethod && method.TypeArguments.Length == 1)
		{
			proxyType = method.TypeArguments[0];
		}
		else
		{
			proxyType = invocation.Arguments.FirstOrDefault(static argument => argument.Parameter?.Ordinal == 0) is { Value: ITypeOfOperation typeOf } ? typeOf.TypeOperand : null;
		}

		if (proxyType is null)
		{
			return;
		}

		if (proxyType.TypeKind == TypeKind.TypeParameter)
		{
			return;
		}

		if (proxyType is not INamedTypeSymbol { TypeKind: TypeKind.Interface } interfaceType || interfaceType.TypeParameters.Length > 0)
		{
			context.ReportDiagnostic(Diagnostic.Create(AttachRequiresSupportedInterface, invocation.Syntax.GetLocation(), proxyType.ToDisplayString()));
			return;
		}

		if (!HasAttribute(interfaceType, contractAttribute) && !HasAttribute(interfaceType, marshalableAttribute))
		{
			context.ReportDiagnostic(Diagnostic.Create(AttachRequiresContract, invocation.Syntax.GetLocation(), interfaceType.ToDisplayString()));
		}
	}

	private static void AnalyzeMarshalableInterfaceConversion(OperationAnalysisContext context, INamedTypeSymbol? marshalableAttribute)
	{
		IConversionOperation conversion = (IConversionOperation)context.Operation;
		if ((conversion.IsImplicit && !conversion.IsTryCast)
			|| !AreDistinctMarshalableInterfaces(conversion.Operand.Type, conversion.Type, marshalableAttribute))
		{
			return;
		}

		context.ReportDiagnostic(Diagnostic.Create(PreferClientProxyTypeCheck, conversion.Syntax.GetLocation()));
	}

	private static void AnalyzeMarshalableInterfaceTypeCheck(OperationAnalysisContext context, INamedTypeSymbol? marshalableAttribute)
	{
		IIsTypeOperation typeCheck = (IIsTypeOperation)context.Operation;
		if (AreDistinctMarshalableInterfaces(typeCheck.ValueOperand.Type, typeCheck.TypeOperand, marshalableAttribute))
		{
			context.ReportDiagnostic(Diagnostic.Create(PreferClientProxyTypeCheck, typeCheck.Syntax.GetLocation()));
		}
	}

	private static void AnalyzeMarshalableInterfacePattern(OperationAnalysisContext context, INamedTypeSymbol? marshalableAttribute)
	{
		IIsPatternOperation patternCheck = (IIsPatternOperation)context.Operation;
		ReportMarshalableInterfacePattern(context, patternCheck.Value.Type, patternCheck.Pattern, patternCheck.Syntax.GetLocation(), marshalableAttribute);
	}

	private static void AnalyzeMarshalableInterfaceSwitch(OperationAnalysisContext context, INamedTypeSymbol? marshalableAttribute)
	{
		ISwitchOperation switchOperation = (ISwitchOperation)context.Operation;
		foreach (IPatternCaseClauseOperation patternCase in switchOperation.Cases.SelectMany(static @case => @case.Clauses).OfType<IPatternCaseClauseOperation>())
		{
			ReportMarshalableInterfacePattern(context, switchOperation.Value.Type, patternCase.Pattern, patternCase.Pattern.Syntax.GetLocation(), marshalableAttribute);
		}
	}

	private static void AnalyzeMarshalableInterfaceSwitchExpression(OperationAnalysisContext context, INamedTypeSymbol? marshalableAttribute)
	{
		ISwitchExpressionOperation switchExpression = (ISwitchExpressionOperation)context.Operation;
		foreach (ISwitchExpressionArmOperation arm in switchExpression.Arms)
		{
			ReportMarshalableInterfacePattern(context, switchExpression.Value.Type, arm.Pattern, arm.Pattern.Syntax.GetLocation(), marshalableAttribute);
		}
	}

	private static void ReportMarshalableInterfacePattern(OperationAnalysisContext context, ITypeSymbol? source, IPatternOperation pattern, Location location, INamedTypeSymbol? marshalableAttribute)
	{
		if (GetMatchedType(pattern) is ITypeSymbol target
			&& AreDistinctMarshalableInterfaces(source, target, marshalableAttribute))
		{
			context.ReportDiagnostic(Diagnostic.Create(PreferClientProxyTypeCheck, location));
		}
	}

	private static ITypeSymbol? GetMatchedType(IPatternOperation pattern)
		=> pattern switch
		{
			IDeclarationPatternOperation { MatchedType: { } type } => type,
			ITypePatternOperation { MatchedType: { } type } => type,
			IRecursivePatternOperation { MatchedType: { } type } => type,
			INegatedPatternOperation negated => GetMatchedType(negated.Pattern),
			_ => null,
		};

	private static bool AreDistinctMarshalableInterfaces(ITypeSymbol? source, ITypeSymbol? target, INamedTypeSymbol? marshalableAttribute)
		=> source is INamedTypeSymbol { TypeKind: TypeKind.Interface } sourceInterface
			&& target is INamedTypeSymbol { TypeKind: TypeKind.Interface } targetInterface
			&& !SymbolEqualityComparer.Default.Equals(sourceInterface, targetInterface)
			&& HasAttribute(sourceInterface, marshalableAttribute)
			&& HasAttribute(targetInterface, marshalableAttribute);

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
		foreach (INamedTypeSymbol type in new[] { contract }.Concat(contract.AllInterfaces))
		{
			context.CancellationToken.ThrowIfCancellationRequested();
			if (type.MetadataName == KnownApis.IShapeableMetadataName && type.ContainingNamespace.ToDisplayString() == KnownApis.PolyTypeNamespace)
			{
				continue;
			}

			foreach (ISymbol member in type.GetMembers())
			{
				AttributeData? methodShape = member is IMethodSymbol && methodShapeAttribute is not null
					? member.GetAttributes().FirstOrDefault(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, methodShapeAttribute))
					: null;
				if ((member.DeclaredAccessibility != Accessibility.Public && methodShape is null)
					|| methodShape?.NamedArguments.Any(static argument => argument.Key == KnownApis.MethodShapeIgnore && argument.Value.Value is true) is true)
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
					|| (method.IsStatic && !method.IsAbstract)
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
