// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Nerdbank.JsonRpc;
using Nerdbank.JsonRpc.SourceGeneration;
using Xunit;

public class ClientProxyGeneratorTests
{
	[Fact]
	public void UnsupportedMethodSignaturesProduceDiagnosticsAndNoProxy()
	{
		const string Source = """
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[GenerateJsonRpcProxy]
			internal partial interface IUnsupportedProxySignatures
			{
				ValueTask<T> GenericAsync<T>(T value, CancellationToken cancellationToken);

				ValueTask<int> OptionalAsync(int value = 1);

				ValueTask<int> ParamsAsync(params int[] values);

				ValueTask<int> RefAsync(ref int value);
			}
			""";

		GeneratorDriverRunResult result = RunGenerator(Source);

		Diagnostic[] diagnostics = GetDiagnostics(result).Where(static diagnostic => diagnostic.Id == "NBJSONRPC001").ToArray();
		Assert.Equal(4, diagnostics.Length);
		Assert.Contains(diagnostics, static diagnostic => diagnostic.GetMessage().Contains("generic methods", StringComparison.Ordinal));
		Assert.Contains(diagnostics, static diagnostic => diagnostic.GetMessage().Contains("optional parameters", StringComparison.Ordinal));
		Assert.Contains(diagnostics, static diagnostic => diagnostic.GetMessage().Contains("params parameters", StringComparison.Ordinal));
		Assert.Contains(diagnostics, static diagnostic => diagnostic.GetMessage().Contains("ref, out, and in parameters", StringComparison.Ordinal));
		Assert.Empty(result.Results.Single().GeneratedSources);
	}

	[Fact]
	public void KeywordIdentifiersAreEscapedInGeneratedProxy()
	{
		const string Source = """
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[GenerateJsonRpcProxy]
			internal partial interface IKeywordParameters
			{
				ValueTask<int> EchoKeywordAsync(int @event, CancellationToken cancellationToken);
			}
			""";

		GeneratorDriverRunResult result = RunGenerator(Source);

		Assert.DoesNotContain(GetDiagnostics(result), static diagnostic => diagnostic.Id == "NBJSONRPC001");
		GeneratedSourceResult generatedSource = Assert.Single(result.Results.Single().GeneratedSources);
		string sourceText = generatedSource.SourceText.ToString();
		Assert.Contains("EchoKeywordAsync", sourceText, StringComparison.Ordinal);
		Assert.Contains("@event", sourceText, StringComparison.Ordinal);
		Assert.Contains("Serialize(ref argumentsWriter, @event,", sourceText, StringComparison.Ordinal);
	}

	private static GeneratorDriverRunResult RunGenerator(string source)
	{
		CSharpCompilation compilation = CSharpCompilation.Create(
			"GeneratorTests",
			[CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
			GetReferences(),
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

		GeneratorDriver driver = CSharpGeneratorDriver.Create([new ClientProxyGenerator().AsSourceGenerator()], parseOptions: new CSharpParseOptions(LanguageVersion.Preview));
		driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
		return driver.GetRunResult();
	}

	private static IEnumerable<Diagnostic> GetDiagnostics(GeneratorDriverRunResult result)
	{
		HashSet<string> seenDiagnostics = new(StringComparer.Ordinal);
		foreach (Diagnostic diagnostic in result.Diagnostics)
		{
			if (seenDiagnostics.Add(GetDiagnosticKey(diagnostic)))
			{
				yield return diagnostic;
			}
		}

		foreach (GeneratorRunResult generatorResult in result.Results)
		{
			if (generatorResult.Diagnostics.IsDefault)
			{
				continue;
			}

			foreach (Diagnostic diagnostic in generatorResult.Diagnostics)
			{
				if (seenDiagnostics.Add(GetDiagnosticKey(diagnostic)))
				{
					yield return diagnostic;
				}
			}
		}
	}

	private static string GetDiagnosticKey(Diagnostic diagnostic)
		=> diagnostic.Id + ":" + diagnostic.Location.GetLineSpan().StartLinePosition + ":" + diagnostic.GetMessage();

	private static IEnumerable<MetadataReference> GetReferences()
	{
		string trustedPlatformAssemblies = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
		foreach (string assemblyPath in trustedPlatformAssemblies.Split(Path.PathSeparator))
		{
			yield return MetadataReference.CreateFromFile(assemblyPath);
		}

		yield return MetadataReference.CreateFromFile(typeof(GenerateJsonRpcProxyAttribute).Assembly.Location);
	}
}
