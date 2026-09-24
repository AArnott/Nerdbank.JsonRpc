// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Nerdbank.JsonRpc;
using Nerdbank.Streams;

public class JsonRpcMessagePackChannelTests() : JsonRpcPipeChannelTestBase(CreateTransports())
{
	[Fact]
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

	[Fact]
	public async Task ConfiguredSerializerIsUsedForTypedRequestsAndEnvelopes()
	{
		(IDuplexPipe local, IDuplexPipe remote) = FullDuplexStream.CreatePipePair();
		Nerdbank.MessagePack.MessagePackSerializer configured = new() { InternStrings = false };
		await using JsonRpcMessagePackChannel clientChannel = new(local, LoggerFactory.CreateLogger<JsonRpcPipeChannel>(), serializer: configured);
		await using JsonRpcMessagePackChannel serverChannel = new(remote, LoggerFactory.CreateLogger<JsonRpcPipeChannel>());
		using JsonRpc client = new(clientChannel);
		client.Start();

		JsonRpcValue arguments;
		using (JsonRpcArgumentsBuilder builder = client.CreateArguments(false, 1, TestContext.Current.CancellationToken))
		{
			builder.Add(null, 42, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
			arguments = builder.Build();
		}

		await client.NotifyAsync("example", arguments, TestContext.Current.CancellationToken);
		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await serverChannel.Reader.ReadAsync(TestContext.Current.CancellationToken));
		Nerdbank.MessagePack.MessagePackReader reader = new(request.Arguments.AsMessagePack());
		Assert.Equal(1, reader.ReadArrayHeader());
		Assert.Equal(42, reader.ReadInt32());
		Assert.True(reader.End);
		Assert.Same(configured, Assert.IsType<MessagePackSerializerPlugin>(((IJsonRpcClient)client).Serializer).Serializer);
	}

	[Fact]
	public async Task OriginalChannelNameRemainsMessagePackCompatible()
	{
		(IDuplexPipe local, _) = FullDuplexStream.CreatePipePair();
#pragma warning disable CS0618 // Verify the original public name still works.
		await using StreamingJsonRpcMessageChannel channel = new(local, LoggerFactory.CreateLogger<JsonRpcPipeChannel>());
#pragma warning restore CS0618
		Assert.Equal(JsonRpcEncoding.MessagePack, channel.Encoding);
		Assert.Same(JsonRpcMessagePackChannel.DefaultSerializer, Assert.IsType<MessagePackSerializerPlugin>(channel.Serializer).Serializer);
	}

	private static (JsonRpcPipeChannel Alice, JsonRpcPipeChannel Bob) CreateTransports()
	{
		(IDuplexPipe alice, IDuplexPipe bob) = FullDuplexStream.CreatePipePair();
		ILogger<JsonRpcPipeChannel> aliceLogger = LoggerFactory.CreateLogger<JsonRpcPipeChannel>();
		ILogger<JsonRpcPipeChannel> bobLogger = LoggerFactory.CreateLogger<JsonRpcPipeChannel>();
		return (new JsonRpcMessagePackChannel(alice, aliceLogger), new JsonRpcMessagePackChannel(bob, bobLogger));
	}
}
