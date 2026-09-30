// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;
using Nerdbank.MessagePack;
using Nerdbank.Streams;

public class OptionalInterfaceTests
{
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task AdvertisesOnlyImplementedCapabilities(JsonRpcEncoding encoding)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		serverRpc.AddRpcTarget<IOptionalInterfaceService>(new OptionalInterfaceService());
		serverRpc.Start();
		clientRpc.Start();
		IOptionalInterfaceService client = clientRpc.Attach<IOptionalInterfaceService>();

		IOptionalObject basic = await client.GetObjectAsync(0, CancellationToken.None);
		Assert.False(basic is ISubtractCapability);
		Assert.False(basic is IMultiplyCapability);

		IOptionalObject subtract = await client.GetObjectAsync(1, CancellationToken.None);
		ISubtractCapability subtractCapability = Assert.IsAssignableFrom<ISubtractCapability>(subtract);
		Assert.False(subtract is IMultiplyCapability);
		Assert.Equal(7, await subtractCapability.CalculateAsync(3, CancellationToken.None));

		IOptionalObject all = await client.GetObjectAsync(3, CancellationToken.None);
		Assert.Equal(7, await Assert.IsAssignableFrom<ISubtractCapability>(all).CalculateAsync(3, CancellationToken.None));
		Assert.Equal(30, await Assert.IsAssignableFrom<IMultiplyCapability>(all).CalculateAsync(3, CancellationToken.None));

		basic.Dispose();
		subtract.Dispose();
		all.Dispose();
	}

	[Test]
	public async Task UnknownCapabilitiesAreIgnored()
	{
		(MockChannel<JsonRpcMessage> transport, MockChannel<JsonRpcMessage> remote) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc rpc = new(new MockJsonRpcPipeChannel(transport));
		rpc.Start();
		IOptionalInterfaceService client = rpc.Attach<IOptionalInterfaceService>();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Task<IOptionalObject> resultTask = client.GetObjectAsync(0, cts.Token);
		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await remote.Reader.ReadAsync(cts.Token));

		using Sequence<byte> buffer = new();
		MessagePackWriter writer = new(buffer);
		writer.WriteMapHeader(4);
		writer.Write("__jsonrpc_marshaled");
		writer.Write(1);
		writer.Write("handle");
		writer.Write(1L);
		writer.Write("lifetime");
		writer.Write("explicit");
		writer.Write("optionalInterfaces");
		writer.WriteArrayHeader(2);
		writer.Write(-7);
		writer.Write(999);
		writer.Flush();
		await remote.Writer.WriteAsync(new JsonRpcResult { Id = request.Id!.Value, Result = (RawMessagePack)buffer.AsReadOnlySequence.ToArray() }, cts.Token);

		IOptionalObject result = await resultTask.WithCancellation(cts.Token);
		Assert.IsAssignableFrom<ISubtractCapability>(result);
		Assert.False(result is IMultiplyCapability);
		result.Dispose();
	}

	private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding)
		=> encoding == JsonRpcEncoding.Json
			? new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited)
			: new JsonRpcMessagePackChannel(pipe);
}
