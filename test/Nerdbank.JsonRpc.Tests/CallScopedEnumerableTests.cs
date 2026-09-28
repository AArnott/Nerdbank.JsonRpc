// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;

using ShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests;

namespace Nerdbank.JsonRpc.Tests;

/// <summary>Exercises call-scoped callbacks used by returned sequences.</summary>
public class CallScopedEnumerableTests : TestBase
{
	/// <summary>Callbacks survive both the originating response and prefetched/read-ahead batches.</summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <param name="prefetch">The prefetch count.</param>
	/// <param name="readAhead">The read-ahead count.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json, 0, 0)]
	[Arguments(JsonRpcEncoding.MessagePack, 0, 0)]
	[Arguments(JsonRpcEncoding.Json, 1, 0)]
	[Arguments(JsonRpcEncoding.MessagePack, 1, 0)]
	[Arguments(JsonRpcEncoding.Json, 10, 0)]
	[Arguments(JsonRpcEncoding.MessagePack, 10, 0)]
	[Arguments(JsonRpcEncoding.Json, 0, 2)]
	[Arguments(JsonRpcEncoding.MessagePack, 0, 2)]
	public async Task CallbackLivesThroughEnumeration(JsonRpcEncoding encoding, int prefetch, int readAhead)
	{
		using Fixture fixture = new(encoding);
		Counter counter = new();
		List<int> values = [];
		await foreach (int value in fixture.Client.UseCounterAsync(counter, 3, prefetch, readAhead, false, this.TimeoutToken).WithCancellation(this.TimeoutToken))
		{
			values.Add(value);
		}

		Assert.Equal(new[] { 1, 2, 3 }, values);
		Assert.Equal(4, counter.Count);
		await this.AssertExpiredAsync(fixture, counter);
	}

	/// <summary>Both bare and task-wrapped sequences retain arguments when sent in a batch.</summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <param name="wrapped">Whether the result is task-wrapped.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json, false)]
	[Arguments(JsonRpcEncoding.MessagePack, false)]
	[Arguments(JsonRpcEncoding.Json, true)]
	[Arguments(JsonRpcEncoding.MessagePack, true)]
	public async Task BatchedSequenceRetainsCallback(JsonRpcEncoding encoding, bool wrapped)
	{
		using Fixture fixture = new(encoding);
		using JsonRpcBatch batch = fixture.ClientRpc.CreateBatch();
		IAsyncEnumerableService client = batch.Attach<IAsyncEnumerableService>();
		Counter counter = new();
		Task<IAsyncEnumerable<int>> result = wrapped
			? client.UseCounterWrappedAsync(counter, this.TimeoutToken)
			: Task.FromResult(client.UseCounterAsync(counter, 3, 0, 0, false, this.TimeoutToken));
		await batch.SendAsync(this.TimeoutToken);
		List<int> values = [];
		await foreach (int value in (await result).WithCancellation(this.TimeoutToken))
		{
			values.Add(value);
		}

		Assert.Equal(new[] { 1, 2, 3 }, values);
		Assert.Equal(4, counter.Count);
		await this.AssertExpiredAsync(fixture, counter);
	}

	/// <summary>Aborting waits for asynchronous generator disposal before releasing callbacks.</summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <param name="canceled">Whether the consumer cancels instead of breaking.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json, false)]
	[Arguments(JsonRpcEncoding.MessagePack, false)]
	[Arguments(JsonRpcEncoding.Json, true)]
	[Arguments(JsonRpcEncoding.MessagePack, true)]
	public async Task EarlyTerminationKeepsCallbackAliveDuringDisposal(JsonRpcEncoding encoding, bool canceled)
	{
		using Fixture fixture = new(encoding);
		Counter counter = new();
		using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
		await using IAsyncEnumerator<int> enumerator = fixture.Client.UseCounterAsync(counter, 100, 0, 0, false, this.TimeoutToken).GetAsyncEnumerator(cancellation.Token);
		Assert.True(await enumerator.MoveNextAsync());
		if (canceled)
		{
			cancellation.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerator.MoveNextAsync().AsTask());
		}
		else
		{
			await enumerator.DisposeAsync();
		}

