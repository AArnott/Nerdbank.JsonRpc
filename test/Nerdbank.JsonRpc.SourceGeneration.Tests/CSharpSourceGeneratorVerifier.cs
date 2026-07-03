// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Testing.Verifiers;
using Nerdbank.JsonRpc;
using Nerdbank.JsonRpc.SourceGeneration;
using Nerdbank.MessagePack;
using Nerdbank.Streams;
using PolyType;

internal static class CSharpSourceGeneratorVerifier
{
	internal static DiagnosticResult Diagnostic(string diagnosticId)
		=> new(diagnosticId, DiagnosticSeverity.Warning);

	internal static async Task VerifyGeneratorAsync(string source, params DiagnosticResult[] expected)
	{
		Test test = new()
		{
			CompilerDiagnostics = CompilerDiagnostics.None,
			TestCode = source,
			ReferenceAssemblies = ReferenceAssemblies.Net.Net90,
			TestBehaviors = TestBehaviors.SkipGeneratedCodeCheck | TestBehaviors.SkipGeneratedSourcesCheck,
		};

		test.TestState.AdditionalReferences.Add(typeof(GenerateJsonRpcProxyAttribute).Assembly);
		test.TestState.AdditionalReferences.Add(typeof(MessagePackSerializer).Assembly);
		test.TestState.AdditionalReferences.Add(typeof(Sequence<>).Assembly);
		test.TestState.AdditionalReferences.Add(typeof(ITypeShape<>).Assembly);
		test.ExpectedDiagnostics.AddRange(expected);
		await test.RunAsync();
	}

	private sealed class Test : CSharpSourceGeneratorTest<ClientProxyGenerator, DefaultVerifier>
	{
		public Test()
		{
			this.SolutionTransforms.Add(static (solution, projectId) => solution.WithProjectParseOptions(projectId, new CSharpParseOptions(LanguageVersion.Preview)));
		}
	}
}
