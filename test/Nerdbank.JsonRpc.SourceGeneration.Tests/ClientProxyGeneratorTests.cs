// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;
using Nerdbank.JsonRpc.SourceGeneration;

public class ClientProxyGeneratorTests
{
	[Test]
	public async Task ProxyBaseMemberNamesDoNotCollideWithContractMethods()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[JsonRpcContract]
			partial interface IService
			{
				ValueTask Client();
				ValueTask Context();
				ValueTask LocalContext();
				ValueTask MethodNameTransform();
				ValueTask UseNamedArguments();
				ValueTask TryDispose();
				ValueTask DisposeUnowned();
				ValueTask Is();
				ValueTask IsInterfaceEnabled();
			}
			""";

		GeneratorRunResult result = await RunGeneratorAsync(Source);
		ClassDeclarationSyntax proxy = GetProxyClasses(result).Single();
		ClassDeclarationSyntax[] factories = proxy.Members.OfType<ClassDeclarationSyntax>().ToArray();
		if (factories.Length != 1 || factories[0].Members.OfType<MethodDeclarationSyntax>().Count() != 2
			|| proxy.SyntaxTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Count() != 2)
		{
			throw new InvalidOperationException("A contract should generate one proxy and one factory with RPC and local creation overloads.");
		}
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task AsyncDisposableContractUsesProxyLifecycle(bool synchronous)
	{
		string source = /* lang=c#-test */ $$"""
			using System;
			using Nerdbank.JsonRpc;

			[JsonRpcContract]
			partial interface IService : IAsyncDisposable{{(synchronous ? ", IDisposable" : string.Empty)}}
			{
			}
			""";

		await RunGeneratorAsync(source);
	}

	[Test]
	public async Task ConcreteStaticHelpersDoNotSuppressProxyGeneration()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			interface IBase
			{
				static int Count() => 0;
			}

			[JsonRpcContract]
			partial interface IService : IBase
			{
				static Task CreateAsync() => Task.CompletedTask;
			}
			""";

