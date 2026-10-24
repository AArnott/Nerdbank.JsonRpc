// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Nerdbank.JsonRpc.SourceGeneration;

internal static class CSharpSourceGeneratorVerifier
{
	internal static DiagnosticResult Diagnostic(string diagnosticId)
		=> new(diagnosticId, DiagnosticSeverity.Warning);

	internal static Task VerifyGeneratorAsync([StringSyntax("c#-test")] string source, params DiagnosticResult[] expected)
		=> VerifyGeneratorAsync(source, validateGeneratedCode: false, expected);

	internal static Task VerifyGeneratorAsync([StringSyntax("c#-test")] string source, bool validateGeneratedCode, params DiagnosticResult[] expected)
		=> VerifyGeneratorAsync(source, validateGeneratedCode ? CompilerDiagnostics.Errors : CompilerDiagnostics.None, expected);

	internal static async Task VerifyGeneratorAsync([StringSyntax("c#-test")] string source, CompilerDiagnostics compilerDiagnostics, params DiagnosticResult[] expected)
	{
		bool validateGeneratedCode = compilerDiagnostics != CompilerDiagnostics.None;
		Test test = new()
		{
			CompilerDiagnostics = compilerDiagnostics,
			TestCode = source,
			ReferenceAssemblies = TestCompilationResources.Net10,
			TestBehaviors = validateGeneratedCode
				? TestBehaviors.SkipGeneratedSourcesCheck
				: TestBehaviors.SkipGeneratedCodeCheck | TestBehaviors.SkipGeneratedSourcesCheck,
		};

		test.DisabledDiagnostics.Add("CS1701");
		test.DisabledDiagnostics.Add("CS1702");
		test.TestState.AdditionalReferences.AddRange(TestCompilationResources.GeneratorAdditionalReferences);
		test.ExpectedDiagnostics.AddRange(expected);
		await test.RunAsync();
	}

	private sealed class Test : CSharpSourceGeneratorTest<ClientProxyGenerator, DefaultVerifier>
	{
		public Test()
		{
			this.SolutionTransforms.Add(static (solution, projectId) => solution.WithProjectParseOptions(projectId, TestCompilationResources.ParseOptions));
		}
	}
}
