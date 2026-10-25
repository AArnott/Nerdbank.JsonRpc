// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Nerdbank.JsonRpc.SourceGeneration;

/// <summary>Checks ownership of acquired brokered-service proxies and cached rentals.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BrokeredServiceAnalyzer : DiagnosticAnalyzer
{
	private static readonly DiagnosticDescriptor DisposeProxy = new(
		"NBJSONRPC004",
		"Dispose of brokered-service proxies",
		"Dispose of the acquired proxy, or transfer it to a caller or a disposable owner that disposes its previous value before replacement",
		"Usage",
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		helpLinkUri: "https://aarnott.github.io/Nerdbank.JsonRpc/analyzers/NBJSONRPC004.html");

	private static readonly DiagnosticDescriptor DoNotStoreRental = new(
		"NBJSONRPC005",
		"Do not store service rentals in fields or properties",
		"Keep service rentals local to the acquiring method; use IServiceBroker for longer-lived proxies",
		"Usage",
		DiagnosticSeverity.Warning,
		isEnabledByDefault: true,
		helpLinkUri: "https://aarnott.github.io/Nerdbank.JsonRpc/analyzers/NBJSONRPC005.html");

	/// <inheritdoc/>
	public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [DisposeProxy, DoNotStoreRental];

	/// <inheritdoc/>
	public override void Initialize(AnalysisContext context)
	{
		if (context is null)
		{
			throw new ArgumentNullException(nameof(context));
		}

		context.EnableConcurrentExecution();
		context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
		context.RegisterCompilationStartAction(static start =>
		{
			INamedTypeSymbol? broker = start.Compilation.GetTypeByMetadataName(KnownApis.IServiceBroker);
			INamedTypeSymbol? client = start.Compilation.GetTypeByMetadataName(KnownApis.ServiceBrokerClient);
			if (broker is not null)
			{
				IMethodSymbol? acquisition = broker.GetMembers(KnownApis.GetProxyAsync).OfType<IMethodSymbol>().FirstOrDefault();
				if (acquisition is not null)
				{
					start.RegisterOperationAction(context => AnalyzeAcquisition(context, acquisition), OperationKind.Invocation);
				}
			}

			if (client is not null)
			{
				start.RegisterOperationAction(context => AnalyzeRentalStorage(context, client), OperationKind.SimpleAssignment, OperationKind.CoalesceAssignment, OperationKind.FieldInitializer, OperationKind.PropertyInitializer);
			}
		});
	}

	private static void AnalyzeAcquisition(OperationAnalysisContext context, IMethodSymbol acquisition)
	{
		IInvocationOperation invocation = (IInvocationOperation)context.Operation;
		IMethodSymbol method = invocation.TargetMethod.OriginalDefinition;
		if (method.Name != KnownApis.GetProxyAsync
			|| (!SymbolEqualityComparer.Default.Equals(method, acquisition)
				&& !SymbolEqualityComparer.Default.Equals((method.ContainingType.FindImplementationForInterfaceMember(acquisition) as IMethodSymbol)?.OriginalDefinition, method)))
		{
			return;
		}

		IOperation root = invocation;
		while (root.Parent is { } parent && parent is not IAnonymousFunctionOperation and not ILocalFunctionOperation)
		{
			root = parent;
		}

		ProxyOwnershipAnalysis analysis = new(context.Compilation, invocation, context.CancellationToken);
		if (!analysis.IsReleased(root))
		{
			context.ReportDiagnostic(Diagnostic.Create(DisposeProxy, invocation.Syntax.GetLocation()));
		}
	}

	private static void AnalyzeRentalStorage(OperationAnalysisContext context, INamedTypeSymbol client)
	{
		IOperation value;
		Location location;
		switch (context.Operation)
		{
			case IAssignmentOperation assignment when assignment.Target is IFieldReferenceOperation or IPropertyReferenceOperation:
				value = assignment.Value;
				location = assignment.Target.Syntax.GetLocation();
				break;
			case IFieldInitializerOperation initializer:
				value = initializer.Value;
				location = initializer.Syntax.GetLocation();
				break;
			case IPropertyInitializerOperation initializer:
				value = initializer.Value;
				location = initializer.Syntax.GetLocation();
				break;
			default:
				return;
		}

		while (value is IConversionOperation conversion)
		{
			value = conversion.Operand;
		}

		if (value.Type is INamedTypeSymbol type && type.OriginalDefinition.MetadataName == KnownApis.Rental
			&& SymbolEqualityComparer.Default.Equals(type.ContainingType, client))
		{
			context.ReportDiagnostic(Diagnostic.Create(DoNotStoreRental, location));
		}
	}
}
