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
		await using JsonRpcMessagePackChannel channel = new(local, LoggerFactory.CreateLogger<JsonRpcPipeChannel>());
		Assert.Equal(JsonRpcEncoding.MessagePack, channel.Encoding);
		Assert.Null(channel.SerializerPlugin);
		using JsonRpc rpc = new(channel) { Serializer = new Nerdbank.Json.JsonSerializer() };
		Assert.Throws<InvalidOperationException>(() => rpc.Start());
	}

	[Fact]
	public async Task OriginalChannelNameRemainsMessagePackCompatible()
	{
		(IDuplexPipe local, _) = FullDuplexStream.CreatePipePair();
#pragma warning disable CS0618 // Verify the original public name still works.
		await using StreamingJsonRpcMessageChannel channel = new(local, LoggerFactory.CreateLogger<JsonRpcPipeChannel>());
#pragma warning restore CS0618
		Assert.Equal(JsonRpcEncoding.MessagePack, channel.Encoding);
	}

	private static (JsonRpcPipeChannel Alice, JsonRpcPipeChannel Bob) CreateTransports()
	{
		(IDuplexPipe alice, IDuplexPipe bob) = FullDuplexStream.CreatePipePair();
		ILogger<JsonRpcPipeChannel> aliceLogger = LoggerFactory.CreateLogger<JsonRpcPipeChannel>();
		ILogger<JsonRpcPipeChannel> bobLogger = LoggerFactory.CreateLogger<JsonRpcPipeChannel>();
		return (new JsonRpcMessagePackChannel(alice, aliceLogger), new JsonRpcMessagePackChannel(bob, bobLogger));
	}
}
