// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;

using ShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests;

public class GeneratedProxyTests
{
	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task GeneratedProxy_SupportsRequestsAndNotifications(bool useNamedArguments)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();

		JsonRpcMessagePackChannel clientChannel = new(clientPipe, NullLogger.Instance);
		JsonRpcMessagePackChannel serverChannel = new(serverPipe, NullLogger.Instance);

		JsonRpc clientRpc = new(clientChannel);
		clientRpc.Start();
		ICalculator client = clientRpc.Attach<ICalculator>(new JsonRpcProxyOptions { UseNamedArguments = useNamedArguments });

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
		int notificationValue = await server.NotificationReceived.Task.WithCancellation(cts.Token);
		Assert.Equal(7, notificationValue);
	}

	[Test]
	public async Task GeneratedProxy_MarshalsDisposableArguments()
	{
		(MockChannel<JsonRpcMessage> transport, MockChannel<JsonRpcMessage> remote) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc rpc = new(new MockJsonRpcPipeChannel(transport));
		rpc.Start();
		IDisposableContract client = rpc.Attach<IDisposableContract>();

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Task requestTask = client.UseDisposableAsync(new TestDisposable(), cts.Token);
		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await remote.Reader.ReadAsync(cts.Token));
		MessagePackReader reader = new(request.Arguments.AsMessagePack());
		Assert.Equal(1, reader.ReadArrayHeader());
		Assert.Equal(MessagePackType.Map, reader.NextMessagePackType);
		int propertyCount = reader.ReadMapHeader();
		Dictionary<string, object?> properties = [];
		for (int i = 0; i < propertyCount; i++)
		{
			string key = reader.ReadString()!;
			properties[key] = key == "__jsonrpc_marshaled" ? reader.ReadInt32() : key == "handle" ? reader.ReadInt64() : reader.ReadString();
		}

		Assert.Equal(1, properties["__jsonrpc_marshaled"]);
		Assert.IsType<long>(properties["handle"]);
		Assert.Equal("explicit", properties["lifetime"]);
	}

	[Test]
	[Arguments(JsonRpcEncoding.MessagePack)]
	[Arguments(JsonRpcEncoding.Json)]
	public async Task GeneratedProxy_MarshalsDisposableParameterEndToEnd(JsonRpcEncoding encoding)
	{
		NativeAotTestHelper.SkipNerdbankJsonOnNativeAot(encoding);
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		DisposableTarget target = new();
		serverRpc.AddRpcTarget<IDisposableContract>(target);
		serverRpc.Start();
		clientRpc.Start();
		IDisposableContract client = clientRpc.Attach<IDisposableContract>();
		TestDisposable disposable = new();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

		await client.UseDisposableAsync(disposable, cts.Token);

		await disposable.Disposed.WithCancellation(cts.Token);
		Assert.True(disposable.IsDisposed);
	}

	[Test]
	[Arguments(JsonRpcEncoding.MessagePack)]
	[Arguments(JsonRpcEncoding.Json)]
	public async Task GeneratedProxy_MarshalsDisposableReturnValueEndToEnd(JsonRpcEncoding encoding)
	{
		NativeAotTestHelper.SkipNerdbankJsonOnNativeAot(encoding);
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		DisposableTarget target = new();
		serverRpc.AddRpcTarget<IDisposableContract>(target);
		serverRpc.Start();
		clientRpc.Start();
		IDisposableContract client = clientRpc.Attach<IDisposableContract>();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

		IDisposable disposable = await client.GetDisposableAsync(cts.Token);
		disposable.Dispose();

		await target.ReturnedDisposable.Disposed.WithCancellation(cts.Token);
		Assert.True(target.ReturnedDisposable.IsDisposed);
	}

	[Test]
	[Arguments(JsonRpcEncoding.MessagePack)]
	[Arguments(JsonRpcEncoding.Json)]
	public async Task GeneratedProxy_PreservesRemoteDisposableWhenSentBack(JsonRpcEncoding encoding)
	{
		NativeAotTestHelper.SkipNerdbankJsonOnNativeAot(encoding);
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		DisposableTarget target = new();
		serverRpc.AddRpcTarget<IDisposableContract>(target);
		serverRpc.Start();
		clientRpc.Start();
		IDisposableContract client = clientRpc.Attach<IDisposableContract>();
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		IDisposable disposable = await client.GetDisposableAsync(cts.Token);

		Assert.True(await client.IsReturnedDisposableAsync(disposable, cts.Token));
	}

	[Test]
	[Arguments(JsonRpcEncoding.MessagePack)]
	[Arguments(JsonRpcEncoding.Json)]
	public async Task GeneratedProxy_MarshalsDisposablePropertyEndToEnd(JsonRpcEncoding encoding)
	{
		NativeAotTestHelper.SkipNerdbankJsonOnNativeAot(encoding);
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		DisposableTarget target = new();
		serverRpc.AddRpcTarget<IDisposableContract>(target);
		serverRpc.Start();
		clientRpc.Start();
		IDisposableContract client = clientRpc.Attach<IDisposableContract>();
		TestDisposable disposable = new();
		DisposableContainer container = new() { Value = disposable };
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

		await client.UseDisposableContainerAsync(container, cts.Token);

		await disposable.Disposed.WithCancellation(cts.Token);
		Assert.True(disposable.IsDisposed);
	}

	[Test]
	[Arguments(JsonRpcEncoding.MessagePack)]
	[Arguments(JsonRpcEncoding.Json)]
	public async Task GeneratedProxy_SerializesConcreteDisposablePropertyByValue(JsonRpcEncoding encoding)
	{
		NativeAotTestHelper.SkipNerdbankJsonOnNativeAot(encoding);
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		DisposableTarget target = new();
		serverRpc.AddRpcTarget<IDisposableContract>(target);
		serverRpc.Start();
		clientRpc.Start();
		IDisposableContract client = clientRpc.Attach<IDisposableContract>();
		SerializableDisposable disposable = new() { Number = 42 };
		ConcreteDisposableContainer container = new() { Value = disposable };
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

		await client.UseConcreteDisposableContainerAsync(container, cts.Token);

		SerializableDisposable received = await target.ConcreteDisposableReceived.Task.WithCancellation(cts.Token);
		Assert.NotSame(disposable, received);
		Assert.Equal(42, received.Number);
		Assert.True(received.IsDisposed);
		Assert.False(disposable.IsDisposed);
	}

	[Test]
	public async Task GeneratedProxy_IncludesInheritedInterfaceMethods()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();

		JsonRpcMessagePackChannel clientChannel = new(clientPipe, NullLogger.Instance);
		JsonRpcMessagePackChannel serverChannel = new(serverPipe, NullLogger.Instance);

		JsonRpc clientRpc = new(clientChannel);
		clientRpc.Start();
		ICompositeCalculator client = clientRpc.Attach<ICompositeCalculator>();

		Calculator server = new();
		JsonRpc serverRpc = new(serverChannel);
		serverRpc.AddRpcTarget<ICalculator>(server);
		serverRpc.Start();

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		int sum = await client.AddAsync(2, 3, cts.Token);
		Assert.Equal(5, sum);
	}

	[Test]
	public async Task GeneratedProxy_CanPackArgumentsPositionally()
	{
		(MockChannel<JsonRpcMessage> transport, MockChannel<JsonRpcMessage> remote) = MockChannel<JsonRpcMessage>.CreatePair();
		JsonRpc clientRpc = new(new MockJsonRpcPipeChannel(transport));
		clientRpc.Start();
		IPositionalCalculator client = clientRpc.Attach<IPositionalCalculator>(new JsonRpcProxyOptions());

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Task<int> resultTask = client.SubtractAsync(9, 4, cts.Token).AsTask();

		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await remote.Reader.ReadAsync(cts.Token));
		Assert.Equal("subtract", request.Method);
		Assert.NotNull(request.Id);

		MessagePackReader reader = new(request.Arguments.AsMessagePack());
		Assert.Equal(MessagePackType.Array, reader.NextMessagePackType);
		Assert.Equal(2, reader.ReadArrayHeader());
		Assert.Equal(9, reader.ReadInt32());
		Assert.Equal(4, reader.ReadInt32());

		JsonRpcResult response = new()
		{
			Id = request.Id!.Value,
			Result = (RawMessagePack)((IJsonRpcClient)clientRpc).Serializer.Serialize(5, ShapeProvider.Default.Int32, cts.Token),
		};
		await remote.Writer.WriteAsync(response, cts.Token);

		Assert.Equal(5, await resultTask.WithCancellation(cts.Token));
	}

	[Test]
	public async Task GeneratedProxy_EscapesKeywordParameterNames()
	{
		(MockChannel<JsonRpcMessage> transport, MockChannel<JsonRpcMessage> remote) = MockChannel<JsonRpcMessage>.CreatePair();
		JsonRpc clientRpc = new(new MockJsonRpcPipeChannel(transport));
		clientRpc.Start();
		IPositionalCalculator client = clientRpc.Attach<IPositionalCalculator>();

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Task<int> resultTask = client.EchoKeywordAsync(9, cts.Token).AsTask();

		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await remote.Reader.ReadAsync(cts.Token));
		Assert.Equal("echoKeyword", request.Method);
		Assert.NotNull(request.Id);

		MessagePackReader reader = new(request.Arguments.AsMessagePack());
		Assert.Equal(MessagePackType.Array, reader.NextMessagePackType);
		Assert.Equal(1, reader.ReadArrayHeader());
		Assert.Equal(9, reader.ReadInt32());

		JsonRpcResult response = new()
		{
			Id = request.Id!.Value,
			Result = (RawMessagePack)((IJsonRpcClient)clientRpc).Serializer.Serialize(9, ShapeProvider.Default.Int32, cts.Token),
		};
		await remote.Writer.WriteAsync(response, cts.Token);

		Assert.Equal(9, await resultTask.WithCancellation(cts.Token));
	}

	[Test]
	public async Task GeneratedProxy_CanPackArgumentsByNameWhenRequested()
	{
		(MockChannel<JsonRpcMessage> transport, MockChannel<JsonRpcMessage> remote) = MockChannel<JsonRpcMessage>.CreatePair();
		JsonRpc clientRpc = new(new MockJsonRpcPipeChannel(transport));
		clientRpc.Start();
		INamedCalculator client = clientRpc.Attach<INamedCalculator>(new JsonRpcProxyOptions { UseNamedArguments = true });

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Task<int> resultTask = client.SubtractAsync(9, 4, cts.Token).AsTask();

		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await remote.Reader.ReadAsync(cts.Token));
		Assert.Equal("subtract", request.Method);
		Assert.NotNull(request.Id);

		MessagePackReader reader = new(request.Arguments.AsMessagePack());
		Assert.Equal(MessagePackType.Map, reader.NextMessagePackType);
		Assert.Equal(2, reader.ReadMapHeader());
		Assert.Equal("a", reader.ReadString());
		Assert.Equal(9, reader.ReadInt32());
		Assert.Equal("b", reader.ReadString());
		Assert.Equal(4, reader.ReadInt32());

		JsonRpcResult response = new()
		{
			Id = request.Id!.Value,
			Result = (RawMessagePack)((IJsonRpcClient)clientRpc).Serializer.Serialize(5, ShapeProvider.Default.Int32, cts.Token),
		};
		await remote.Writer.WriteAsync(response, cts.Token);

		Assert.Equal(5, await resultTask.WithCancellation(cts.Token));
	}

	[Test]
	public async Task GeneratedProxy_SameContractCanUseBothArgumentModes()
	{
		(MockChannel<JsonRpcMessage> transport, MockChannel<JsonRpcMessage> remote) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc rpc = new(new MockJsonRpcPipeChannel(transport));
		rpc.Start();
		IPositionalCalculator positional = rpc.Attach<IPositionalCalculator>();
		IPositionalCalculator named = rpc.Attach<IPositionalCalculator>(new JsonRpcProxyOptions { UseNamedArguments = true });

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		Task<int> positionalResult = positional.SubtractAsync(9, 4, cts.Token).AsTask();
		Task<int> namedResult = named.SubtractAsync(8, 3, cts.Token).AsTask();
		JsonRpcRequest first = Assert.IsType<JsonRpcRequest>(await remote.Reader.ReadAsync(cts.Token));
		JsonRpcRequest second = Assert.IsType<JsonRpcRequest>(await remote.Reader.ReadAsync(cts.Token));
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

		await remote.Writer.WriteAsync(new JsonRpcResult { Id = first.Id!.Value, Result = ((IJsonRpcClient)rpc).Serializer.Serialize(5, ShapeProvider.Default.Int32, cts.Token) }, cts.Token);
		await remote.Writer.WriteAsync(new JsonRpcResult { Id = second.Id!.Value, Result = ((IJsonRpcClient)rpc).Serializer.Serialize(5, ShapeProvider.Default.Int32, cts.Token) }, cts.Token);
		Assert.Equal(5, await positionalResult.WithCancellation(cts.Token));
		Assert.Equal(5, await namedResult.WithCancellation(cts.Token));
	}

	[Test]
	public void GeneratedProxy_AttachRequiresGeneratedProxyMetadata()
	{
		(MockChannel<JsonRpcMessage> transport, _) = MockChannel<JsonRpcMessage>.CreatePair();
		JsonRpc clientRpc = new(new MockJsonRpcPipeChannel(transport));

		NotSupportedException ex = Assert.Throws<NotSupportedException>(() => clientRpc.Attach<INotGeneratedProxy>());
		Assert.Contains(nameof(INotGeneratedProxy), ex.Message);
	}

	[Test]
	public void GeneratedProxy_AttachRequiresInterfaceType()
	{
		(MockChannel<JsonRpcMessage> transport, _) = MockChannel<JsonRpcMessage>.CreatePair();
		JsonRpc clientRpc = new(new MockJsonRpcPipeChannel(transport));

		ArgumentException ex = Assert.Throws<ArgumentException>(() => clientRpc.Attach(typeof(string)));
		Assert.Contains("interface", ex.Message, StringComparison.OrdinalIgnoreCase);
	}

	private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding)
		=> encoding switch
		{
			JsonRpcEncoding.MessagePack => new JsonRpcMessagePackChannel(pipe, NullLogger.Instance),
			JsonRpcEncoding.Json => new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited, NullLogger.Instance),
			_ => throw new ArgumentOutOfRangeException(nameof(encoding)),
		};
}
