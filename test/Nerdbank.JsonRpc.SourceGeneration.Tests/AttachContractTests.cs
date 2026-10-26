// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using TUnit.Core;
using Verify = CSharpContractAnalyzerVerifier;

public class AttachContractTests
{
	[Test]
	public async Task AttachRequiresAnnotatedInterface()
	{
		const string Source = /* lang=c#-test */ """
			using Nerdbank.JsonRpc;

			interface IUnannotated { }

			class C
			{
				void M(JsonRpc rpc) => {|#0:rpc.Attach<IUnannotated>()|};
			}
			""";

		await Verify.VerifyAsync(Source, Verify.Diagnostic("NBJSONRPC010", "IUnannotated").WithLocation(0));
	}

	[Test]
	[Arguments("rpc.Attach<string>()", "string")]
	[Arguments("rpc.Attach(typeof(string))", "string")]
	[Arguments("rpc.Attach(options: null, interfaceType: typeof(string))", "string")]
	[Arguments("rpc.Attach<IGeneric<int>>()", "IGeneric<int>")]
	public async Task AttachRequiresSupportedInterface(string attachExpression, string typeName)
	{
		string source = /* lang=c#-test */ $$"""
			using Nerdbank.JsonRpc;

			interface IGeneric<T> { }

			class C
			{
				void M(JsonRpc rpc) => {|#0:{{attachExpression}}|};
			}
			""";

		await Verify.VerifyAsync(source, Verify.Diagnostic("NBJSONRPC011", typeName).WithLocation(0));
	}

	[Test]
	public async Task AttachByTypeRequiresAnnotatedInterfaceWhenArgumentsAreReordered()
	{
		const string Source = /* lang=c#-test */ """
			using Nerdbank.JsonRpc;

			interface IUnannotated { }

			class C
			{
				void M(JsonRpc rpc) => {|#0:rpc.Attach(options: null, interfaceType: typeof(IUnannotated))|};
			}
			""";

		await Verify.VerifyAsync(Source, Verify.Diagnostic("NBJSONRPC010", "IUnannotated").WithLocation(0));
	}

	[Test]
	public async Task AttachAcceptsAnnotatedContractsAndMarshalableInterfaces()
	{
		const string Source = /* lang=c#-test */ """
			using System;
			using Nerdbank.JsonRpc;

			[JsonRpcContract]
			partial interface IContract { }

			[RpcMarshalable]
			partial interface IMarshalable : IDisposable { }

			class C
			{
				void M(JsonRpc rpc)
				{
					rpc.Attach<IContract>();
					rpc.Attach<IMarshalable>();
				}
			}
			""";

		await Verify.VerifyAsync(Source);
	}
}
