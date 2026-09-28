// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc.Tests;

/// <summary>Exercises connection boundaries for marshaled proxies.</summary>
public class ProxyForwardingTests : TestBase
{
	/// <summary>Rejects forwarding without damaging proxies or their original targets.</summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <param name="batched">Whether forwarding uses a batch.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json, false)]
	[Arguments(JsonRpcEncoding.Json, true)]
	[Arguments(JsonRpcEncoding.MessagePack, false)]
	[Arguments(JsonRpcEncoding.MessagePack, true)]
	public async Task ForeignProxiesAreRejected(JsonRpcEncoding encoding, bool batched)
	{
		(IDuplexPipe aPipe, IDuplexPipe bPipe) = FullDuplexStream.CreatePipePair();
		(IDuplexPipe bToCPipe, IDuplexPipe cPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc a = new(CreateChannel(aPipe, encoding));
		using JsonRpc b = new(CreateChannel(bPipe, encoding));
		using JsonRpc bToC = new(CreateChannel(bToCPipe, encoding));
		using JsonRpc c = new(CreateChannel(cPipe, encoding));
		RemoteCounterService counters = new();
		DisposableTarget disposables = new();
		ObserverService observers = new();
		a.AddRpcTarget<IRemoteCounterService>(counters);
		a.AddRpcTarget<IDisposableContract>(disposables);
		a.AddRpcTarget<IObserverService>(observers, new JsonRpcTargetOptions { MethodNameTransform = name => "observer/" + name });
		a.Start();
		b.Start();
		bToC.Start();
		c.Start();
		using JsonRpcBatch batch = bToC.CreateBatch();
		IRemoteCounterService original = b.Attach<IRemoteCounterService>();
		IDisposableContract disposableClient = b.Attach<IDisposableContract>();
		IObserverService observerClient = b.Attach<IObserverService>(new JsonRpcProxyOptions { MethodNameTransform = name => "observer/" + name });
		IRemoteCounterService foreign = batched ? batch.Attach<IRemoteCounterService>() : bToC.Attach<IRemoteCounterService>();
		IDisposableContract foreignDisposable = batched ? batch.Attach<IDisposableContract>() : bToC.Attach<IDisposableContract>();
		IObserverService foreignObserver = batched ? batch.Attach<IObserverService>() : bToC.Attach<IObserverService>();
		using IRemoteCounter counter = await original.GetCounterAsync(this.TimeoutToken);
		using IDisposable disposable = await disposableClient.GetDisposableAsync(this.TimeoutToken);
		IObserver<int> observer = await observerClient.GetObserverAsync(this.TimeoutToken);

		await AssertForwardingRejectedAsync(encoding, () => foreign.IsSameCounterAsync(counter, this.TimeoutToken));
		await AssertForwardingRejectedAsync(encoding, () => foreignDisposable.IsReturnedDisposableAsync(disposable, this.TimeoutToken));
		await AssertForwardingRejectedAsync(encoding, () => foreignObserver.IsOwnedObserverAsync(observer, this.TimeoutToken));
		await AssertForwardingRejectedAsync(encoding, () => foreignDisposable.UseDisposableContainerAsync(new DisposableContainer { Value = counter }, this.TimeoutToken));

		await original.UseCallScopedCounterAsync(new CallScopedCounter(), this.TimeoutToken);
		await AssertForwardingRejectedAsync(encoding, () => foreign.UseCallScopedCounterAsync(counters.LastCallScopedProxy!, this.TimeoutToken));

		Assert.True(await original.IsSameCounterAsync(counter, this.TimeoutToken));
		Assert.True(await disposableClient.IsReturnedDisposableAsync(disposable, this.TimeoutToken));
		Assert.True(await observerClient.IsOwnedObserverAsync(observer, this.TimeoutToken));
		Assert.Equal(1, await counter.IncrementAsync(this.TimeoutToken));
		Assert.False(counters.Counter.IsDisposed);
		Assert.False(disposables.ReturnedDisposable.IsDisposed);
		observer.OnNext(42);
		Assert.Equal(42, await observers.OwnedObserver.Next.Task.WithCancellation(this.TimeoutToken));
		observer.OnCompleted();
	}

	private static async Task AssertForwardingRejectedAsync(JsonRpcEncoding encoding, Func<Task> action)
	{
		Exception exception = encoding == JsonRpcEncoding.Json
			? await Assert.ThrowsAsync<NotSupportedException>(action)
			: await Assert.ThrowsAsync<MessagePackSerializationException>(action);
		Assert.IsType<NotSupportedException>(exception.GetBaseException());
		Assert.Contains("different JSON-RPC connection", exception.GetBaseException().Message);
	}

	private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding)
		=> encoding == JsonRpcEncoding.Json
			? new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited, NullLogger.Instance)
			: new JsonRpcMessagePackChannel(pipe, NullLogger.Instance);
}
