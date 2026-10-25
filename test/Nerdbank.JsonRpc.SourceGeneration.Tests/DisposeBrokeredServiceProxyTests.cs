// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

public class DisposeBrokeredServiceProxyTests
{
	[Test]
	[Arguments("var proxy = await [|broker.GetProxyAsync(descriptor)|];")]
	[Arguments("_ = await [|broker.GetProxyAsync(descriptor)|];")]
	[Arguments("var proxy = await [|broker.GetProxyAsync(descriptor)|]; if (flag) ((IDisposable)proxy).Dispose();")]
	[Arguments("var proxy = await [|broker.GetProxyAsync(descriptor)|]; if (flag) return; ((IDisposable)proxy).Dispose();")]
	[Arguments("var proxy = await [|broker.GetProxyAsync(descriptor)|]; _ = ((IAsyncDisposable)proxy).DisposeAsync();")]
	[Arguments("var proxy = await [|broker.GetProxyAsync(descriptor)|]; proxy = null; (proxy as IDisposable)?.Dispose();")]
	[Arguments("var proxy = await [|broker.GetProxyAsync(descriptor)|]; Action cleanup = () => ((IDisposable)proxy).Dispose();")]
	[Arguments("var proxy = await [|broker.GetProxyAsync(descriptor)|].ConfigureAwait(false);")]
	[Arguments("var proxy = await [|broker.GetProxyAsync(descriptor)|]; switch (flag) { case true: ((IDisposable)proxy).Dispose(); break; }")]
	[Arguments("for (int i = 0; i < 2; i++) { var proxy = await [|broker.GetProxyAsync(descriptor)|]; }")]
	[Arguments("var proxy = [|broker.GetProxyAsync(descriptor)|].Result;")]
	[Arguments("var proxy = await [|broker.GetProxyAsync(descriptor)|]; lock (this) { if (flag) ((IDisposable)proxy).Dispose(); }")]
	[Arguments("var proxy = await [|broker.GetProxyAsync(descriptor)|]; using var unrelated = new System.IO.MemoryStream(proxy.GetHashCode());")]
	[Arguments("var proxy = await [|broker.GetProxyAsync(descriptor)|]; await (flag switch { true => ((IAsyncDisposable)proxy).DisposeAsync(), _ => default(ValueTask) });")]
	public Task ReportsUnreleasedLocal(string body) => VerifyLocalAsync(body);

	[Test]
	[Arguments("var proxy = await broker.GetProxyAsync(descriptor); using (proxy as IDisposable) { }")]
	[Arguments("var proxy = await broker.GetProxyAsync(descriptor); using IDisposable lifetime = (IDisposable)proxy;")]
	[Arguments("var proxy = await broker.GetProxyAsync(descriptor); await using IAsyncDisposable lifetime = (IAsyncDisposable)proxy;")]
	[Arguments("using var lifetime = (IDisposable)await broker.GetProxyAsync(descriptor);")]
	[Arguments("await using var lifetime = (IAsyncDisposable)await broker.GetProxyAsync(descriptor);")]
	[Arguments("using var lifetime = (IDisposable)broker.GetProxyAsync(descriptor).Result;")]
	[Arguments("var proxy = await broker.GetProxyAsync(descriptor).ConfigureAwait(false); using (proxy as IDisposable) { }")]
	[Arguments("var proxy = await broker.GetProxyAsync(descriptor) ?? throw new Exception(); using (proxy as IDisposable) { }")]
	[Arguments("var proxy = await broker.GetProxyAsync(descriptor); var alias = proxy; using (alias as IDisposable) { }")]
	[Arguments("var proxy = await broker.GetProxyAsync(descriptor); try { if (flag) return; } finally { (proxy as IDisposable)?.Dispose(); }")]
	[Arguments("var proxy = await broker.GetProxyAsync(descriptor); if (flag) ((IDisposable)proxy).Dispose(); else ((IDisposable)proxy).Dispose();")]
	[Arguments("var proxy = await broker.GetProxyAsync(descriptor); if (proxy is null) return; ((IDisposable)proxy).Dispose();")]
	[Arguments("var proxy = await broker.GetProxyAsync(descriptor); await ((IAsyncDisposable)proxy).DisposeAsync();")]
	[Arguments("var proxy = await broker.GetProxyAsync(descriptor); if (proxy is IDisposable disposable) disposable.Dispose();")]
	[Arguments("var proxy = await broker.GetProxyAsync(descriptor); switch (flag) { case true: ((IDisposable)proxy).Dispose(); break; default: ((IDisposable)proxy).Dispose(); break; }")]
	[Arguments("for (int i = 0; i < 2; i++) { var proxy = await broker.GetProxyAsync(descriptor); using (proxy as IDisposable) { } }")]
	public Task AcceptsReleasedLocal(string body) => VerifyLocalAsync(body);

	[Test]
	public Task AcceptsOwnershipTransferToCaller() => VerifyAsync("""
		class C
		{
			async ValueTask<IFoo> M(IServiceBroker broker, IJsonRpcDescriptor<IFoo> descriptor)
				=> await broker.GetProxyAsync(descriptor);
		}
		""");

