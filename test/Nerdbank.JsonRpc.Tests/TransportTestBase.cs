// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable NBMsgPack051 // Prefer modern .NET APIs - Remove this when https://github.com/AArnott/Nerdbank.MessagePack/pull/771 merges

using Nerdbank.JsonRpc;
using Nerdbank.MessagePack;
using Xunit;

public abstract class TransportTestBase((JsonRpcTransport Alice, JsonRpcTransport Bob) pair) : TestBase
{
	[Fact(Skip = "Does not yet pass.")]
	public async Task SendAndReceiveOne()
	{
		JsonRpcRequest sent = new() { Id = 1, Method = "testMethod" };
		await pair.Alice.Writer.WriteAsync(sent, this.TimeoutToken);

		JsonRpcRequest recv = Assert.IsAssignableFrom<JsonRpcRequest>(await pair.Bob.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(sent, recv, StructuralEqualityComparer.GetDefaultSourceGenerated<JsonRpcRequest>());
	}
}
