// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using Nerdbank.JsonRpc;
using Nerdbank.Streams;

public class JsonRpcMessagePackChannelTests() : JsonRpcPipeChannelTestBase(CreateTransports())
{
	[Test]
	public async Task MessagePackChannelDeclaresItsEncoding()
	{
		(IDuplexPipe local, _) = FullDuplexStream.CreatePipePair();
		Nerdbank.MessagePack.MessagePackSerializer configured = new();
		await using JsonRpcMessagePackChannel channel = new(local, LoggerFactory.CreateLogger<JsonRpcPipeChannel>(), serializer: configured);
		Assert.Equal(JsonRpcEncoding.MessagePack, channel.Encoding);
		Assert.Same(configured, Assert.IsType<MessagePackSerializerPlugin>(channel.Serializer).Serializer);
		using JsonRpc rpc = new(channel);
		Assert.Same(channel.Serializer, ((IJsonRpcClient)rpc).Serializer);
	}

	[Test]
	public async Task ConfiguredSerializerIsUsedForTypedRequestsAndEnvelopes()
	{
		(IDuplexPipe local, IDuplexPipe remote) = FullDuplexStream.CreatePipePair();
		Nerdbank.MessagePack.MessagePackSerializer configured = new() { InternStrings = false };
		await using JsonRpcMessagePackChannel clientChannel = new(local, LoggerFactory.CreateLogger<JsonRpcPipeChannel>(), serializer: configured);
		await using JsonRpcMessagePackChannel serverChannel = new(remote, LoggerFactory.CreateLogger<JsonRpcPipeChannel>());
		using JsonRpc client = new(clientChannel);
		client.Start();

		JsonRpcValue arguments;
		using (JsonRpcArgumentsBuilder builder = client.CreateArguments(false, 1, TestContext.Current!.Execution.CancellationToken))
		{
			builder.Add(null, 42, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
			arguments = builder.Build();
		}

		await client.NotifyAsync("example", arguments, TestContext.Current!.Execution.CancellationToken);
		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await serverChannel.Reader.ReadAsync(TestContext.Current!.Execution.CancellationToken));
		Nerdbank.MessagePack.MessagePackReader reader = new(request.Arguments.AsMessagePack());
		Assert.Equal(1, reader.ReadArrayHeader());
		Assert.Equal(42, reader.ReadInt32());
		Assert.True(reader.End);
		Assert.Same(configured, Assert.IsType<MessagePackSerializerPlugin>(((IJsonRpcClient)client).Serializer).Serializer);
	}

	[Test]
	[Arguments(0, "A batch must not be empty.")]
	[Arguments(1, "A batch cannot contain nested batches.")]
	[Arguments(2, "JSON-RPC params must be an array or object.")]
	public async Task DirectWriterRejectsInvalidMessages(int caseNumber, string expectedMessage)
	{
		(IDuplexPipe local, _) = FullDuplexStream.CreatePipePair();
		await using JsonRpcMessagePackChannel channel = new(local, LoggerFactory.CreateLogger<JsonRpcPipeChannel>());
		JsonRpcMessage message = caseNumber switch
		{
			0 => new JsonRpcMessageBatch([]),
			1 => new JsonRpcMessageBatch([new JsonRpcMessageBatch([new JsonRpcRequest { Method = "method" }])]),
			_ => new JsonRpcRequest { Method = "method", Arguments = JsonRpcValue.FromMessagePack((RawMessagePack)new byte[] { 42 }) },
		};
		await channel.Writer.WriteAsync(message, this.TimeoutToken);
		ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() => channel.Reader.Completion.WithCancellation(this.TimeoutToken));
		Assert.StartsWith(expectedMessage, error.Message);
	}

	[Test]
	public async Task NullSerializerDoesNotStartTransport()
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		Assert.Throws<ArgumentNullException>(() => new JsonRpcMessagePackChannel(local, LoggerFactory.CreateLogger<JsonRpcPipeChannel>(), serializer: null!));
		peer.Output.Write("still available"u8.ToArray());
		await peer.Output.FlushAsync(this.TimeoutToken);
		ReadResult read = await local.Input.ReadAsync(this.TimeoutToken);
		Assert.Equal("still available", System.Text.Encoding.UTF8.GetString(read.Buffer.ToArray()));
		local.Input.AdvanceTo(read.Buffer.End);
	}

	private static (JsonRpcPipeChannel Alice, JsonRpcPipeChannel Bob) CreateTransports()
	{
		(IDuplexPipe alice, IDuplexPipe bob) = FullDuplexStream.CreatePipePair();
		ILogger<JsonRpcPipeChannel> aliceLogger = LoggerFactory.CreateLogger<JsonRpcPipeChannel>();
		ILogger<JsonRpcPipeChannel> bobLogger = LoggerFactory.CreateLogger<JsonRpcPipeChannel>();
		return (new JsonRpcMessagePackChannel(alice, aliceLogger), new JsonRpcMessagePackChannel(bob, bobLogger));
	}
}