		Assert.Equal(2, counter.Count);
		await this.AssertExpiredAsync(fixture, counter);
	}

	/// <summary>A failed enumeration releases its callback after running its finally block.</summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task EnumerationFailureReleasesCallback(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		Counter counter = new();
		await using IAsyncEnumerator<int> enumerator = fixture.Client.UseCounterAsync(counter, 3, 0, 0, true, this.TimeoutToken).GetAsyncEnumerator(this.TimeoutToken);
		Assert.True(await enumerator.MoveNextAsync());
		await Assert.ThrowsAsync<JsonRpcException>(() => enumerator.MoveNextAsync().AsTask());
		Assert.Equal(2, counter.Count);
		await this.AssertExpiredAsync(fixture, counter);
	}

	/// <summary>Disposing before the first pull still ends the remote callback scope.</summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task DisposalBeforeFirstMoveReleasesCallback(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		Counter counter = new();
		IAsyncEnumerator<int> enumerator = fixture.Client.UseCounterAsync(counter, 3, 0, 0, false, this.TimeoutToken).GetAsyncEnumerator(this.TimeoutToken);
		await enumerator.DisposeAsync();
		await enumerator.DisposeAsync();
		Assert.Equal(0, counter.Count);
		await this.AssertExpiredAsync(fixture, counter);
	}

	/// <summary>Rejecting the original request does not extend callback lifetime.</summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task OriginatingFailureExpiresCallback(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		Counter counter = new();
		IAsyncEnumerator<int> enumerator = fixture.Client.UseCounterAsync(counter, -1, 0, 0, false, this.TimeoutToken).GetAsyncEnumerator(this.TimeoutToken);
		await Assert.ThrowsAsync<JsonRpcException>(() => enumerator.MoveNextAsync().AsTask());
		await this.AssertExpiredAsync(fixture, counter);
	}

	/// <summary>Callback ownership is shared across sibling and nested returned sequences.</summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <param name="nested">Whether sequences arrive inside an outer enumeration.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json, false)]
	[Arguments(JsonRpcEncoding.MessagePack, false)]
	[Arguments(JsonRpcEncoding.Json, true)]
	[Arguments(JsonRpcEncoding.MessagePack, true)]
	public async Task MultipleSequencesShareCallbackLifetime(JsonRpcEncoding encoding, bool nested)
	{
		using Fixture fixture = new(encoding);
		Counter counter = new();
		List<IAsyncEnumerable<int>> sequences = [];
		if (nested)
		{
			await foreach (IAsyncEnumerable<int> sequence in fixture.Client.UseCounterInNestedSequencesAsync(counter, this.TimeoutToken).WithCancellation(this.TimeoutToken))
			{
				sequences.Add(sequence);
			}
		}
		else
		{
			sequences.AddRange(await fixture.Client.UseCounterInArrayAsync(counter, this.TimeoutToken));
		}

		List<int> values = [];
		foreach (IAsyncEnumerable<int> sequence in sequences)
		{
			await foreach (int value in sequence.WithCancellation(this.TimeoutToken))
			{
				values.Add(value);
			}
		}

		Assert.Equal(new[] { 1, 3 }, values);
		Assert.Equal(4, counter.Count);
		await this.AssertExpiredAsync(fixture, counter);
	}

	/// <summary>Cancellation during an outstanding pull still runs cleanup with a valid callback.</summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task CancelingPendingMoveReleasesCallback(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		Counter counter = new(blockSecondCall: true);
		using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
		await using IAsyncEnumerator<int> enumerator = fixture.Client.UseCounterAsync(counter, 100, 0, 0, false, this.TimeoutToken).GetAsyncEnumerator(cancellation.Token);
		Assert.True(await enumerator.MoveNextAsync());
		Task<bool> move = enumerator.MoveNextAsync().AsTask();
		await counter.SecondCallStarted.Task.WithCancellation(this.TimeoutToken);
		cancellation.Cancel();
		await Assert.ThrowsAsync<JsonRpcException>(() => move);
		Assert.Equal(3, counter.Count);
		await this.AssertExpiredAsync(fixture, counter);
	}

	/// <summary>Disconnecting invalidates deferred callbacks without disposing their owner.</summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ConnectionLossEndsCallbackLifetime(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		Counter counter = new();
		await using IAsyncEnumerator<int> enumerator = fixture.Client.UseCounterAsync(counter, 100, 0, 0, false, this.TimeoutToken).GetAsyncEnumerator(this.TimeoutToken);
		Assert.True(await enumerator.MoveNextAsync());
		fixture.Dispose();
		await Assert.ThrowsAnyAsync<Exception>(() => enumerator.MoveNextAsync().AsTask());
		Assert.NotNull(fixture.Service.LastCounter);
		while (await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.LastCounter.IncrementAsync(this.TimeoutToken)) is not ObjectDisposedException)
		{
			await Task.Delay(1, this.TimeoutToken);
		}

		Assert.False(counter.IsDisposed);
	}

	/// <summary>Custom enumerator disposal can use callbacks even when prefetch consumes all values.</summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <param name="prefetch">The prefetch count.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json, 0)]
	[Arguments(JsonRpcEncoding.MessagePack, 0)]
	[Arguments(JsonRpcEncoding.Json, 10)]
	[Arguments(JsonRpcEncoding.MessagePack, 10)]
	public async Task CustomEnumeratorDisposalRetainsCallback(JsonRpcEncoding encoding, int prefetch)
	{
		using Fixture fixture = new(encoding);
		Counter counter = new();
		List<int> values = [];
		await foreach (int value in fixture.Client.UseCounterDuringDisposalAsync(counter, prefetch, this.TimeoutToken).WithCancellation(this.TimeoutToken))
		{
			values.Add(value);
		}

		Assert.Equal(new[] { 42 }, values);
		Assert.Equal(1, counter.Count);
		await this.AssertExpiredAsync(fixture, counter);
	}

	/// <summary>Verifies handle cleanup at the owning endpoint using a previously encoded reference.</summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <param name="abort">Whether to abort instead of draining the sequence.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json, false)]
	[Arguments(JsonRpcEncoding.MessagePack, false)]
	[Arguments(JsonRpcEncoding.Json, true)]
	[Arguments(JsonRpcEncoding.MessagePack, true)]
	public async Task OwnerReleasesCallbackHandleWhenEnumerationEnds(JsonRpcEncoding encoding, bool abort)
	{
		using Fixture fixture = new(encoding);
		Counter counter = new();
		await using IAsyncEnumerator<int> enumerator = fixture.Client.UseCounterAsync(counter, 3, 0, 0, false, this.TimeoutToken).GetAsyncEnumerator(this.TimeoutToken);
		Assert.True(await enumerator.MoveNextAsync());
		JsonRpcValue reference;
		using (JsonRpcArgumentsBuilder arguments = fixture.ServerRpc.CreateArguments(named: false, count: 1, this.TimeoutToken))
		{
			arguments.Add(null, fixture.Service.LastCounter!, ShapeProvider.Default.ICallScopedCounter);
			reference = arguments.Build();
		}

		await fixture.ServerRpc.RequestAsync("useCallScopedCounter", reference, this.TimeoutToken);
		Assert.Equal(2, counter.Count);
		if (abort)
		{
			await enumerator.DisposeAsync();
		}
		else
		{
			while (await enumerator.MoveNextAsync())
			{
			}
		}

		JsonRpcException exception = await Assert.ThrowsAsync<JsonRpcException>(() => fixture.ServerRpc.RequestAsync("useCallScopedCounter", reference, this.TimeoutToken).AsTask());
		Assert.Equal(JsonRpcErrorCode.InvalidParams, exception.ErrorDetails.Code);
		Assert.False(counter.IsDisposed);
	}

	private async Task AssertExpiredAsync(Fixture fixture, Counter counter)
	{
		Assert.NotNull(fixture.Service.LastCounter);
		await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Service.LastCounter.IncrementAsync(this.TimeoutToken));
		Assert.False(counter.IsDisposed);
	}

	private sealed class Counter(bool blockSecondCall = false) : ICallScopedCounter, IDisposable
	{
		private int count;

		/// <summary>Gets the callback count.</summary>
		internal int Count => Volatile.Read(ref this.count);

		/// <summary>Gets the signal that the blocking callback has started.</summary>
		internal TaskCompletionSource<bool> SecondCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <summary>Gets a value indicating whether the owner was disposed.</summary>
		internal bool IsDisposed { get; private set; }

		/// <inheritdoc/>
		public async Task<int> IncrementAsync(CancellationToken cancellationToken)
		{
			await Task.Yield();
			int value = Interlocked.Increment(ref this.count);
			if (blockSecondCall && value == 2)
			{
				this.SecondCallStarted.TrySetResult(true);
				await Task.Delay(Timeout.Infinite, cancellationToken);
			}

			return value;
		}

		/// <inheritdoc/>
		public void Dispose() => this.IsDisposed = true;
	}

	private sealed class Fixture : IDisposable
	{
		private readonly JsonRpc serverRpc;

		/// <summary>Initializes a new instance of the <see cref="Fixture"/> class.</summary>
		/// <param name="encoding">The wire encoding.</param>
		internal Fixture(JsonRpcEncoding encoding)
		{
			(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
			this.ClientRpc = new(CreateChannel(clientPipe, encoding));
			this.serverRpc = new(CreateChannel(serverPipe, encoding));
			this.serverRpc.AddRpcTarget<IAsyncEnumerableService>(this.Service);
			this.ClientRpc.AddRpcTarget<IRemoteCounterService>(new RemoteCounterService());
			this.serverRpc.Start();
			this.ClientRpc.Start();
			this.Client = this.ClientRpc.Attach<IAsyncEnumerableService>();
		}

		/// <summary>Gets the client connection.</summary>
		internal JsonRpc ClientRpc { get; }

		/// <summary>Gets the server connection.</summary>
		internal JsonRpc ServerRpc => this.serverRpc;

		/// <summary>Gets the client proxy.</summary>
		internal IAsyncEnumerableService Client { get; }

		/// <summary>Gets the server target.</summary>
		internal AsyncEnumerableService Service { get; } = new();

		/// <inheritdoc/>
		public void Dispose()
		{
			this.ClientRpc.Dispose();
			this.serverRpc.Dispose();
		}

		private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding)
			=> encoding == JsonRpcEncoding.Json
				? new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited, NullLogger.Instance)
				: new JsonRpcMessagePackChannel(pipe, NullLogger.Instance);
	}
}