	[Test]
	[Arguments("field = await [|broker.GetProxyAsync(descriptor)|];", " : IDisposable", "public void Dispose() => (field as IDisposable)?.Dispose();")]
	[Arguments("(field as IDisposable)?.Dispose(); field = await [|broker.GetProxyAsync(descriptor)|];", "", "")]
	[Arguments("(field as IDisposable)?.Dispose(); field = await [|broker.GetProxyAsync(descriptor)|];", " : IDisposable", "public void Dispose() { }")]
	[Arguments("(field as IDisposable)?.Dispose(); field = await [|broker.GetProxyAsync(descriptor)|];", " : IDisposable", "public void Dispose() { if (DateTime.Now.Ticks == 0) (field as IDisposable)?.Dispose(); }")]
	[Arguments("for (int i = 0; i < 2; i++) field = await [|broker.GetProxyAsync(descriptor)|];", " : IDisposable", "public void Dispose() => (field as IDisposable)?.Dispose();")]
	[Arguments("var proxy = await [|broker.GetProxyAsync(descriptor)|]; field = proxy;", " : IDisposable", "public void Dispose() => (field as IDisposable)?.Dispose();")]
	[Arguments("if (field is null) field = await [|broker.GetProxyAsync(descriptor)|];", " : IDisposable, IAsyncDisposable", "public void Dispose() { } public async ValueTask DisposeAsync() { await ((IAsyncDisposable)field).DisposeAsync(); }")]
	public Task ReportsUnsafeFieldOwnership(string body, string interfaces, string disposal) => VerifyFieldAsync(body, interfaces, disposal);

	[Test]
	[Arguments("(field as IDisposable)?.Dispose(); field = await broker.GetProxyAsync(descriptor);")]
	[Arguments("if (field is null) field = await broker.GetProxyAsync(descriptor);")]
	[Arguments("if (field == null) { field = await broker.GetProxyAsync(descriptor); }")]
	[Arguments("if (field != null) (field as IDisposable)?.Dispose(); field = await broker.GetProxyAsync(descriptor);")]
	[Arguments("field ??= await broker.GetProxyAsync(descriptor);")]
	[Arguments("var proxy = await broker.GetProxyAsync(descriptor); (field as IDisposable)?.Dispose(); field = proxy;")]
	[Arguments("if (field is null) { var proxy = await broker.GetProxyAsync(descriptor); field = proxy; }")]
	[Arguments("if (field is null) { for (int i = 0; i < 2; i++) { field = await broker.GetProxyAsync(descriptor); break; } }")]
	[Arguments("for (int i = 0; i < 2; i++) { (field as IDisposable)?.Dispose(); field = await broker.GetProxyAsync(descriptor); }")]
	public Task AcceptsSafeFieldOwnership(string body) => VerifyFieldAsync(body, " : IDisposable", "public void Dispose() => (field as IDisposable)?.Dispose();");

	[Test]
	public Task AcceptsAsyncOwner() => VerifyFieldAsync(
		"if (field is null) field = await broker.GetProxyAsync(descriptor);",
		" : IAsyncDisposable",
		"public async ValueTask DisposeAsync() { if (field != null) await ((IAsyncDisposable)field).DisposeAsync(); }");

	[Test]
	public Task IgnoresUnrelatedMethod() => VerifyAsync("""
		class C
		{
			ValueTask<IFoo> GetProxyAsync(IJsonRpcDescriptor<IFoo> descriptor) => default;
			async Task M(IJsonRpcDescriptor<IFoo> descriptor) { var proxy = await GetProxyAsync(descriptor); }
		}
		""");

	[Test]
	public Task AnalyzesConcreteBrokerImplementation() => VerifyAsync("""
		class Broker : IServiceBroker
		{
			public ValueTask<T> GetProxyAsync<T>(IJsonRpcDescriptor<T> descriptor) where T : class => default;
		}
		class C
		{
			async Task M(Broker broker, IJsonRpcDescriptor<IFoo> descriptor)
			{
				var proxy = await [|broker.GetProxyAsync(descriptor)|];
			}
		}
		""");

	[Test]
	public Task AnalyzesLambdaBodyIndependently() => VerifyAsync("""
		class C
		{
			void M(IServiceBroker broker, IJsonRpcDescriptor<IFoo> descriptor)
			{
				Func<Task> action = async () => { var proxy = await [|broker.GetProxyAsync(descriptor)|]; };
			}
		}
		""");

	[Test]
	public Task AnalyzesDerivedBrokerInterface() => VerifyAsync("""
		interface IHostBroker : IServiceBroker { }
		class C
		{
			async Task M(IHostBroker broker, IJsonRpcDescriptor<IFoo> descriptor)
			{
				var proxy = await [|broker.GetProxyAsync(descriptor)|];
			}
		}
		""");

	[Test]
	public Task IgnoresGeneratedCode() => VerifyAsync("""
		[System.CodeDom.Compiler.GeneratedCode("test", "1")]
		class C
		{
			async Task M(IServiceBroker broker, IJsonRpcDescriptor<IFoo> descriptor)
			{
				var proxy = await broker.GetProxyAsync(descriptor);
			}
		}
		""");

