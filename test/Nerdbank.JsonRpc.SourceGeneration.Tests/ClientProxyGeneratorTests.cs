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

	[Fact]
	public async Task UnsupportedInterfacesProduceDiagnosticsAndNoProxy()
	{
		const string Source = """
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
