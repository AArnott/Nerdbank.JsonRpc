// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;
using Nerdbank.MessagePack;
using Nerdbank.Streams;

public class StreamingJsonRpcBatchChannelTests : TestBase
{
	[Test]
	public async Task ExplicitNilIdIsNotANotification()
	{
		(IDuplexPipe alicePipe, IDuplexPipe bobPipe) = FullDuplexStream.CreatePipePair();
		JsonRpcMessagePackChannel alice = new(alicePipe, NullLogger.Instance);
		JsonRpcMessagePackChannel bob = new(bobPipe, NullLogger.Instance);
		await alice.Writer.WriteAsync(new JsonRpcRequest { Id = default(RequestId), Method = "testMethod" }, this.TimeoutToken);

		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await bob.Reader.ReadAsync(this.TimeoutToken));
		Assert.True(request.HasId);
		Assert.Equal(default(RequestId), request.Id);
	}

	[Test]
	public async Task OmittedAndPresentMessagePackParamsRemainDistinct()
	{
		(IDuplexPipe alicePipe, IDuplexPipe bobPipe) = FullDuplexStream.CreatePipePair();
		await using JsonRpcMessagePackChannel alice = new(alicePipe, NullLogger.Instance);
		await using JsonRpcMessagePackChannel bob = new(bobPipe, NullLogger.Instance);
		await alice.Writer.WriteAsync(new JsonRpcRequest { Method = "testMethod" }, this.TimeoutToken);
		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await bob.Reader.ReadAsync(this.TimeoutToken));
		Assert.False(request.Arguments.HasValue);

		JsonRpcValue emptyMap = JsonRpcValue.FromMessagePack((RawMessagePack)new byte[] { 0x80 });
		await alice.Writer.WriteAsync(new JsonRpcRequest { Method = "testMethod", Arguments = emptyMap }, this.TimeoutToken);
		request = Assert.IsType<JsonRpcRequest>(await bob.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(emptyMap, request.Arguments);
	}

	[Test]
	public async Task SendAndReceiveBatchPayload()
	{
		(IDuplexPipe alicePipe, IDuplexPipe bobPipe) = FullDuplexStream.CreatePipePair();
		JsonRpcMessagePackChannel alice = new(alicePipe, NullLogger.Instance);
		JsonRpcMessagePackChannel bob = new(bobPipe, NullLogger.Instance);
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

	[Test]
	public async Task ReceiveNestedBatchPayloadClosesChannel()
	{
		(IDuplexPipe alicePipe, IDuplexPipe bobPipe) = FullDuplexStream.CreatePipePair();
		await using JsonRpcMessagePackChannel bob = new(bobPipe, NullLogger.Instance);
		MessagePackWriter writer = new(alicePipe.Output);
		writer.WriteArrayHeader(1);
		writer.WriteArrayHeader(1);
		writer.WriteMapHeader(2);
		writer.Write("jsonrpc");
		writer.Write("2.0");
		writer.Write("method");
		writer.Write("testMethod");
		writer.Flush();
		await alicePipe.Output.FlushAsync(this.TimeoutToken);

		await Assert.ThrowsAsync<System.Net.ProtocolViolationException>(() => bob.Reader.Completion.WithCancellation(this.TimeoutToken));
	}

	[Test]
	public async Task NilParamsClosesChannel()
	{
		(IDuplexPipe alicePipe, IDuplexPipe bobPipe) = FullDuplexStream.CreatePipePair();
		JsonRpcMessagePackChannel bob = new(bobPipe, NullLogger.Instance);

		MessagePackWriter writer = new(alicePipe.Output);
		writer.WriteMapHeader(3);
		writer.Write("jsonrpc");
		writer.Write("2.0");
		writer.Write("method");
		writer.Write("testMethod");
		writer.Write("params");
		writer.WriteNil();
		writer.Flush();
		await alicePipe.Output.FlushAsync(this.TimeoutToken);

		await Assert.ThrowsAsync<System.Net.ProtocolViolationException>(() => bob.Reader.Completion.WithCancellation(this.TimeoutToken));
	}

	[Test]
	public async Task MissingErrorFieldsCloseChannel()
	{
		(IDuplexPipe alicePipe, IDuplexPipe bobPipe) = FullDuplexStream.CreatePipePair();
		await using JsonRpcMessagePackChannel bob = new(bobPipe, NullLogger.Instance);

		MessagePackWriter writer = new(alicePipe.Output);
		writer.WriteMapHeader(3);
		writer.Write("jsonrpc");
		writer.Write("2.0");
		writer.Write("id");
		writer.Write(1);
		writer.Write("error");
		writer.WriteMapHeader(0);
		writer.Flush();
		await alicePipe.Output.FlushAsync(this.TimeoutToken);

		await Assert.ThrowsAsync<System.Net.ProtocolViolationException>(() => bob.Reader.Completion.WithCancellation(this.TimeoutToken));
	}

	[Test]
	public async Task InvalidPayloadClosesChannel()
	{
		(IDuplexPipe alicePipe, IDuplexPipe bobPipe) = FullDuplexStream.CreatePipePair();
		JsonRpcMessagePackChannel bob = new(bobPipe, NullLogger.Instance);

		MessagePackWriter writer = new(alicePipe.Output);
		writer.Write(42);
		writer.Flush();
		await alicePipe.Output.FlushAsync(this.TimeoutToken);

		await Assert.ThrowsAsync<System.Net.ProtocolViolationException>(() => bob.Reader.Completion.WithCancellation(this.TimeoutToken));
	}
}
