// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Nerdbank.MessagePack;
using Nerdbank.Streams;

public class StreamingJsonRpcBatchChannelTests : TestBase
{
	[Fact]
	public async Task SendAndReceiveBatchPayload()
	{
		(IDuplexPipe alicePipe, IDuplexPipe bobPipe) = FullDuplexStream.CreatePipePair();
		StreamingJsonRpcMessageChannel alice = new(alicePipe, NullLogger.Instance);
		StreamingJsonRpcMessageChannel bob = new(bobPipe, NullLogger.Instance);
		JsonRpcMessageBatch sent = new(
		[
			new JsonRpcRequest { Id = 1, Method = "testMethod" },
			new JsonRpcResult { Id = 2, Result = NilMsgPack },
		]);

		await alice.Writer.WriteAsync(sent, this.TimeoutToken);

		JsonRpcMessageBatch recv = Assert.IsAssignableFrom<JsonRpcMessageBatch>(await bob.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(2, recv.Messages.Length);
		Assert.Equal("testMethod", Assert.IsType<JsonRpcRequest>(recv.Messages[0]).Method);
		Assert.Equal((RequestId)2, Assert.IsType<JsonRpcResult>(recv.Messages[1]).Id);
	}

	[Fact]
	public async Task ReceiveNestedBatchPayloadMarksEntryInvalid()
	{
		(IDuplexPipe alicePipe, IDuplexPipe bobPipe) = FullDuplexStream.CreatePipePair();
		StreamingJsonRpcMessageChannel alice = new(alicePipe, NullLogger.Instance);
		StreamingJsonRpcMessageChannel bob = new(bobPipe, NullLogger.Instance);
		JsonRpcMessageBatch sent = new(
		[
			new JsonRpcMessageBatch(
			[
				new JsonRpcRequest { Id = 1, Method = "testMethod" },
			]),
		]);

		await alice.Writer.WriteAsync(sent, this.TimeoutToken);

		JsonRpcMessage invalid = await bob.Reader.ReadAsync(this.TimeoutToken);
		Assert.Equal("JsonRpcInvalidMessage", invalid.GetType().Name);
	}

	[Fact]
	public async Task InvalidPayloadLoggingDoesNotFaultChannel()
	{
		(IDuplexPipe alicePipe, IDuplexPipe bobPipe) = FullDuplexStream.CreatePipePair();
		StreamingJsonRpcMessageChannel bob = new(bobPipe, NullLogger.Instance);

		MessagePackWriter writer = new(alicePipe.Output);
		writer.Write(42);
		writer.Flush();
		await alicePipe.Output.FlushAsync(this.TimeoutToken);

		JsonRpcMessage invalid = await bob.Reader.ReadAsync(this.TimeoutToken);
		Assert.Equal("JsonRpcInvalidMessage", invalid.GetType().Name);
	}
}
