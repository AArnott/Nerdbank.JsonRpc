// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Testing;
using Nerdbank.JsonRpc;
using Nerdbank.JsonRpc.SourceGeneration;
using Nerdbank.MessagePack;
using Nerdbank.Streams;
using PolyType;
using TUnit.Core;
using Xunit;

public class ClientProxyGeneratorTests
{
	[Test]
	public async Task UnsupportedMethodSignaturesProduceDiagnosticsAndNoProxy()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[GenerateJsonRpcProxy]
			internal partial interface IUnsupportedProxySignatures
			{
				ValueTask<T> {|#1:GenericAsync|}<T>(T value, CancellationToken cancellationToken);

				ValueTask<int> {|#2:OptionalAsync|}(int value = 1);

				ValueTask<int> {|#3:ParamsAsync|}(params int[] values);

				ValueTask<int> {|#4:RefAsync|}(ref int value);

				ValueTask<int> {|#5:CancellationTokenNotLastAsync|}(CancellationToken cancellationToken, int value);

				string {|#6:UnsupportedReturnAsync|}(int value);
			}
			""";

		DiagnosticResult genericMethod = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(1)
			.WithArguments("IUnsupportedProxySignatures.GenericAsync<T>(T, System.Threading.CancellationToken)", "generic methods are not supported yet");
		DiagnosticResult optionalParameter = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(2)
			.WithArguments("IUnsupportedProxySignatures.OptionalAsync(int)", "optional parameters with default values are not supported yet");
		DiagnosticResult paramsParameter = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(3)
			.WithArguments("IUnsupportedProxySignatures.ParamsAsync(params int[])", "params parameters are not supported yet");
		DiagnosticResult refParameter = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(4)
			.WithArguments("IUnsupportedProxySignatures.RefAsync(ref int)", "ref, out, and in parameters are not supported yet");
		DiagnosticResult cancellationTokenNotLast = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(5)
			.WithArguments("IUnsupportedProxySignatures.CancellationTokenNotLastAsync(System.Threading.CancellationToken, int)", "CancellationToken parameters must appear last");
		DiagnosticResult unsupportedReturn = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(6)
			.WithArguments("IUnsupportedProxySignatures.UnsupportedReturnAsync(int)", "return type 'string' is not supported yet");

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(Source, genericMethod, optionalParameter, paramsParameter, refParameter, cancellationTokenNotLast, unsupportedReturn);
	}

	[Test]
	public async Task UnsupportedInterfacesProduceDiagnosticsAndNoProxy()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[GenerateJsonRpcProxy]
			internal partial interface {|#0:IGenericProxy|}<T>
			{
				ValueTask<int> GetAsync(T value, CancellationToken cancellationToken);
			}

			internal static class Container
			{
				[GenerateJsonRpcProxy]
				internal partial interface {|#1:INestedProxy|}
				{
					ValueTask<int> GetAsync(int value, CancellationToken cancellationToken);
				}
			}
			""";

		DiagnosticResult genericInterface = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(0)
			.WithArguments("IGenericProxy<T>", "generic interfaces are not supported yet");
		DiagnosticResult nestedInterface = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(1)
			.WithArguments("Container.INestedProxy", "nested interfaces are not supported yet");

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(Source, genericInterface, nestedInterface);
	}

	[Test]
	public async Task DuplicateOptionalInterfaceIdsProduceDiagnostic()
	{
		const string Source = /* lang=c#-test */ """
			using System;
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			internal interface IFirstOptional
			{
				Task FirstAsync(CancellationToken cancellationToken);
			}

			internal interface ISecondOptional
			{
				Task SecondAsync(CancellationToken cancellationToken);
			}

			[RpcMarshalable]
			[RpcMarshalableOptionalInterface(1, typeof(IFirstOptional))]
			[RpcMarshalableOptionalInterface(1, typeof(ISecondOptional))]
			internal partial interface {|#0:IMarshalable|} : IDisposable
			{
				Task InvokeAsync(CancellationToken cancellationToken);
			}
			""";

		DiagnosticResult duplicateId = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(0)
			.WithArguments("IMarshalable", "optional interface ID 1 is declared more than once");
		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(Source, duplicateId);
	}

	[Test]
	public async Task OverlappingOptionalInterfaceMembersAreDeduplicated()
	{
		const string Source = /* lang=c#-test */ """
			using System;
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[RpcMarshalableOptionalInterface(1, typeof(IOptional))]
			internal interface IOptional : IDisposable
			{
				Task InvokeAsync(CancellationToken cancellationToken);
			}

			[RpcMarshalable]
			[RpcMarshalableOptionalInterface(1, typeof(IOptional))]
			internal partial interface IMarshalable : IDisposable
			{
				Task InvokeAsync(CancellationToken cancellationToken);
			}
			""";

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(Source);
	}

	[Test]
	public async Task NonPartialInterfaceProducesDiagnosticAndNoProxy()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[GenerateJsonRpcProxy]
			internal interface {|#0:INonPartialProxy|}
			{
				ValueTask<int> GetAsync(int value, CancellationToken cancellationToken);
			}
			""";

		DiagnosticResult nonPartialInterface = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(0)
			.WithArguments("INonPartialProxy", "annotated interfaces must be partial");

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(Source, nonPartialInterface);
	}

	[Test]
	public async Task MatchingProxyNamesInDifferentNamespacesDoNotCollide()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;
			using PolyType;

			namespace First
			{
				[GenerateJsonRpcProxy]
				[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
				internal partial interface ICalculator
				{
					ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
				}
			}

			namespace Second
			{
				[GenerateJsonRpcProxy]
				[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
				internal partial interface ICalculator
				{
					ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
				}
			}
			""";

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(Source);
	}

	[Test]
	public async Task NotificationsCannotAcceptRpcMarshalableInterfaceParameters()
	{
		const string Source = /* lang=c#-test */ """
			using System;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;
			using PolyType;

			[RpcMarshalable]
			[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
			internal partial interface IRemoteObject : IDisposable
			{
				Task CallAsync();
			}

			[GenerateJsonRpcProxy]
			[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
			internal partial interface INotificationContract
			{
				void {|#0:Notify|}(IRemoteObject remoteObject);
				void {|#1:NotifyMany|}(IRemoteObject[] remoteObjects);
				void {|#2:NotifyNested|}(System.Collections.Generic.IReadOnlyList<IRemoteObject> remoteObjects);
			}
			""";

		DiagnosticResult directParameter = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(0)
			.WithArguments("INotificationContract.Notify(IRemoteObject)", "notification methods cannot accept RPC-marshalable interface parameters");
		DiagnosticResult arrayParameter = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(1)
			.WithArguments("INotificationContract.NotifyMany(IRemoteObject[])", "notification methods cannot accept RPC-marshalable interface parameters");
		DiagnosticResult nestedParameter = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(2)
			.WithArguments("INotificationContract.NotifyNested(System.Collections.Generic.IReadOnlyList<IRemoteObject>)", "notification methods cannot accept RPC-marshalable interface parameters");

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(Source, directParameter, arrayParameter, nestedParameter);
	}

	[Test]
	public async Task RpcMarshalableInterfaceGeneratesProxyWithoutGenerateProxyAttribute()
	{
		const string Source = /* lang=c#-test */ """
			using System;
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;
			using PolyType;

			[RpcMarshalable]
			[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
			internal partial interface IRemoteObject : IDisposable
			{
				Task<int> CallAsync(CancellationToken cancellationToken);
			}
			""";

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(Source);
	}

	[Test]
	public async Task CallScopedRpcMarshalableInterfaceDoesNotRequireDisposable()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;
			using PolyType;

			[RpcMarshalable(CallScopedLifetime = true)]
			[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
			internal partial interface ITemporaryObject
			{
				Task<int> GetValueAsync(CancellationToken cancellationToken);
			}
			""";

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(Source);
	}

	[Test]
	public async Task RpcMarshalableInterfacesMustExtendDisposableAndCannotDeclarePropertiesOrEvents()
	{
		const string Source = /* lang=c#-test */ """
			using System;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[RpcMarshalable]
			internal partial interface {|#0:INotDisposable|}
			{
				Task CallAsync();
			}

			[RpcMarshalable]
			internal partial interface {|#1:IHasProperty|} : IDisposable
			{
				int Count { get; }
			}

			[RpcMarshalable]
			internal partial interface {|#2:IHasEvent|} : IDisposable
			{
				event EventHandler Changed;
			}

			[RpcMarshalable]
			internal partial interface IHasNotification : IDisposable
			{
				void {|#3:Notify|}();
			}
			""";

		DiagnosticResult noDisposable = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(0)
			.WithArguments("INotDisposable", "marshalable interfaces must extend IDisposable");
		DiagnosticResult hasProperty = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(1)
			.WithArguments("IHasProperty", "marshalable interfaces cannot declare properties or events");
		DiagnosticResult hasEvent = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(2)
			.WithArguments("IHasEvent", "marshalable interfaces cannot declare properties or events");
		DiagnosticResult hasNotification = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(3)
			.WithArguments("IHasNotification.Notify()", "marshalable interface methods must return Task or ValueTask");

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(Source, noDisposable, hasProperty, hasEvent, hasNotification);
	}

	[Test]
	public async Task KeywordIdentifiersAreEscapedInGeneratedProxy()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;
			using PolyType;

			[GenerateJsonRpcProxy]
			[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
			internal partial interface IKeywordParameters
			{
				ValueTask<int> EchoKeywordAsync(int @event, CancellationToken cancellationToken);
			}
			""";

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(Source);
	}

	[Test]
	public async Task ProxyFactoryNameAvoidsCollisionWithInterfaceMembers()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[GenerateJsonRpcProxy]
			internal partial interface ICollidingNames
			{
				Task NerdbankJsonRpc_ProxyFactoryAttribute();

				Task NerdbankJsonRpc_ProxyFactoryAttribute_();
			}
			""";

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(Source);
	}

	[Test]
	public async Task UnchangedOutputsAreCachedAndOnlyChangedProxyIsRegenerated()
	{
		const string Contracts = /* lang=c#-test */ """
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[GenerateJsonRpcProxy]
			internal partial interface IFirst { }

			[GenerateJsonRpcProxy]
			internal partial interface ISecond { }
			""";
		const string ChangedContracts = /* lang=c#-test */ """
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[GenerateJsonRpcProxy]
			internal partial interface IFirst
			{
				Task<int> GetAsync();
			}

			[GenerateJsonRpcProxy]
			internal partial interface ISecond { }
			""";

		CSharpParseOptions parseOptions = new(LanguageVersion.Preview);
		SyntaxTree contractsTree = CSharpSyntaxTree.ParseText(Contracts, parseOptions, "Contracts.cs");
		ImmutableArray<MetadataReference> references = await ReferenceAssemblies.Net.Net90.ResolveAsync(LanguageNames.CSharp, CancellationToken.None);
		references = references
			.Add(MetadataReference.CreateFromFile(typeof(GenerateJsonRpcProxyAttribute).Assembly.Location))
			.Add(MetadataReference.CreateFromFile(typeof(MessagePackSerializer).Assembly.Location))
			.Add(MetadataReference.CreateFromFile(typeof(Sequence<>).Assembly.Location))
			.Add(MetadataReference.CreateFromFile(typeof(ITypeShape<>).Assembly.Location));
		CSharpCompilation compilation = CSharpCompilation.Create(
			"IncrementalGeneratorTests",
			[contractsTree],
			references,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
		GeneratorDriver driver = CSharpGeneratorDriver.Create(
			[new ClientProxyGenerator().AsSourceGenerator()],
			parseOptions: parseOptions,
			driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
		driver = driver.RunGenerators(compilation);
		driver = driver.RunGenerators(compilation);
		GeneratorRunResult identicalRun = driver.GetRunResult().Results.Single();
		if (!AreOutputsStable(identicalRun))
		{
			throw new InvalidOperationException("Running the generator again with identical inputs should reuse every output.");
		}

		SyntaxTree unrelatedTree = CSharpSyntaxTree.ParseText("internal class Unrelated { }", parseOptions, "Unrelated.cs");
		CSharpCompilation compilationWithUnrelatedSource = compilation.AddSyntaxTrees(unrelatedTree);
		driver = driver.RunGenerators(compilationWithUnrelatedSource);
		GeneratorRunResult unchangedRun = driver.GetRunResult().Results.Single();
		if (!AreOutputsStable(unchangedRun))
		{
			throw new InvalidOperationException("Adding an unrelated source file should leave every generated proxy unchanged or cached.");
		}

		if (unchangedRun.GeneratedSources.Any(static source => source.SourceText.ToString().Contains('\r')))
		{
			throw new InvalidOperationException("Generated source must use deterministic LF newlines.");
		}

		SyntaxTree changedTree = CSharpSyntaxTree.ParseText(ChangedContracts, parseOptions, "Contracts.cs");
		CSharpCompilation compilationWithChangedProxy = compilationWithUnrelatedSource.ReplaceSyntaxTree(contractsTree, changedTree);
		driver = driver.RunGenerators(compilationWithChangedProxy);
		GeneratorRunResult changedRun = driver.GetRunResult().Results.Single();
		IncrementalStepRunReason[] outputReasons = changedRun.TrackedSteps["GenerateJsonRpcProxyOutputs"]
			.SelectMany(static step => step.Outputs)
			.Select(static output => output.Reason)
			.ToArray();
		bool hasUnchangedProxy = outputReasons.Contains(IncrementalStepRunReason.Cached) || outputReasons.Contains(IncrementalStepRunReason.Unchanged);
		if (!outputReasons.Contains(IncrementalStepRunReason.Modified) || !hasUnchangedProxy)
		{
			throw new InvalidOperationException($"Changing one contract should regenerate only its proxy. Reasons: {string.Join(", ", outputReasons)}");
		}

		static bool AreOutputsStable(GeneratorRunResult run)
			=> run.TrackedSteps["GenerateJsonRpcProxyOutputs"]
				.SelectMany(static step => step.Outputs)
				.All(static output => output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged);
	}
}
