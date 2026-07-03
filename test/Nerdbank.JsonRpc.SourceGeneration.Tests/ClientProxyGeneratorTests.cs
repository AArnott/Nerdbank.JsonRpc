// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis.Testing;
using Xunit;

public class ClientProxyGeneratorTests
{
	[Fact]
	public async Task UnsupportedMethodSignaturesProduceDiagnosticsAndNoProxy()
	{
		const string Source = """
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[GenerateJsonRpcProxy]
			internal partial interface IUnsupportedProxySignatures
			{
				ValueTask<T> {|#0:GenericAsync|}<T>(T value, CancellationToken cancellationToken);

				ValueTask<int> {|#1:OptionalAsync|}(int value = 1);

				ValueTask<int> {|#2:ParamsAsync|}(params int[] values);

				ValueTask<int> {|#3:RefAsync|}(ref int value);
			}
			""";

		DiagnosticResult genericMethod = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(0)
			.WithArguments("IUnsupportedProxySignatures.GenericAsync<T>(T, System.Threading.CancellationToken)", "generic methods are not supported yet");
		DiagnosticResult optionalParameter = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(1)
			.WithArguments("IUnsupportedProxySignatures.OptionalAsync(int)", "optional parameters with default values are not supported yet");
		DiagnosticResult paramsParameter = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(2)
			.WithArguments("IUnsupportedProxySignatures.ParamsAsync(params int[])", "params parameters are not supported yet");
		DiagnosticResult refParameter = CSharpSourceGeneratorVerifier.Diagnostic("NBJSONRPC001")
			.WithLocation(3)
			.WithArguments("IUnsupportedProxySignatures.RefAsync(ref int)", "ref, out, and in parameters are not supported yet");

		await CSharpSourceGeneratorVerifier.VerifyGeneratorAsync(Source, genericMethod, optionalParameter, paramsParameter, refParameter);
	}

	[Fact]
	public async Task KeywordIdentifiersAreEscapedInGeneratedProxy()
	{
		const string Source = """
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
}
