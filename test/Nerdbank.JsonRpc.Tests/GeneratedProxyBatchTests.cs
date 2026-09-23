// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.VisualStudio.Threading;

using ShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests;

public class GeneratedProxyBatchTests
{
	[Fact]
	public async Task GeneratedProxy_AttachesToBatch()
	{
		(MockChannel<JsonRpcMessage> transport, MockChannel<JsonRpcMessage> remote) = MockChannel<JsonRpcMessage>.CreatePair();
		JsonRpc clientRpc = new(transport);
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
		Assert.Equal(nameof(ICalculator.AddAsync), request.Method);
		Assert.Equal(nameof(ICalculator.SetLastValue), notification.Method);
		Assert.Null(notification.Id);

		JsonRpcMessageBatch responseBatch = new(
			[
				new JsonRpcResult
				{
					Id = request.Id!.Value,
					Result = (RawMessagePack)clientRpc.Serializer.Serialize(7, ShapeProvider.Default.Int32, cts.Token),
				},
			]);
		await remote.Writer.WriteAsync(responseBatch, cts.Token);

		Assert.Equal(7, await resultTask.WithCancellation(cts.Token));
	}
}
