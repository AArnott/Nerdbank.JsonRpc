// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable NBMsgPack051 // Prefer modern .NET APIs - Remove this when https://github.com/AArnott/Nerdbank.MessagePack/pull/771 merges

using Nerdbank.JsonRpc;
using Nerdbank.MessagePack;
using Nerdbank.Streams;
using Xunit;

public abstract class JsonRpcPipeChannelTestBase((JsonRpcPipeChannel Alice, JsonRpcPipeChannel Bob) pair) : TestBase
{
	[Fact]
	public async Task SendAndReceiveOneRequest()
	{
		JsonRpcRequest sent = new() { Id = 1, Method = "testMethod" };
		await pair.Alice.Writer.WriteAsync(sent, this.TimeoutToken);

		JsonRpcRequest recv = Assert.IsAssignableFrom<JsonRpcRequest>(await pair.Bob.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(sent.Method, recv.Method);
	}

	[Fact]
	public async Task SendAndReceiveOneResult()
	{
		Sequence<byte> seq = new();
		MessagePackWriter msgpackWriter = new(seq);
		msgpackWriter.WriteNil();
		msgpackWriter.Flush();

		JsonRpcResult sent = new() { Id = 1, Result = (RawMessagePack)seq.AsReadOnlySequence };
		await pair.Alice.Writer.WriteAsync(sent, this.TimeoutToken);

		JsonRpcResult recv = Assert.IsAssignableFrom<JsonRpcResult>(await pair.Bob.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(sent.Result, recv.Result);
	}

	[Fact]
	public async Task SendAndReceiveOneError()
	{
		Sequence<byte> seq = new();
		MessagePackWriter msgpackWriter = new(seq);
		msgpackWriter.WriteNil();
		msgpackWriter.Flush();

		JsonRpcError sent = new() { Id = 1, Error = new JsonRpcErrorDetails { Code = 123, Message = "msg" } };
		await pair.Alice.Writer.WriteAsync(sent, this.TimeoutToken);

		JsonRpcError recv = Assert.IsAssignableFrom<JsonRpcError>(await pair.Bob.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(sent.Error.Message, recv.Error.Message);
	}

	[Fact]
	public async Task SendAndReceiveMultipleMessages()
	{
		JsonRpcRequest sent1 = new() { Id = 1, Method = "testMethod" };
		JsonRpcRequest sent2 = new() { Id = 2, Method = "testMethod2" };
		await pair.Alice.Writer.WriteAsync(sent1, this.TimeoutToken);
		await pair.Alice.Writer.WriteAsync(sent2, this.TimeoutToken);

		JsonRpcRequest recv1 = Assert.IsAssignableFrom<JsonRpcRequest>(await pair.Bob.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(sent1.Method, recv1.Method);

		JsonRpcRequest recv2 = Assert.IsAssignableFrom<JsonRpcRequest>(await pair.Bob.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(sent2.Method, recv2.Method);
	}
}
