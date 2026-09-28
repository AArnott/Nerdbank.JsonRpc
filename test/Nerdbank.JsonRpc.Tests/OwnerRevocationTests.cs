// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Nerdbank.Streams;

using ShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests;

public class OwnerRevocationTests
{
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task RevokesEveryHandleWithoutDisposingTarget(JsonRpcEncoding encoding)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		RemoteCounterService target = new();
		clientRpc.AddRpcTarget<ICalculator>(new Calculator());
		serverRpc.AddRpcTarget<IRemoteCounterService>(target);
		serverRpc.Start();
		clientRpc.Start();
		IRemoteCounterService client = clientRpc.Attach<IRemoteCounterService>();
		ICalculator barrier = serverRpc.Attach<ICalculator>();
		IRemoteCounter first = await client.GetCounterAsync(CancellationToken.None);
		IRemoteCounter second = await client.GetCounterAsync(CancellationToken.None);

		Assert.Equal(2, serverRpc.RevokeMarshaledObject(target.Counter));
		Assert.Equal(0, serverRpc.RevokeMarshaledObject(target.Counter));
		await barrier.PingAsync(CancellationToken.None);

		await Assert.ThrowsAsync<ObjectDisposedException>(() => first.IncrementAsync(CancellationToken.None));
		await Assert.ThrowsAsync<ObjectDisposedException>(() => second.IncrementAsync(CancellationToken.None));
		Assert.False(target.Counter.IsDisposed);
		first.Dispose();
		second.Dispose();
		Assert.False(target.Counter.IsDisposed);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task InvokingRevokedHandleReturnsDedicatedError(JsonRpcEncoding encoding)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		RemoteCounterService target = new();
		clientRpc.AddRpcTarget<ICalculator>(new Calculator());
		serverRpc.AddRpcTarget<IRemoteCounterService>(target);
		serverRpc.Start();
		clientRpc.Start();
		ICalculator barrier = serverRpc.Attach<ICalculator>();
		MarshaledObjectMarker marker = await clientRpc.RequestAsync("getCounter", CreateEmptyArguments(clientRpc), ShapeProvider.Default.MarshaledObjectMarker, CancellationToken.None);

		Assert.Equal(1, serverRpc.RevokeMarshaledObject(target.Counter));
		await barrier.PingAsync(CancellationToken.None);

		JsonRpcException exception = await Assert.ThrowsAsync<JsonRpcException>(() => clientRpc.RequestAsync($"$/invokeProxy/{marker.Handle}/increment", CreateEmptyArguments(clientRpc), ShapeProvider.Default.Int32, CancellationToken.None).AsTask());
		Assert.Equal(JsonRpcErrorCode.NoMarshaledObjectFound, exception.ErrorDetails.Code);
		Assert.False(target.Counter.IsDisposed);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task EitherEndpointMayRevokeItsOwnedObject(JsonRpcEncoding encoding)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		RemoteCounterService target = new();
		serverRpc.AddRpcTarget<IRemoteCounterService>(target);
		serverRpc.AddRpcTarget<ICalculator>(new Calculator());
		serverRpc.Start();
		clientRpc.Start();
		IRemoteCounterService client = clientRpc.Attach<IRemoteCounterService>();
		RemoteCounter localCounter = new();
		Assert.False(await client.IsSameCounterAsync(localCounter, CancellationToken.None));
		Assert.NotNull(target.LastExplicitProxy);

		Assert.Equal(1, clientRpc.RevokeMarshaledObject(localCounter));
		await clientRpc.Attach<ICalculator>().PingAsync(CancellationToken.None);

		await Assert.ThrowsAsync<ObjectDisposedException>(() => target.LastExplicitProxy.IncrementAsync(CancellationToken.None));
		Assert.False(localCounter.IsDisposed);
	}

	[Test]
	public async Task InvocationDispatchedBeforeRevocationMayComplete()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(new JsonRpcMessagePackChannel(clientPipe, NullLogger.Instance));
		using JsonRpc serverRpc = new(new JsonRpcMessagePackChannel(serverPipe, NullLogger.Instance));
		RemoteCounterService target = new();
		target.Counter.IncrementStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		target.Counter.ContinueIncrement = new(TaskCreationOptions.RunContinuationsAsynchronously);
		serverRpc.AddRpcTarget<IRemoteCounterService>(target);
		serverRpc.Start();
		clientRpc.Start();
		IRemoteCounter proxy = await clientRpc.Attach<IRemoteCounterService>().GetCounterAsync(CancellationToken.None);
		Task<int> invocation = proxy.IncrementAsync(CancellationToken.None);
		await target.Counter.IncrementStarted.Task;

		Assert.Equal(1, serverRpc.RevokeMarshaledObject(target.Counter));
		target.Counter.ContinueIncrement.SetResult(true);

		Assert.Equal(1, await invocation);
	}

	[Test]
	public async Task ReceiverDisposalRacingRevocationIsSafe()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(new JsonRpcMessagePackChannel(clientPipe, NullLogger.Instance));
		using JsonRpc serverRpc = new(new JsonRpcMessagePackChannel(serverPipe, NullLogger.Instance));
		RemoteCounterService target = new();
		serverRpc.AddRpcTarget<IRemoteCounterService>(target);
		serverRpc.Start();
		clientRpc.Start();
		IRemoteCounter proxy = await clientRpc.Attach<IRemoteCounterService>().GetCounterAsync(CancellationToken.None);

		await Task.WhenAll(Task.Run(proxy.Dispose), Task.Run(() => serverRpc.RevokeMarshaledObject(target.Counter)));

		Assert.Equal(0, serverRpc.RevokeMarshaledObject(target.Counter));
	}

	private static JsonRpcValue CreateEmptyArguments(JsonRpc rpc)
	{
		using JsonRpcArgumentsBuilder builder = rpc.CreateArguments(named: false, count: 0);
		return builder.Build();
	}

	private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding)
		=> encoding == JsonRpcEncoding.Json
			? new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited, NullLogger.Instance)
			: new JsonRpcMessagePackChannel(pipe, NullLogger.Instance);
}
