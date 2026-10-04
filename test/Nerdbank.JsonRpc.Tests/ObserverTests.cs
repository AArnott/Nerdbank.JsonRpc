// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using PolyType;
using PolyType.Abstractions;

public partial class ObserverTests
{
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ObserverArgumentReceivesValuesAndCompletion(JsonRpcEncoding encoding)
	{
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		ObserverService service = new();
		serverRpc.AddRpcTarget<IObserverService>(service);
		serverRpc.Start();
		clientRpc.Start();
		IObserverService client = clientRpc.Attach<IObserverService>();
		TestObserver observer = new();

		using IDisposable subscription = await client.SubscribeAsync(observer, cts.Token);
		Assert.NotNull(service.Subscriber);
		service.Subscriber.OnNext(42);
		service.Subscriber.OnCompleted();
		Assert.Equal(42, await observer.Next.Task.WithCancellation(cts.Token));
		await observer.Completed.Task.WithCancellation(cts.Token);
		Assert.False(observer.IsDisposed);
		Assert.Throws<ObjectDisposedException>(() => service.Subscriber.OnNext(43));
	}

	/// <summary>Verifies remote unsubscription stops updates after the server processes disposal.</summary>
	/// <param name="encoding">The wire encoding to exercise.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task DisposingSubscriptionStopsUpdates(JsonRpcEncoding encoding)
	{
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		ObserverService service = new();
		serverRpc.AddRpcTarget<IObserverService>(service);
		serverRpc.Start();
		clientRpc.Start();
		IObserverService client = clientRpc.Attach<IObserverService>();
		TestObserver observer = new();
		TestObserver remainingObserver = new();
		using IDisposable subscription = await client.SubscribeAsync(observer, cts.Token);
		using IDisposable remainingSubscription = await client.SubscribeAsync(remainingObserver, cts.Token);

		service.Publish(1);
		Assert.Equal(1, await observer.Updates.DequeueAsync(cts.Token));
		Assert.Equal(1, await remainingObserver.Updates.DequeueAsync(cts.Token));

		// This update may still be in flight when Dispose returns and must not be treated as a failure.
		service.Publish(2);
		subscription.Dispose();
		await service.Unsubscribed.Task.WithCancellation(cts.Token);
		service.Publish(3);

		Assert.Equal(2, await remainingObserver.Updates.DequeueAsync(cts.Token));
		Assert.Equal(3, await remainingObserver.Updates.DequeueAsync(cts.Token));

		// Both observers share an ordered connection. The last update is a delivery barrier, not a delay.
		Assert.Equal(new[] { 1, 2 }, observer.Values.ToArray());
		Assert.Equal(new[] { 1, 2, 3 }, remainingObserver.Values.ToArray());
		Assert.False(observer.IsDisposed);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ReturnedObserverReceivesError(JsonRpcEncoding encoding)
	{
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		ObserverService service = new();
		serverRpc.AddRpcTarget<IObserverService>(service);
		serverRpc.Start();
		clientRpc.Start();
		IObserverService client = clientRpc.Attach<IObserverService>();

		IObserver<int> observer = await client.GetObserverAsync(cts.Token);
		observer.OnNext(7);
		observer.OnError(new InvalidOperationException("failed"));
		Assert.Equal(7, await service.OwnedObserver.Next.Task.WithCancellation(cts.Token));
		Assert.Equal("failed", (await service.OwnedObserver.Error.Task.WithCancellation(cts.Token)).Message);
		Assert.False(service.OwnedObserver.IsDisposed);
		Assert.Throws<ObjectDisposedException>(() => observer.OnNext(8));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ObserverReturnedToOwnerRetainsIdentity(JsonRpcEncoding encoding)
	{
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		ObserverService service = new();
		serverRpc.AddRpcTarget<IObserverService>(service);
		serverRpc.Start();
		clientRpc.Start();
		IObserverService client = clientRpc.Attach<IObserverService>();

		IObserver<int> observer = await client.GetObserverAsync(cts.Token);
		Assert.True(await client.IsOwnedObserverAsync(observer, cts.Token));
		observer.OnCompleted();
		await service.OwnedObserver.Completed.Task.WithCancellation(cts.Token);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task FailedSubscriptionInvalidatesObserverProxy(JsonRpcEncoding encoding)
	{
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		ObserverService service = new();
		serverRpc.AddRpcTarget<IObserverService>(service);
		serverRpc.Start();
		clientRpc.Start();
		IObserverService client = clientRpc.Attach<IObserverService>();
		TestObserver observer = new();

		await Assert.ThrowsAsync<RemoteInvocationException>(() => client.FailAfterReceivingAsync(observer, cts.Token));
		Assert.NotNull(service.Subscriber);
		Assert.Throws<ObjectDisposedException>(() => service.Subscriber.OnNext(1));
		Assert.False(observer.Next.Task.IsCompleted);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ObserverIsNotDisposedWhenConnectionCloses(JsonRpcEncoding encoding)
	{
		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		ObserverService service = new();
		using (JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding)))
		using (JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding)))
		{
			serverRpc.AddRpcTarget<IObserverService>(service);
			serverRpc.Start();
			clientRpc.Start();
			IObserverService client = clientRpc.Attach<IObserverService>();
			IObserver<int> observer = await client.GetObserverAsync(cts.Token);
			observer.OnNext(11);
			Assert.Equal(11, await service.OwnedObserver.Next.Task.WithCancellation(cts.Token));
		}

		Assert.False(service.OwnedObserver.IsDisposed);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ObserverCannotBeSentInNotification(JsonRpcEncoding encoding)
	{
		(IDuplexPipe clientPipe, _) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		TestObserver observer = new();
		ITypeShape<IObserver<int>> shape = TypeShapeResolver.ResolveDynamicOrThrow<IObserver<int>, Witness>();

		await Assert.ThrowsAsync<InvalidOperationException>(() => clientRpc.NotifyAsync("subscribe", observer, shape, CancellationToken.None).AsTask());
		using JsonRpcBatch batch = clientRpc.CreateBatch();
		Assert.Throws<InvalidOperationException>(() => batch.NotifyAsync("subscribe", observer, shape, CancellationToken.None));
	}

	private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding)
		=> encoding == JsonRpcEncoding.Json
			? new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited)
			: new JsonRpcMessagePackChannel(pipe);

	[GenerateShapeFor<IObserver<int>>]
	private partial class Witness;
}
