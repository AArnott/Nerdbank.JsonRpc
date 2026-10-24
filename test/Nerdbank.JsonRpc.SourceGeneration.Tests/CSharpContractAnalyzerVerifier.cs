// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Nerdbank.JsonRpc;
using Nerdbank.JsonRpc.SourceGeneration;
using PolyType;

internal static class CSharpContractAnalyzerVerifier
{
	internal static DiagnosticResult Diagnostic(string id, params object[] arguments)
		=> new DiagnosticResult(id, DiagnosticSeverity.Warning).WithArguments(arguments);

	internal static Task VerifyAsync([StringSyntax("c#-test")] string source, params DiagnosticResult[] expected)
		=> VerifyAsync(source, includeLibrary: true, expected);

	internal static Task VerifyWithoutLibraryAsync([StringSyntax("c#-test")] string source)
		=> VerifyAsync(source, includeLibrary: false);

	private static async Task VerifyAsync([StringSyntax("c#-test")] string source, bool includeLibrary, params DiagnosticResult[] expected)
	{
		Test test = new()
		{
			TestCode = source,
			ReferenceAssemblies = CSharpSourceGeneratorVerifier.Net10,
		};
		if (includeLibrary)
		{
			test.TestState.AdditionalReferences.Add(typeof(JsonRpcContractAttribute).Assembly);
			test.TestState.AdditionalReferences.Add(typeof(ITypeShape<>).Assembly);
		}

		test.ExpectedDiagnostics.AddRange(expected);
		await test.RunAsync();
	}

	private sealed class Test : CSharpCodeFixTest<JsonRpcContractAnalyzer, EmptyCodeFixProvider, DefaultVerifier>
	{
		public Test()
		{
			this.SolutionTransforms.Add(static (solution, projectId) => solution.WithProjectParseOptions(projectId, new CSharpParseOptions(LanguageVersion.Preview)));
		}
	}

	private sealed class EmptyCodeFixProvider : CodeFixProvider
	{
		public EmptyCodeFixProvider()
		{
		}

		public override ImmutableArray<string> FixableDiagnosticIds => [];

		public override Task RegisterCodeFixesAsync(CodeFixContext context) => Task.CompletedTask;
	}
}
