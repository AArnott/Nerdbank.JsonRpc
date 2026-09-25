// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.VisualStudio.Threading;

using ShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests;

public class GeneratedProxyBatchTests
{
	[Test]
	public async Task GeneratedProxy_BatchUsesAttachmentOptions()
	{
		(MockChannel<JsonRpcMessage> transport, MockChannel<JsonRpcMessage> remote) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc rpc = new(new MockJsonRpcPipeChannel(transport));
		rpc.Start();
		using JsonRpcBatch batch = rpc.CreateBatch();
		IPositionalCalculator positional = batch.Attach<IPositionalCalculator>();
		IPositionalCalculator named = batch.Attach<IPositionalCalculator>(new JsonRpcProxyOptions { UseNamedArguments = true });

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Task<int> firstResult = positional.SubtractAsync(9, 4, cts.Token).AsTask();
		Task<int> secondResult = named.SubtractAsync(8, 3, cts.Token).AsTask();
		await batch.SendAsync(cts.Token);
		JsonRpcMessageBatch requests = Assert.IsType<JsonRpcMessageBatch>(await remote.Reader.ReadAsync(cts.Token));
		Assert.Equal(2, requests.Messages.Length);
		JsonRpcRequest first = Assert.IsType<JsonRpcRequest>(requests.Messages[0]);
		JsonRpcRequest second = Assert.IsType<JsonRpcRequest>(requests.Messages[1]);
		MessagePackReader firstReader = new(first.Arguments.AsMessagePack());
		Assert.Equal(2, firstReader.ReadArrayHeader());
		Assert.Equal(9, firstReader.ReadInt32());
		Assert.Equal(4, firstReader.ReadInt32());
		MessagePackReader secondReader = new(second.Arguments.AsMessagePack());
		Assert.Equal(2, secondReader.ReadMapHeader());
		Assert.Equal("a", secondReader.ReadString());
		Assert.Equal(8, secondReader.ReadInt32());
		Assert.Equal("b", secondReader.ReadString());
		Assert.Equal(3, secondReader.ReadInt32());

		JsonRpcMessageBatch responses = new(
			[
				new JsonRpcResult { Id = first.Id!.Value, Result = ((IJsonRpcClient)rpc).Serializer.Serialize(5, ShapeProvider.Default.Int32, cts.Token) },
				new JsonRpcResult { Id = second.Id!.Value, Result = ((IJsonRpcClient)rpc).Serializer.Serialize(5, ShapeProvider.Default.Int32, cts.Token) },
			]);
		await remote.Writer.WriteAsync(responses, cts.Token);
		Assert.Equal(5, await firstResult.WithCancellation(cts.Token));
		Assert.Equal(5, await secondResult.WithCancellation(cts.Token));
	}

	[Test]
	public async Task GeneratedProxy_AttachesToBatch()
	{
		(MockChannel<JsonRpcMessage> transport, MockChannel<JsonRpcMessage> remote) = MockChannel<JsonRpcMessage>.CreatePair();
		JsonRpc clientRpc = new(new MockJsonRpcPipeChannel(transport));
		clientRpc.Start();
		JsonRpcBatch batch = clientRpc.CreateBatch();
		ICalculator client = batch.Attach<ICalculator>();

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Task<int> resultTask = client.AddAsync(2, 5, cts.Token).AsTask();
		client.SetLastValue(7, cts.Token);
		await batch.SendAsync(cts.Token);

		JsonRpcMessageBatch requestBatch = Assert.IsType<JsonRpcMessageBatch>(await remote.Reader.ReadAsync(cts.Token));
		Assert.Equal(2, requestBatch.Messages.Length);
		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(requestBatch.Messages[0]);
		JsonRpcRequest notification = Assert.IsType<JsonRpcRequest>(requestBatch.Messages[1]);
		Assert.Equal(CommonMethodNameTransforms.Default(nameof(ICalculator.AddAsync)), request.Method);
		Assert.Equal(CommonMethodNameTransforms.Default(nameof(ICalculator.SetLastValue)), notification.Method);
		Assert.Null(notification.Id);

		JsonRpcMessageBatch responseBatch = new(
			[
				new JsonRpcResult
				{
					Id = request.Id!.Value,
					Result = (RawMessagePack)((IJsonRpcClient)clientRpc).Serializer.Serialize(7, ShapeProvider.Default.Int32, cts.Token),
				},
			]);
		await remote.Writer.WriteAsync(responseBatch, cts.Token);

		Assert.Equal(7, await resultTask.WithCancellation(cts.Token));
	}
}
