// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using TUnit.Core;
using Verify = CSharpContractAnalyzerVerifier;

public class UnsupportedContractMemberTests
{
	[Test]
	[Arguments("")]
	[Arguments("(GenerateProxy = false)")]
	public async Task PropertiesAreUnsupportedRegardlessOfProxyGeneration(string options)
	{
		string source = /* lang=c#-test */ $$"""
			using Nerdbank.JsonRpc;
			[JsonRpcContract{{options}}]
			partial interface IFoo { int {|#0:Count|} { get; } }
			""";

		await Verify.VerifyAsync(source, Verify.Diagnostic("NBJSONRPC003", "IFoo.Count", "properties and indexers are not supported; use asynchronous methods instead").WithLocation(0));
	}

	[Test]
	[Arguments("int Call();", "IFoo.Call()", "return type 'int' is not supported yet")]
	[Arguments("Task Call<T>();", "IFoo.Call<T>()", "generic methods are not supported yet")]
	[Arguments("Task Call(ref int value);", "IFoo.Call(ref int)", "ref, out, and in parameters are not supported yet")]
	[Arguments("Task Call(params int[] values);", "IFoo.Call(params int[])", "params parameters are not supported yet")]
	[Arguments("Task Call(CancellationToken token, int value);", "IFoo.Call(System.Threading.CancellationToken, int)", "CancellationToken parameters must appear last")]
	[Arguments("Task Call(CancellationToken token, CancellationToken other);", "IFoo.Call(System.Threading.CancellationToken, System.Threading.CancellationToken)", "CancellationToken parameters must appear last")]
	[Arguments("static abstract Task Call();", "IFoo.Call()", "static methods are not supported")]
	[Arguments("Task Call(Span<int> value);", "IFoo.Call(System.Span<int>)", "ref-like and pointer parameters are not supported")]
	public async Task SignaturesAreValidatedWithoutProxyGeneration(string member, string displayName, string reason)
	{
		string source = /* lang=c#-test */ $$"""
			using System;
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;
			[JsonRpcContract(GenerateProxy = false)]
			interface IFoo { {{member.Replace("Call", "{|#0:Call|}")}} }
			""";

		await Verify.VerifyAsync(source, Verify.Diagnostic("NBJSONRPC003", displayName, reason).WithLocation(0));
	}

	[Test]
	[Arguments("Func<int>")]
	[Arguments("Func<Task>")]
	public async Task EventDelegateIsValidatedWithoutProxyGeneration(string handler)
	{
		string source = /* lang=c#-test */ $$"""
			using System;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;
			[JsonRpcContract(GenerateProxy = false)]
			interface IFoo { event {{handler}} {|#0:Changed|}; }
			""";

		await Verify.VerifyAsync(source, Verify.Diagnostic("NBJSONRPC003", "IFoo.Changed", "proxy events must be instance events with synchronous, void-returning delegates and by-value, non-ref-like parameters").WithLocation(0));
	}

	[Test]
	public async Task SupportedMembersAndDefaultPropertiesAreAllowed()
	{
		const string Source = /* lang=c#-test */ """
			using System;
			using System.Collections.Generic;
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;
			[JsonRpcContract(GenerateProxy = false)]
			interface IFoo
			{
				int Count => 0;
				Task One();
				Task<int> Two(int value = 0, CancellationToken token = default);
				ValueTask Three();
				ValueTask<string> Four();
				IAsyncEnumerable<int> Five();
				void Notify();
				event Action Changed;
			}
			""";

		await Verify.VerifyAsync(Source);
	}
}