	[Test]
	public Task WorksWithoutBrokerReference() => CSharpBrokeredServiceAnalyzerVerifier.VerifyAsync(
		"""
		class C { void M() { } }
		""",
		"NBJSONRPC004",
		includeContracts: false);

	[Test]
	public Task AcceptsPatternOwnershipTransfer() => VerifyAsync("""
		class C
		{
			async ValueTask<IFoo> M(IServiceBroker[] brokers, IJsonRpcDescriptor<IFoo> descriptor)
			{
				foreach (IServiceBroker broker in brokers)
				{
					if (await broker.GetProxyAsync(descriptor).ConfigureAwait(false) is { } proxy)
					{
						return proxy;
					}
				}
				return null;
			}
		}
		""");

	[Test]
	public Task AcceptsExplicitDisposalImplementation() => VerifyFieldAsync(
		"if (field is null) field = await broker.GetProxyAsync(descriptor);",
		" : IDisposable",
		"void IDisposable.Dispose() => (field as IDisposable)?.Dispose();");

	[Test]
	public Task ReportsUnawaitedOwnerDisposal() => VerifyFieldAsync(
		"if (field is null) field = await [|broker.GetProxyAsync(descriptor)|];",
		" : IAsyncDisposable",
		"public ValueTask DisposeAsync() { _ = ((IAsyncDisposable)field).DisposeAsync(); return default; }");

	[Test]
	public Task AcceptsPropertyOwnership() => VerifyAsync("""
		class C : IDisposable
		{
			IFoo Proxy { get; set; }
			async Task M(IServiceBroker broker, IJsonRpcDescriptor<IFoo> descriptor)
			{
				(Proxy as IDisposable)?.Dispose();
				Proxy = await broker.GetProxyAsync(descriptor);
			}
			public void Dispose() => (Proxy as IDisposable)?.Dispose();
		}
		""");

	[Test]
	public Task AnotherInstanceDoesNotDisposeOwnedField() => VerifyAsync("""
		class C : IDisposable
		{
			IFoo field;
			C other;
			async Task M(IServiceBroker broker, IJsonRpcDescriptor<IFoo> descriptor)
			{
				(other.field as IDisposable)?.Dispose();
				field = await [|broker.GetProxyAsync(descriptor)|];
			}
			public void Dispose() => (field as IDisposable)?.Dispose();
		}
		""");

	[Test]
	public Task DoesNotAssumeWrapperOwnsProxy() => VerifyAsync("""
		class Wrapper : IDisposable
		{
			public Wrapper(IFoo proxy) { }
			public void Dispose() { }
		}
		class C
		{
			async Task M(IServiceBroker broker, IJsonRpcDescriptor<IFoo> descriptor)
			{
				using var wrapper = new Wrapper(await [|broker.GetProxyAsync(descriptor)|]);
			}
		}
		""");

	[Test]
	public Task ToleratesIncompleteCode() => CSharpBrokeredServiceAnalyzerVerifier.VerifyAsync(
		"""
		class C
		{
			async Task M(IServiceBroker broker, IJsonRpcDescriptor<IFoo> descriptor)
			{
				var proxy = await [|broker.GetProxyAsync(descriptor)|];
				{|#0:missing|}();
			}
		}
		""",
		"NBJSONRPC004",
		true,
		Microsoft.CodeAnalysis.Testing.DiagnosticResult.CompilerError("CS0103").WithLocation(0).WithArguments("missing"));

	[Test]
	[Arguments("field = broker.GetProxyAsync(descriptor).Result;")]
	[Arguments("var proxy = broker.GetProxyAsync(descriptor).Result; field = proxy;")]
	public Task AcceptsFirstConstructorAssignment(string body) => VerifyAsync("""
		class C : IDisposable
		{
			IFoo field;
			public C(IServiceBroker broker, IJsonRpcDescriptor<IFoo> descriptor)
			{
		""" + body + """

			}
			public void Dispose() => (field as IDisposable)?.Dispose();
		}
		""");

	private static Task VerifyLocalAsync([System.Diagnostics.CodeAnalysis.StringSyntax("c#-test")] string body)
		=> VerifyAsync("class C { async Task M(IServiceBroker broker, IJsonRpcDescriptor<IFoo> descriptor, bool flag) { " + body + " } }");

	private static Task VerifyFieldAsync(
		[System.Diagnostics.CodeAnalysis.StringSyntax("c#-test")] string body,
		string interfaces,
		[System.Diagnostics.CodeAnalysis.StringSyntax("c#-test")] string disposal)
		=> VerifyAsync("class C" + interfaces + " { IFoo field; async Task M(IServiceBroker broker, IJsonRpcDescriptor<IFoo> descriptor) { " + body + " } " + disposal + " }");

	private static Task VerifyAsync([System.Diagnostics.CodeAnalysis.StringSyntax("c#-test")] string source)
		=> CSharpBrokeredServiceAnalyzerVerifier.VerifyAsync(source, "NBJSONRPC004");
}
