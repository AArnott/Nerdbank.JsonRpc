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
		IClientProxy basicProxy = Assert.IsAssignableFrom<IClientProxy>(basic);
		Assert.True(basicProxy.Is(typeof(IOptionalObject)));
		Assert.True(basicProxy.Is(typeof(IDisposable)));
		Assert.Null(basicProxy.As<ISubtractCapability>());
		Assert.Null(basicProxy.As<IMultiplyCapability>());

		IOptionalObject subtract = await client.GetObjectAsync(1, CancellationToken.None);
		ISubtractCapability subtractCapability = Assert.IsAssignableFrom<ISubtractCapability>(subtract);
		Assert.True(subtract is IMultiplyCapability);
		Assert.Null(subtract.As<IMultiplyCapability>());
		Assert.True(subtract.Is(typeof(ISubtractCapability)));
		Assert.Equal(7, await subtractCapability.CalculateAsync(3, CancellationToken.None));

		IOptionalObject all = await client.GetObjectAsync(3, CancellationToken.None);
		IClientProxy allProxy = Assert.IsAssignableFrom<IClientProxy>(all);
		Assert.True(allProxy.Is(typeof(IOptionalObject)));
		Assert.Same(all, allProxy.As<ISubtractCapability>());
		Assert.Same(all, allProxy.As<IMultiplyCapability>());
		Assert.Equal(7, await Assert.IsAssignableFrom<ISubtractCapability>(all).CalculateAsync(3, CancellationToken.None));
		Assert.Equal(30, await Assert.IsAssignableFrom<IMultiplyCapability>(all).CalculateAsync(3, CancellationToken.None));

		basic.Dispose();
		subtract.Dispose();
		all.Dispose();
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ServerQueriesCapabilitiesOfMarshaledArguments(JsonRpcEncoding encoding)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		serverRpc.AddRpcTarget<IOptionalInterfaceService>(new OptionalInterfaceService());
		serverRpc.Start();
		clientRpc.Start();
		IOptionalInterfaceService client = clientRpc.Attach<IOptionalInterfaceService>();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

		Assert.False(await client.HasMultiplyCapabilityAsync(new OptionalObject(), cts.Token));
		Assert.True(await client.HasMultiplyCapabilityAsync(new AllOptionalObject(), cts.Token));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json, false)]
	[Arguments(JsonRpcEncoding.MessagePack, false)]
	[Arguments(JsonRpcEncoding.Json, true)]
	[Arguments(JsonRpcEncoding.MessagePack, true)]
	public async Task MarshaledProxyOptionsCanRestrictAdvertisedCapabilities(JsonRpcEncoding encoding, bool emptyList)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding), new JsonRpcOptions
		{
			MarshaledProxy = new JsonRpcProxyOptions { EnabledInterfaces = emptyList ? [] : [typeof(ISubtractCapability)] },
		});
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		serverRpc.AddRpcTarget<IOptionalInterfaceService>(new OptionalInterfaceService());
		serverRpc.Start();
		clientRpc.Start();
		IOptionalInterfaceService client = clientRpc.Attach<IOptionalInterfaceService>();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		using IOptionalObject all = await client.GetObjectAsync(3, cts.Token);
		IClientProxy proxy = Assert.IsAssignableFrom<IClientProxy>(all);

		Assert.True(all is IMultiplyCapability);
		Assert.True(proxy.Is(typeof(IOptionalObject)));
		Assert.True(proxy.Is(typeof(IDisposable)));
		Assert.Same(all, proxy.As<IOptionalObject>());
		Assert.Equal(!emptyList, proxy.As<ISubtractCapability>() is not null);
		Assert.Null(proxy.As<IMultiplyCapability>());
		Assert.Equal(30, await ((IMultiplyCapability)all).CalculateAsync(3, cts.Token));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ExplicitGroupsPreferExactMatchAndFallBackToSuperset(JsonRpcEncoding encoding)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		serverRpc.AddRpcTarget<IOptionalInterfaceService>(new OptionalInterfaceService());
		serverRpc.Start();
		clientRpc.Start();
		IOptionalInterfaceService client = clientRpc.Attach<IOptionalInterfaceService>();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		using IGroupedOptionalObject basic = await client.GetGroupedObjectAsync(0, cts.Token);
		Assert.False(basic is ISubtractCapability);
		Assert.False(basic is IMultiplyCapability);
		using IGroupedOptionalObject subtract = await client.GetGroupedObjectAsync(1, cts.Token);
		Assert.True(subtract is ISubtractCapability);
		Assert.False(subtract is IMultiplyCapability);
		Assert.Equal(7, await subtract.As<ISubtractCapability>()!.CalculateAsync(3, cts.Token));
		using IGroupedOptionalObject multiply = await client.GetGroupedObjectAsync(2, cts.Token);
		Assert.True(multiply is ISubtractCapability);
		Assert.True(multiply is IMultiplyCapability);
		Assert.False(multiply.Is(typeof(ISubtractCapability)));
		Assert.Null(multiply.As<ISubtractCapability>());
		Assert.Equal(30, await multiply.As<IMultiplyCapability>()!.CalculateAsync(3, cts.Token));
		using IGroupedOptionalObject all = await client.GetGroupedObjectAsync(3, cts.Token);
		Assert.Equal(7, await all.As<ISubtractCapability>()!.CalculateAsync(3, cts.Token));
		Assert.Equal(30, await all.As<IMultiplyCapability>()!.CalculateAsync(3, cts.Token));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task FullOnlyGroupCanRepresentObjectWithNoOptionalInterfaces(JsonRpcEncoding encoding)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		serverRpc.AddRpcTarget<IOptionalInterfaceService>(new OptionalInterfaceService());
		serverRpc.Start();
		clientRpc.Start();
		IOptionalInterfaceService client = clientRpc.Attach<IOptionalInterfaceService>();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		using IFullOnlyObject basic = await client.GetFullOnlyObjectAsync(false, cts.Token);
		Assert.True(basic is IMultiplyCapability);
		Assert.False(basic.Is(typeof(IMultiplyCapability)));
		Assert.Null(basic.As<IMultiplyCapability>());
		Assert.True(basic.Is(typeof(IDisposable)));
		using IFullOnlyObject multiply = await client.GetFullOnlyObjectAsync(true, cts.Token);
		Assert.Equal(30, await multiply.As<IMultiplyCapability>()!.CalculateAsync(3, cts.Token));
	}

	[Test]
	public async Task MissingCoveringGroupReportsActionableError()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, JsonRpcEncoding.Json));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, JsonRpcEncoding.Json));
		serverRpc.AddRpcTarget<IOptionalInterfaceService>(new OptionalInterfaceService());
		serverRpc.Start();
		clientRpc.Start();
		IOptionalInterfaceService client = clientRpc.Attach<IOptionalInterfaceService>();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Nerdbank.Json.JsonSerializationException exception = await Assert.ThrowsAsync<Nerdbank.Json.JsonSerializationException>(() => client.GetIncompleteObjectAsync(cts.Token));
		Assert.IsType<NotSupportedException>(exception.InnerException);
		Assert.Contains(nameof(JsonRpcProxyInterfaceGroupAttribute), exception.Message);
		Assert.Contains("42", exception.Message);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task RestrictionsPreserveSharedInheritedCapabilities(JsonRpcEncoding encoding)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding), new JsonRpcOptions
		{
			MarshaledProxy = new() { EnabledInterfaces = [typeof(IClientCapability)] },
		});
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		serverRpc.AddRpcTarget<IOptionalInterfaceService>(new OptionalInterfaceService());
		serverRpc.Start();
		clientRpc.Start();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		using ISiblingCapabilityObject proxy = await clientRpc.Attach<IOptionalInterfaceService>().GetSiblingObjectAsync(cts.Token);
		Assert.True(proxy.Is(typeof(ISiblingCapabilityObject)));
		Assert.True(proxy.Is(typeof(IDisposable)));
		Assert.Same(proxy, proxy.As<ISiblingCapabilityObject>());
		Assert.False(proxy.Is(typeof(IServerCapability)));
		Assert.False(proxy.Is(typeof(IClientCapability)));
		Assert.True(proxy.Is(typeof(ICommonCapability)));
		Assert.Equal(42, await proxy.As<ICommonCapability>()!.ReadAsync(cts.Token));
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
		Assert.True(result is IMultiplyCapability);
		Assert.Null(result.As<IMultiplyCapability>());
		result.Dispose();
	}

	private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding)
		=> encoding == JsonRpcEncoding.Json
			? new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited)
			: new JsonRpcMessagePackChannel(pipe);
}