		GeneratorRunResult result = await RunGeneratorAsync(Source);
		if (GetProxyClasses(result).Length != 1 || result.GeneratedSources.Any(static source => source.SourceText.ToString().Contains("CreateAsync")))
		{
			throw new InvalidOperationException("Static helpers should neither suppress proxy generation nor be emitted as proxy methods.");
		}
	}

	[Test]
	public async Task StaticAbstractMethodsStillPreventProxyGeneration()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;
			[JsonRpcContract]
			partial interface IService
			{
				static abstract Task {|#0:CreateAsync|}();
			}
			""";

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(
			Source,
			CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001").WithLocation(0)
				.WithArguments("IService.CreateAsync()", "static methods are not supported"));
	}

	[Test]
	[Arguments("")]
	[Arguments("(GenerateProxy = true)")]
	public async Task ContractGeneratesProxyByDefault(string options)
	{
		string source = /* lang=c#-test */ $$"""
			using Nerdbank.JsonRpc;
			[JsonRpcContract{{options}}]
			internal partial interface IService { }
			""";

		GeneratorRunResult result = await RunGeneratorAsync(source);
		if (GetProxyClasses(result) is not [{ Identifier.ValueText: "ServiceProxy" }])
		{
			throw new InvalidOperationException("The contract should generate exactly one ServiceProxy.");
		}
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task DisabledProxyGenerationProducesNoSourcesOrGeneratorDiagnostics(bool marshalable)
	{
		string source = /* lang=c#-test */ $$"""
			using Nerdbank.JsonRpc;
			[JsonRpcContract(GenerateProxy = false)]
			{{(marshalable ? "[RpcMarshalable]" : string.Empty)}}
			internal interface IService { }
			""";

		GeneratorRunResult result = await RunGeneratorAsync(source);
		if (!result.GeneratedSources.IsEmpty || !result.Diagnostics.IsEmpty)
		{
			throw new InvalidOperationException("Disabling proxy generation should emit no sources or generator diagnostics.");
		}
	}

	[Test]
	public async Task ContractAndMarshalableAttributesDoNotGenerateDuplicateProxies()
	{
		const string Source = /* lang=c#-test */ """
			using System;
			using Nerdbank.JsonRpc;
			[JsonRpcContract]
			[RpcMarshalable]
			internal partial interface IService : IDisposable { }
			""";

		GeneratorRunResult result = await RunGeneratorAsync(Source);
		if (GetProxyClasses(result).Length != 1)
		{
			throw new InvalidOperationException("Combining contract and marshalable attributes should generate exactly one proxy.");
		}
	}

	[Test]
	public async Task PrivateEventsAreExcludedFromValidationAndEmission()
	{
		const string Source = /* lang=c#-test */ """
			using System;
			using Nerdbank.JsonRpc;

			internal interface IBaseEvents
			{
				private event Func<int> Hidden { add { } remove { } }
			}

			[JsonRpcContract]
			internal partial interface IPrivateEvents : IBaseEvents
			{
				private event Action Hidden { add { } remove { } }
			}
			""";

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(Source, CompilerDiagnostics.Warnings);
	}

	[Test]
	public async Task EventsCompileWithInheritedGenericDelegatesAndEscapedNames()
	{
		const string Source = /* lang=c#-test */ """
			#nullable enable
			using System;
			using Nerdbank.JsonRpc;
			using PolyType;

			internal interface IBaseEvents<T>
			{
				event EventHandler<T>? Changed;
			}

			[JsonRpcContract]
			[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
			internal partial interface IEvents : IBaseEvents<int>
			{
				event Action @event;

				[EventShape(Name = "explicit.name")]
				event Action<int, string?>? Renamed;

				event Action? NerdbankJsonRpc_Event0;
			}
			""";

		string generatedPath = System.IO.Path.Combine("Nerdbank.JsonRpc.SourceGeneration", "Nerdbank.JsonRpc.SourceGeneration.ClientProxyGenerator", "EventsProxy.g.cs");
		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(
			Source,
			CompilerDiagnostics.Warnings,
			DiagnosticResult.CompilerError("CS0311").WithSpan(generatedPath, 10, 269, 10, 293)
				.WithArguments("PolyType.Abstractions.TypeShapeResolver.Resolve<T>()", "PolyType.IShapeable<IEvents>", "T", "IEvents"),
			DiagnosticResult.CompilerError("CS0311").WithSpan(generatedPath, 11, 258, 11, 282)
				.WithArguments("PolyType.Abstractions.TypeShapeResolver.Resolve<T>()", "PolyType.IShapeable<IEvents>", "T", "IEvents"),
			DiagnosticResult.CompilerError("CS0311").WithSpan(generatedPath, 12, 272, 12, 296)
				.WithArguments("PolyType.Abstractions.TypeShapeResolver.Resolve<T>()", "PolyType.IShapeable<IEvents>", "T", "IEvents"),
			DiagnosticResult.CompilerError("CS0311").WithSpan(generatedPath, 13, 258, 13, 282)
				.WithArguments("PolyType.Abstractions.TypeShapeResolver.Resolve<T>()", "PolyType.IShapeable<IEvents>", "T", "IEvents"));
	}

	[Test]
	[Arguments("Func<Task>", false)]
	[Arguments("Func<int>", false)]
	[Arguments("ByRefHandler", false)]
	[Arguments("RefLikeHandler", false)]
	[Arguments("Action", true)]
	public async Task UnsupportedEventDelegatesProduceDiagnosticsAndNoProxy(string handlerType, bool isStatic)
	{
		string source = /* lang=c#-test */ $$"""
			using System;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			internal delegate void ByRefHandler(ref int value);

			internal delegate void RefLikeHandler(Span<int> value);

			[JsonRpcContract]
			internal partial interface IEvents
			{
				{{(isStatic ? "static abstract " : string.Empty)}}event {{handlerType}} {|#0:Changed|};
			}
			""";

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(source, CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(0)
			.WithArguments("IEvents", "proxy events must be instance events with synchronous, void-returning delegates and by-value, non-ref-like parameters"));
	}

	[Test]
	[Arguments("IRemoteObject")]
	[Arguments("IRemoteObject[]")]
	[Arguments("System.Collections.Generic.IReadOnlyList<IRemoteObject[]>")]
	public async Task EventsCannotAcceptRpcMarshalableInterfaceParameters(string parameterType)
	{
		string source = /* lang=c#-test */ $$"""
			using System;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[RpcMarshalable]
			internal partial interface IRemoteObject : IDisposable
			{
				Task CallAsync();
			}

			internal interface IBaseEvents
			{
				event Action<{{parameterType}}> {|#0:Changed|};
			}

			[JsonRpcContract]
			internal partial interface IEvents : IBaseEvents
			{
			}
			""";

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(source, CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(0)
			.WithArguments("IEvents", "proxy events cannot accept RPC-marshalable interface parameters"));
	}

	[Test]
	public async Task UnsupportedMethodSignaturesProduceDiagnosticsAndNoProxy()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[JsonRpcContract]
			internal partial interface IUnsupportedProxySignatures
			{
				ValueTask<T> {|#1:GenericAsync|}<T>(T value, CancellationToken cancellationToken);

				ValueTask<int> {|#3:ParamsAsync|}(params int[] values);

				ValueTask<int> {|#4:RefAsync|}(ref int value);

				ValueTask<int> {|#5:CancellationTokenNotLastAsync|}(CancellationToken cancellationToken, int value);

				string {|#6:UnsupportedReturnAsync|}(int value);
			}
			""";

		DiagnosticResult genericMethod = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(1)
			.WithArguments("IUnsupportedProxySignatures.GenericAsync<T>(T, System.Threading.CancellationToken)", "generic methods are not supported yet");
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

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(Source, genericMethod, paramsParameter, refParameter, cancellationTokenNotLast, unsupportedReturn);
	}

	[Test]
	public async Task OptionalParametersDoNotProduceDiagnostics()
	{
		const string Source = /* lang=c#-test */ """
			#nullable enable
			using System;
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			internal interface IBaseContract
			{
				ValueTask<int> GetAsync(int value = 42, CancellationToken cancellationToken = default);
			}

			[JsonRpcContract]
			internal partial interface IOptionalParameters : IBaseContract
			{
				Task<string?> EchoAsync(string? value = "default", CancellationToken cancellationToken = default);

				ValueTask SetAsync(bool enabled = true, DayOfWeek day = DayOfWeek.Monday, decimal amount = 1.5m);

				void Notify(string? value = null);
			}

			[RpcMarshalable]
			internal partial interface IOptionalMarshalable : IDisposable, IBaseContract
			{
			}
			""";

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(Source);
	}

	[Test]
	public async Task UnsupportedInterfacesProduceDiagnosticsAndNoProxy()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[JsonRpcContract]
			internal partial interface {|#0:IGenericProxy|}<T>
			{
				ValueTask<int> GetAsync(T value, CancellationToken cancellationToken);
			}

			internal static class Container
			{
				[JsonRpcContract]
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

			[JsonRpcContract]
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
				[JsonRpcContract]
				[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
				internal partial interface ICalculator
				{
					ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
				}
			}

			namespace Second
			{
				[JsonRpcContract]
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

			[JsonRpcContract]
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

			[JsonRpcContract]
			[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
			internal partial interface IKeywordParameters
			{
				ValueTask<int> EchoKeywordAsync(int @event, CancellationToken cancellationToken);
			}
			""";

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(Source);
	}

	[Test]
	public async Task ParameterNamesMatchingGeneratedLocalsAndNullableResultsCompileCleanly()
	{
		const string Source = /* lang=c#-test */ """
			#nullable enable
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;
			using PolyType;

			[JsonRpcContract]
			internal partial interface ICollidingLocals
			{
				Task<string?> GetAsync(string arguments, string argumentsBuilder, CancellationToken cancellationToken);

				ValueTask<string?> GetValueAsync(string arguments, CancellationToken cancellationToken);
			}
			""";

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(
			Source,
			CompilerDiagnostics.Warnings,
			DiagnosticResult.CompilerError("CS0311")
				.WithSpan(System.IO.Path.Combine("Nerdbank.JsonRpc.SourceGeneration", "Nerdbank.JsonRpc.SourceGeneration.ClientProxyGenerator", "CollidingLocalsProxy.g.cs"), 18, 107, 18, 140)
				.WithArguments("PolyType.Abstractions.TypeShapeResolver.Resolve<T>()", "PolyType.IShapeable<ICollidingLocals>", "T", "ICollidingLocals"));
	}

	[Test]
	public async Task ProxyFactoryNameAvoidsCollisionWithInterfaceMembers()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[JsonRpcContract]
			internal partial interface ICollidingNames
			{
				Task NerdbankJsonRpc_ProxyFactoryAttribute();

				Task NerdbankJsonRpc_ProxyFactoryAttribute_();

				Task NerdbankJsonRpc_EnabledInterfaces();

				Task Is();
			}
			""";

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(Source, validateGeneratedCode: true);
	}

	[Test]
	[Arguments(0, 1)]
	[Arguments(3, 2)]
	[Arguments(9, 2)]
	public async Task DefaultGroupsDoNotGrowExponentially(int optionalCount, int expectedProxyCount)
	{
		string attributes = string.Join("\n", Enumerable.Range(0, optionalCount).Select(index => $"[RpcMarshalableOptionalInterface({index}, typeof(IOptional{index}))]"));
		string interfaces = string.Join("\n", Enumerable.Range(0, optionalCount).Select(index => $"internal interface IOptional{index} {{ }}"));
		string source = /* lang=c#-test */ $$"""
			using System;
			using Nerdbank.JsonRpc;
			[RpcMarshalable]
			{{attributes}}
			internal partial interface IContract : IDisposable { }
			{{interfaces}}
			""";
		GeneratorRunResult result = await RunGeneratorAsync(source);
		ClassDeclarationSyntax[] proxies = GetProxyClasses(result);
		if (proxies.Length != expectedProxyCount || proxies[0].BaseList!.Types.Count != 2)
		{
			throw new InvalidOperationException($"Expected {expectedProxyCount} proxies including a base-only proxy, got {proxies.Length}.");
		}

		if (optionalCount > 0 && proxies[1].BaseList!.Types.Count != optionalCount + 2)
		{
			throw new InvalidOperationException("The full proxy must implement every optional interface.");
		}
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task ExplicitGroupsReplaceDefaultsAndDeduplicate(bool includeEmpty)
	{
		string source = /* lang=c#-test */ $$"""
			using System;
			using Nerdbank.JsonRpc;
			[RpcMarshalable]
			[RpcMarshalableOptionalInterface(1, typeof(IFirst))]
			[RpcMarshalableOptionalInterface(2, typeof(ISecond))]
			[RpcMarshalableOptionalInterface(3, typeof(IThird))]
			[JsonRpcProxyInterfaceGroup(typeof(IFirst))]
			[JsonRpcProxyInterfaceGroup(typeof(IFirst), typeof(IFirst))]
			[JsonRpcProxyInterfaceGroup(typeof(ISecond))]
			{{(includeEmpty ? "[JsonRpcProxyInterfaceGroup]" : string.Empty)}}
			internal partial interface IContract : IDisposable { }
			internal interface IFirst { }
			internal interface ISecond { }
			internal interface IThird { }
			""";
		ClassDeclarationSyntax[] proxies = GetProxyClasses(await RunGeneratorAsync(source));
		if (proxies.Length != (includeEmpty ? 3 : 2)
			|| proxies.Count(proxy => proxy.BaseList!.Types.Count == 2) != (includeEmpty ? 1 : 0)
			|| proxies.Any(proxy => proxy.BaseList!.Types.Any(type => type.ToString().Contains("IThird"))))
		{
			throw new InvalidOperationException("Only the distinct explicit groups should be emitted, without implicit base-only or full proxies.");
		}
	}

	[Test]
	[Arguments("typeof(string)")]
	[Arguments("null")]
	public async Task InvalidProxyGroupsProduceDiagnostics(string argument)
	{
		string source = /* lang=c#-test */ $$"""
			using Nerdbank.JsonRpc;
			[JsonRpcContract]
			[JsonRpcProxyInterfaceGroup({{argument}})]
			internal partial interface {|#0:IContract|} { }
			""";
		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(
			source,
			CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001").WithLocation(0)
				.WithArguments("IContract", "proxy groups must contain non-null interface types"));
	}

	[Test]
	public async Task MarshalableGroupsRequireDeclaredOptionalInterfaces()
	{
		const string Source = /* lang=c#-test */ """
			using System;
			using Nerdbank.JsonRpc;
			[RpcMarshalable]
			[JsonRpcProxyInterfaceGroup(typeof(IExtra))]
			internal partial interface {|#0:IContract|} : IDisposable { }
			internal interface IExtra { }
			""";
		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(
			Source,
			CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001").WithLocation(0)
				.WithArguments("IContract", "proxy group interface 'IExtra' must be declared as an optional marshalable interface"));
	}

	[Test]
	public async Task InterfaceExtensionsAreDeduplicatedAcrossContractsAndNamespaces()
	{
		const string Source = /* lang=c#-test */ """
			#nullable enable
			using System;
			using Nerdbank.JsonRpc;
			namespace Contracts
			{
				[RpcMarshalable]
				[RpcMarshalableOptionalInterface(1, typeof(Capabilities.IExtra))]
				public partial interface IFirst : IDisposable { }
				[RpcMarshalable]
				[RpcMarshalableOptionalInterface(2, typeof(Capabilities.IExtra))]
				public partial interface ISecond : IDisposable { }
				internal class Consumer
				{
					bool Test(IFirst? first, ISecond? second) => first.Is(typeof(Capabilities.IExtra)) || second.As<Capabilities.IExtra>() is not null;
				}
			}
			namespace Capabilities
			{
				public interface IExtra { }
				internal class Consumer
				{
					bool Test(IExtra? extra) => extra.Is(typeof(IExtra)) && extra.As<IExtra>() is not null;
				}
			}
			""";
		GeneratorRunResult result = await RunGeneratorAsync(Source);
		GeneratedSourceResult extensions = result.GeneratedSources.Single(source => source.HintName == "NerdbankJsonRpcInterfaceExtensions.Capabilities.g.cs");
		if (extensions.SyntaxTree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Count() != 2)
		{
			throw new InvalidOperationException("A shared optional interface should get exactly one Is and one As extension.");
		}
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task InterfaceExtensionVisibilityMatchesStreamJsonRpcPolicy(bool publicExtensions)
	{
		const string Source = /* lang=c#-test */ """
			using Nerdbank.JsonRpc;
			[JsonRpcContract]
			public partial interface IPublicContract { }
			[JsonRpcContract]
			internal partial interface IInternalContract { }
			""";
		GeneratorRunResult result = await RunGeneratorAsync(Source, publicExtensions);
		ClassDeclarationSyntax extensions = result.GeneratedSources.SelectMany(static source => source.SyntaxTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
			.Single(static declaration => declaration.Identifier.ValueText == "NerdbankJsonRpcInterfaceExtensions");
		if (extensions.Modifiers.Any(SyntaxKind.PublicKeyword) != publicExtensions
			|| extensions.Members.OfType<MethodDeclarationSyntax>().Count(static method => method.Modifiers.Any(SyntaxKind.PublicKeyword)) != 2
			|| extensions.Members.OfType<MethodDeclarationSyntax>().Count(static method => method.Modifiers.Any(SyntaxKind.InternalKeyword)) != 2)
		{
			throw new InvalidOperationException("Extensions should be assembly-local by default, with opt-in public accessors only for public contracts.");
		}
	}

	[Test]
	public async Task ImportedInterfaceExtensionsRemainInternalForDownstreamConsumers()
	{
		const string FirstContract = /* lang=c#-test */ """
			using Nerdbank.JsonRpc;
			namespace Contracts;
			[JsonRpcContract]
			public partial interface IExtra { }
			""";
		const string SecondContract = /* lang=c#-test */ """
			using Nerdbank.JsonRpc;
			namespace Contracts;
			[JsonRpcContract]
			[JsonRpcProxyInterfaceGroup(typeof(IExtra))]
			public partial interface IRoot { }
			""";
		const string Consumer = /* lang=c#-test */ """
			using Contracts;
			public static class Consumer
			{
				public static bool IsExtra(IExtra proxy) => proxy.Is(typeof(IExtra));
				public static IExtra? AsExtra(IExtra proxy) => proxy.As<IExtra>();
				public static bool IsRoot(IRoot proxy) => proxy.Is(typeof(IRoot));
				public static IRoot? AsRoot(IRoot proxy) => proxy.As<IRoot>();
			}
			""";

		CSharpParseOptions parseOptions = TestCompilationResources.ParseOptions;
		ImmutableArray<MetadataReference> references = await TestCompilationResources.GetContractReferencesAsync();
		MetadataReference first = Compile("FirstContracts", FirstContract, references, generate: true);
		MetadataReference second = Compile("SecondContracts", SecondContract, references.Add(first), generate: true);
		Compile("Consumer", Consumer, references.Add(first).Add(second), generate: false);

		MetadataReference Compile(string name, [StringSyntax("c#-test")] string source, ImmutableArray<MetadataReference> metadata, bool generate)
		{
			Compilation compilation = CSharpCompilation.Create(
				name,
				[CSharpSyntaxTree.ParseText(source, parseOptions)],
				metadata,
				TestCompilationResources.CompilationOptions.WithNullableContextOptions(NullableContextOptions.Enable));
			if (generate)
			{
				GeneratorDriver driver = CSharpGeneratorDriver.Create(
					[new ClientProxyGenerator().AsSourceGenerator()],
					parseOptions: parseOptions,
					optionsProvider: new ExtensionOptionsProvider(true));
				driver.RunGeneratorsAndUpdateCompilation(compilation, out compilation, out ImmutableArray<Diagnostic> diagnostics);
				if (!diagnostics.IsEmpty)
				{
					throw new InvalidOperationException(string.Join("\n", diagnostics));
				}
			}

			using System.IO.MemoryStream stream = new();
			Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(stream);
			if (!result.Success)
			{
				throw new InvalidOperationException(string.Join("\n", result.Diagnostics));
			}

			return MetadataReference.CreateFromImage(stream.ToArray());
		}
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task UnchangedOutputsAreCachedAndOnlyChangedProxyIsRegenerated(bool changeEvent)
	{
		const string Contracts = /* lang=c#-test */ """
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[JsonRpcContract]
			internal partial interface IFirst { event System.Action Changed; }

			[JsonRpcContract]
			internal partial interface ISecond { }
			""";
		const string ChangedContracts = /* lang=c#-test */ """
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[JsonRpcContract]
			internal partial interface IFirst
			{
				Task<int> GetAsync();

				event System.Action<int> Changed;
			}

			[JsonRpcContract]
			internal partial interface ISecond { }
			""";

		CSharpParseOptions parseOptions = TestCompilationResources.ParseOptions;
		SyntaxTree contractsTree = CSharpSyntaxTree.ParseText(Contracts, parseOptions, "Contracts.cs");
		ImmutableArray<MetadataReference> references = await TestCompilationResources.GetGeneratorReferencesAsync();
		CSharpCompilation compilation = CSharpCompilation.Create(
			"IncrementalGeneratorTests",
			[contractsTree],
			references,
			TestCompilationResources.CompilationOptions);
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

		string changedSource = changeEvent ? Contracts.Replace("System.Action Changed", "System.Action<int> Changed") : ChangedContracts;
		SyntaxTree changedTree = CSharpSyntaxTree.ParseText(changedSource, parseOptions, "Contracts.cs");
		CSharpCompilation compilationWithChangedProxy = compilationWithUnrelatedSource.ReplaceSyntaxTree(contractsTree, changedTree);
		driver = driver.RunGenerators(compilationWithChangedProxy);
		GeneratorRunResult changedRun = driver.GetRunResult().Results.Single();
		IncrementalStepRunReason[] outputReasons = changedRun.TrackedSteps["JsonRpcContractOutputs"]
			.SelectMany(static step => step.Outputs)
			.Select(static output => output.Reason)
			.ToArray();
		bool hasUnchangedProxy = outputReasons.Contains(IncrementalStepRunReason.Cached) || outputReasons.Contains(IncrementalStepRunReason.Unchanged);
		if (!outputReasons.Contains(IncrementalStepRunReason.Modified) || !hasUnchangedProxy)
		{
			throw new InvalidOperationException($"Changing one contract should regenerate only its proxy. Reasons: {string.Join(", ", outputReasons)}");
		}

		static bool AreOutputsStable(GeneratorRunResult run)
			=> run.TrackedSteps["JsonRpcContractOutputs"]
				.SelectMany(static step => step.Outputs)
				.All(static output => output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged);
	}

	private static ClassDeclarationSyntax[] GetProxyClasses(GeneratorRunResult result)
		=> [
			.. result.GeneratedSources.SelectMany(static source => source.SyntaxTree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
				.Where(static declaration => declaration.BaseList?.Types.Any(static type => type.ToString() == "global::Nerdbank.JsonRpc.JsonRpcProxy") is true),
		];

	private static async Task<GeneratorRunResult> RunGeneratorAsync([StringSyntax("c#-test")] string source, bool publicExtensions = false)
	{
		CSharpParseOptions parseOptions = TestCompilationResources.ParseOptions;
		ImmutableArray<MetadataReference> references = await TestCompilationResources.GetContractReferencesAsync();
		CSharpCompilation compilation = CSharpCompilation.Create(
			"ProxyGroupTests",
			[CSharpSyntaxTree.ParseText(source, parseOptions)],
			references,
			TestCompilationResources.CompilationOptions);
		GeneratorDriver driver = CSharpGeneratorDriver.Create(
			[new ClientProxyGenerator().AsSourceGenerator()],
			parseOptions: parseOptions,
			optionsProvider: new ExtensionOptionsProvider(publicExtensions));
		driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation updated, out ImmutableArray<Diagnostic> diagnostics);
		Diagnostic[] errors = [.. diagnostics.Concat(updated.GetDiagnostics()).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];
		if (errors.Length > 0)
		{
			throw new InvalidOperationException(string.Join("\n", errors.Select(static error => error.ToString())));
		}

		GeneratorRunResult result = driver.GetRunResult().Results.Single();
		if (!result.Diagnostics.IsEmpty)
		{
			throw new InvalidOperationException(string.Join("\n", result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
		}

		return result;
	}

	private sealed class ExtensionOptionsProvider(bool publicExtensions) : AnalyzerConfigOptionsProvider
	{
		public override AnalyzerConfigOptions GlobalOptions { get; } = new ExtensionOptions(publicExtensions);

		public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => new ExtensionOptions(false);

		public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => new ExtensionOptions(false);
	}

	private sealed class ExtensionOptions(bool enabled) : AnalyzerConfigOptions
	{
		public override bool TryGetValue(string key, out string value)
		{
			value = enabled ? "true" : "false";
			return key == "build_property.PublicRpcMarshalableInterfaceExtensions";
		}
	}
}
