// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
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
}
