// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;

[InheritsTests]
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
		Assert.NotSame(channel.Serializer, ((IJsonRpcClient)rpc).Serializer);
		Assert.NotSame(configured, Assert.IsType<MessagePackSerializerPlugin>(((IJsonRpcClient)rpc).Serializer).Serializer);
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
		Assert.NotSame(configured, Assert.IsType<MessagePackSerializerPlugin>(((IJsonRpcClient)client).Serializer).Serializer);
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

	[Test]
	public async Task DefaultFramingIsBigEndianLengthHeader()
	{
		Assert.Equal(JsonRpcMessagePackFraming.BigEndianInt32LengthHeader, JsonRpcMessagePackChannel.DefaultFraming);
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcMessagePackChannel channel = new(local, LoggerFactory.CreateLogger<JsonRpcPipeChannel>());
		await channel.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = "one" }, this.TimeoutToken);

		byte[] header = await ReadExactlyAsync(peer.Input, 4, this.TimeoutToken);
		int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header);
		byte[] body = await ReadExactlyAsync(peer.Input, length, this.TimeoutToken);
		Nerdbank.MessagePack.MessagePackReader reader = new(body);
		reader.Skip(default);
		Assert.True(reader.End);
	}

	[Test]
	public async Task SelfDelimitingFramingWritesNoHeader()
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcMessagePackChannel channel = CreateChannel(local, JsonRpcMessagePackFraming.SelfDelimiting);
		await channel.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = "one" }, this.TimeoutToken);

		byte[] first = await ReadExactlyAsync(peer.Input, 1, this.TimeoutToken);
		Assert.Equal(Nerdbank.MessagePack.MessagePackType.Map, Nerdbank.MessagePack.MessagePackCode.ToMessagePackType(first[0]));
	}

	[Test]
	public void UndefinedFramingIsRejected()
	{
		(IDuplexPipe local, _) = FullDuplexStream.CreatePipePair();
		Assert.Throws<ArgumentOutOfRangeException>(() => CreateChannel(local, (JsonRpcMessagePackFraming)99));
	}

	[Test]
	[Arguments(JsonRpcMessagePackFraming.SelfDelimiting)]
	[Arguments(JsonRpcMessagePackFraming.BigEndianInt32LengthHeader)]
	public async Task LargeMessagesRoundTrip(JsonRpcMessagePackFraming framing)
	{
		(IDuplexPipe alice, IDuplexPipe bob) = FullDuplexStream.CreatePipePair();
		await using JsonRpcMessagePackChannel aliceChannel = CreateChannel(alice, framing);
		await using JsonRpcMessagePackChannel bobChannel = CreateChannel(bob, framing);
		string method = new('m', 200_000);
		await aliceChannel.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = method }, this.TimeoutToken);
		await aliceChannel.Writer.WriteAsync(new JsonRpcRequest { Id = 2, Method = "small" }, this.TimeoutToken);
		Assert.Equal(method, Assert.IsType<JsonRpcRequest>(await bobChannel.Reader.ReadAsync(this.TimeoutToken)).Method);
		Assert.Equal("small", Assert.IsType<JsonRpcRequest>(await bobChannel.Reader.ReadAsync(this.TimeoutToken)).Method);
	}

	[Test]
	[Arguments(JsonRpcMessagePackFraming.SelfDelimiting, 8)]
	[Arguments(JsonRpcMessagePackFraming.BigEndianInt32LengthHeader, 2)]
	[Arguments(JsonRpcMessagePackFraming.BigEndianInt32LengthHeader, 8)]
	public async Task HandlesFragmentedAndCoalescedMessages(JsonRpcMessagePackFraming framing, int firstFragmentLength)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcMessagePackChannel channel = CreateChannel(local, framing);
		byte[] first = Frame(EncodeRequest("one", 1), framing);
		byte[] second = Frame(EncodeRequest("two", 2), framing);

		peer.Output.Write(first.AsSpan(0, firstFragmentLength));
		await peer.Output.FlushAsync(this.TimeoutToken);
		Task<JsonRpcMessage> pending = channel.Reader.ReadAsync(this.TimeoutToken).AsTask();
		Assert.False(pending.IsCompleted);

		peer.Output.Write(first.AsSpan(firstFragmentLength));
		peer.Output.Write(second);
		await peer.Output.FlushAsync(this.TimeoutToken);
		Assert.Equal("one", Assert.IsType<JsonRpcRequest>(await pending.WithCancellation(this.TimeoutToken)).Method);
		Assert.Equal("two", Assert.IsType<JsonRpcRequest>(await channel.Reader.ReadAsync(this.TimeoutToken)).Method);
	}

	[Test]
	[Arguments(JsonRpcMessagePackFraming.SelfDelimiting)]
	[Arguments(JsonRpcMessagePackFraming.BigEndianInt32LengthHeader)]
	public async Task RetainedArgumentsSurviveLaterMessagesReusingThePipeBuffer(JsonRpcMessagePackFraming framing)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcMessagePackChannel channel = CreateChannel(local, framing);
		peer.Output.Write(Frame(EncodeRequest("one", 1), framing));
		await peer.Output.FlushAsync(this.TimeoutToken);
		JsonRpcRequest first = Assert.IsType<JsonRpcRequest>(await channel.Reader.ReadAsync(this.TimeoutToken));

		peer.Output.Write(Frame(EncodeRequest("two", 2), framing));
		await peer.Output.FlushAsync(this.TimeoutToken);
		Assert.IsType<JsonRpcRequest>(await channel.Reader.ReadAsync(this.TimeoutToken));

		Nerdbank.MessagePack.MessagePackReader reader = new(first.Arguments.AsMessagePack());
		Assert.Equal(1, reader.ReadArrayHeader());
		Assert.Equal(1, reader.ReadInt32());
	}

	[Test]
	[Arguments(JsonRpcMessagePackFraming.SelfDelimiting, 1)]
	[Arguments(JsonRpcMessagePackFraming.BigEndianInt32LengthHeader, 1)]
	[Arguments(JsonRpcMessagePackFraming.BigEndianInt32LengthHeader, -2)]
	public async Task TruncatedMessageAtEndOfStreamFaultsTransport(JsonRpcMessagePackFraming framing, int bytesToWrite)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcMessagePackChannel channel = CreateChannel(local, framing);
		byte[] message = Frame(EncodeRequest("one", 1), framing);

		// A positive value truncates that many bytes from the end; a negative value writes only that many leading bytes.
		peer.Output.Write(bytesToWrite > 0 ? message.AsSpan(0, message.Length - bytesToWrite) : message.AsSpan(0, -bytesToWrite));
		await peer.Output.CompleteAsync();
		await Assert.ThrowsAsync<EndOfStreamException>(() => channel.Reader.Completion.WithCancellation(this.TimeoutToken));
	}

	[Test]
	[Arguments(0u)]
	[Arguments(0x80000000u)]
	public async Task InvalidLengthHeaderFaultsTransport(uint length)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcMessagePackChannel channel = CreateChannel(local, JsonRpcMessagePackFraming.BigEndianInt32LengthHeader);
		byte[] header = new byte[4];
		System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, length);
		peer.Output.Write(header);
		await peer.Output.FlushAsync(this.TimeoutToken);
		await Assert.ThrowsAsync<System.Net.ProtocolViolationException>(() => channel.Reader.Completion.WithCancellation(this.TimeoutToken));
	}

	private static JsonRpcMessagePackChannel CreateChannel(IDuplexPipe pipe, JsonRpcMessagePackFraming framing)
		=> new(pipe, LoggerFactory.CreateLogger<JsonRpcPipeChannel>(), JsonRpcMessagePackChannel.DefaultSerializer, framing);

	private static byte[] Frame(byte[] message, JsonRpcMessagePackFraming framing)
	{
		if (framing == JsonRpcMessagePackFraming.SelfDelimiting)
		{
			return message;
		}

		byte[] framed = new byte[4 + message.Length];
		System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(framed, message.Length);
		message.CopyTo(framed, 4);
		return framed;
	}

	private static async Task<byte[]> ReadExactlyAsync(PipeReader reader, int length, CancellationToken cancellationToken)
	{
		while (true)
		{
			ReadResult read = await reader.ReadAsync(cancellationToken);
			if (read.Buffer.Length >= length)
			{
				byte[] result = read.Buffer.Slice(0, length).ToArray();
				reader.AdvanceTo(read.Buffer.GetPosition(length));
				return result;
			}

			Assert.False(read.IsCompleted);
			reader.AdvanceTo(read.Buffer.Start, read.Buffer.End);
		}
	}

	private static byte[] EncodeRequest(string method, int argument)
	{
		Nerdbank.Streams.Sequence<byte> buffer = new();
		Nerdbank.MessagePack.MessagePackWriter writer = new(buffer);
		writer.WriteMapHeader(4);
		writer.Write("jsonrpc");
		writer.Write("2.0");
		writer.Write("method");
		writer.Write(method);
		writer.Write("id");
		writer.Write(argument);
		writer.Write("params");
		writer.WriteArrayHeader(1);
		writer.Write(argument);
		writer.Flush();
		return buffer.AsReadOnlySequence.ToArray();
	}

	private static (JsonRpcPipeChannel Alice, JsonRpcPipeChannel Bob) CreateTransports()
	{
		(IDuplexPipe alice, IDuplexPipe bob) = FullDuplexStream.CreatePipePair();
		ILogger<JsonRpcPipeChannel> aliceLogger = LoggerFactory.CreateLogger<JsonRpcPipeChannel>();
		ILogger<JsonRpcPipeChannel> bobLogger = LoggerFactory.CreateLogger<JsonRpcPipeChannel>();
		return (new JsonRpcMessagePackChannel(alice, aliceLogger), new JsonRpcMessagePackChannel(bob, bobLogger));
	}
}
