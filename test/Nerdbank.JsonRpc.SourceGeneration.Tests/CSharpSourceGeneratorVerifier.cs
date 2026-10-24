// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Nerdbank.JsonRpc;
using Nerdbank.JsonRpc.SourceGeneration;
using Nerdbank.MessagePack;
using Nerdbank.Streams;
using PolyType;

internal static class CSharpSourceGeneratorVerifier
{
	internal static ReferenceAssemblies Net10
	{
		get
		{
			string refPackRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "packs", "Microsoft.NETCore.App.Ref");
			string version = Directory.EnumerateDirectories(refPackRoot)
				.Select(static directory => Path.GetFileName(directory))
				.OfType<string>()
				.Where(static name => name.StartsWith("10.", StringComparison.Ordinal))
				.OrderByDescending(static name => Version.Parse(name.Split('-')[0]))
				.FirstOrDefault() ?? throw new InvalidOperationException("No .NET 10 reference pack was found.");
			string path = Path.Combine(refPackRoot, version, "ref", "net10.0");
			return new ReferenceAssemblies("net10.0", new PackageIdentity("Microsoft.NETCore.App.Ref", version), path);
		}
	}

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
			ReferenceAssemblies = Net10,
			TestBehaviors = validateGeneratedCode
				? TestBehaviors.SkipGeneratedSourcesCheck
				: TestBehaviors.SkipGeneratedCodeCheck | TestBehaviors.SkipGeneratedSourcesCheck,
		};

		test.DisabledDiagnostics.Add("CS1701");
		test.DisabledDiagnostics.Add("CS1702");
		test.TestState.AdditionalReferences.Add(typeof(JsonRpcContractAttribute).Assembly);
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
