// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Nerdbank.JsonRpc;
using Nerdbank.MessagePack;
using Nerdbank.Streams;
using Xunit;

using ShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_SourceGeneration_Tests;

public class GeneratedProxyTests
{
	[Fact]
	public async Task GeneratedProxy_SupportsRequestsAndNotifications()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();

		StreamingJsonRpcMessageChannel clientChannel = new(clientPipe, NullLogger.Instance);
		StreamingJsonRpcMessageChannel serverChannel = new(serverPipe, NullLogger.Instance);

		JsonRpc clientRpc = new(clientChannel);
		clientRpc.Start();
		CalculatorProxy client = new(clientRpc, ShapeProvider.Default);

		Calculator server = new();
		JsonRpc serverRpc = new(serverChannel);
		serverRpc.AddRpcTarget<ICalculator>(server);
		serverRpc.Start();

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		int sum = await client.AddAsync(1, 3, cts.Token);
		Assert.Equal(4, sum);

		int product = await client.MultiplyAsync(2, 5, cts.Token);
		Assert.Equal(10, product);

		await client.PingAsync(cts.Token);
		await client.PingTaskAsync(cts.Token);
		Assert.Equal(2, server.PingCount);

		client.SetLastValue(7, cts.Token);
		int notificationValue = await server.NotificationReceived.Task.WaitAsync(cts.Token);
		Assert.Equal(7, notificationValue);
	}

	[Fact]
	public async Task GeneratedProxy_CanPackArgumentsPositionally()
	{
		(MockChannel<JsonRpcMessage> transport, MockChannel<JsonRpcMessage> remote) = MockChannel<JsonRpcMessage>.CreatePair();
		JsonRpc clientRpc = new(transport);
		clientRpc.Start();
		PositionalCalculatorProxy client = new(clientRpc, ShapeProvider.Default);

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Task<int> resultTask = client.SubtractAsync(9, 4, cts.Token).AsTask();

		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await remote.Reader.ReadAsync(cts.Token));
		Assert.Equal(nameof(IPositionalCalculator.SubtractAsync), request.Method);
		Assert.NotNull(request.Id);

		MessagePackReader reader = new(request.Arguments);
		Assert.Equal(MessagePackType.Array, reader.NextMessagePackType);
		Assert.Equal(2, reader.ReadArrayHeader());
		Assert.Equal(9, reader.ReadInt32());
		Assert.Equal(4, reader.ReadInt32());

		JsonRpcResult response = new()
		{
			Id = request.Id!.Value,
			Result = (RawMessagePack)clientRpc.Serializer.Serialize(5, ShapeProvider.Default.Int32, cts.Token),
		};
		await remote.Writer.WriteAsync(response, cts.Token);

		Assert.Equal(5, await resultTask.WaitAsync(cts.Token));
	}

	[Fact]
	public async Task GeneratedProxy_CanPackArgumentsByNameWhenRequested()
	{
		(MockChannel<JsonRpcMessage> transport, MockChannel<JsonRpcMessage> remote) = MockChannel<JsonRpcMessage>.CreatePair();
		JsonRpc clientRpc = new(transport);
		clientRpc.Start();
		NamedCalculatorProxy client = new(clientRpc, ShapeProvider.Default);

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Task<int> resultTask = client.SubtractAsync(9, 4, cts.Token).AsTask();

		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await remote.Reader.ReadAsync(cts.Token));
		Assert.Equal(nameof(INamedCalculator.SubtractAsync), request.Method);
		Assert.NotNull(request.Id);

		MessagePackReader reader = new(request.Arguments);
		Assert.Equal(MessagePackType.Map, reader.NextMessagePackType);
		Assert.Equal(2, reader.ReadMapHeader());
		Assert.Equal("a", reader.ReadString());
		Assert.Equal(9, reader.ReadInt32());
		Assert.Equal("b", reader.ReadString());
		Assert.Equal(4, reader.ReadInt32());

		JsonRpcResult response = new()
		{
			Id = request.Id!.Value,
			Result = (RawMessagePack)clientRpc.Serializer.Serialize(5, ShapeProvider.Default.Int32, cts.Token),
		};
		await remote.Writer.WriteAsync(response, cts.Token);

		Assert.Equal(5, await resultTask.WaitAsync(cts.Token));
	}
}
