// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Nerdbank.JsonRpc.SourceGeneration;

internal static class CSharpBrokeredServiceAnalyzerVerifier
{
	internal const string Contracts = """
		using System;
		using System.Threading.Tasks;
		using Nerdbank.JsonRpc.BrokeredServices;
		namespace Nerdbank.JsonRpc.BrokeredServices
		{
			public interface IJsonRpcDescriptor<T> where T : class { }
			public interface IServiceBroker
			{
				ValueTask<T> GetProxyAsync<T>(IJsonRpcDescriptor<T> descriptor) where T : class;
			}
			public sealed class ServiceBrokerClient
			{
				public ValueTask<Rental<T>> GetProxyAsync<T>(IJsonRpcDescriptor<T> descriptor) where T : class => default;
				public sealed class Rental<T> : IDisposable where T : class
				{
					public T Proxy => default;
					public void Dispose() { }
				}
			}
		}
		interface IFoo { }
		""";

	internal static async Task VerifyAsync([StringSyntax("c#-test")] string source, string diagnosticId, bool includeContracts = true, params DiagnosticResult[] expectedDiagnostics)
	{
		Test test = new()
		{
			TestCode = includeContracts ? Contracts + Environment.NewLine + source : source,
			ReferenceAssemblies = ReferenceAssemblies.Net.Net90,
		};
		test.MarkupOptions = MarkupOptions.UseFirstDescriptor;
		test.DisabledDiagnostics.Add(diagnosticId == "NBJSONRPC004" ? "NBJSONRPC005" : "NBJSONRPC004");
		test.ExpectedDiagnostics.AddRange(expectedDiagnostics);
		await test.RunAsync();
	}

	private sealed class Test : CSharpCodeFixTest<BrokeredServiceAnalyzer, EmptyCodeFixProvider, DefaultVerifier>
	{
		public Test()
		{
			this.SolutionTransforms.Add(static (solution, projectId) => solution.WithProjectParseOptions(projectId, new CSharpParseOptions(LanguageVersion.Preview)));
		}
	}

	private sealed class EmptyCodeFixProvider : CodeFixProvider
	{
		public override ImmutableArray<string> FixableDiagnosticIds => [];

		public override Task RegisterCodeFixesAsync(CodeFixContext context) => Task.CompletedTask;
	}
}
