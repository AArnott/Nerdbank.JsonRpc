// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using TUnit.Core;
using Verify = CSharpContractAnalyzerVerifier;

public class MarshalableInterfaceTypeCheckTests
{
	[Test]
	public async Task DirectConversionsAndTypeChecksAreDiscouraged()
	{
		const string Source = /* lang=c#-test */ """
			using Nerdbank.JsonRpc;

			[RpcMarshalable(CallScopedLifetime = true)]
			interface IFirst { }

			[RpcMarshalable(CallScopedLifetime = true)]
			interface ISecond { }

			class C
			{
				void M(IFirst value)
				{
					_ = {|#0:(ISecond)value|};
					_ = {|#1:value as ISecond|};
					_ = {|#2:value is ISecond|};
				}
			}
			""";

		await Verify.VerifyAsync(
			Source,
			Verify.Diagnostic("NBJSONRPC050").WithLocation(0),
			Verify.Diagnostic("NBJSONRPC050").WithLocation(1),
			Verify.Diagnostic("NBJSONRPC050").WithLocation(2));
	}

	[Test]
	public async Task UnrelatedInterfacesAreAllowed()
	{
		const string Source = /* lang=c#-test */ """
			using Nerdbank.JsonRpc;

			[RpcMarshalable(CallScopedLifetime = true)]
			interface IMarshalable { }

			interface IUnrelated { }

			class C
			{
				void M(IMarshalable value)
				{
					_ = value as IUnrelated;
					_ = value is IUnrelated;
				}
			}
			""";

		await Verify.VerifyAsync(Source);
	}
}
