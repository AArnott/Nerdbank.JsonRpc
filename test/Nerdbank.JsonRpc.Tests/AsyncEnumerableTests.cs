// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;

public partial class AsyncEnumerableTests : TestBase
{
	/// <summary>How a test severs an open JSON-RPC connection.</summary>
	public enum ConnectionLoss
	{
		/// <summary>The client <see cref="JsonRpc"/> is disposed.</summary>
		ClientDisposed,

		/// <summary>The underlying pipe is closed without disposing either <see cref="JsonRpc"/>.</summary>
		TransportClosed,

		/// <summary>The server <see cref="JsonRpc"/> is disposed.</summary>
		ServerDisposed,
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ReturnedSequenceStreamsAllValues(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		List<int> received = [];
		await foreach (int value in fixture.Client.GetNumbersAsync(5, CancellationToken.None))
		{
			received.Add(value);
		}

		Assert.Equal(new[] { 0, 1, 2, 3, 4 }, received);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task TaskWrappedReturnedSequenceStreamsAllValues(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		List<int> received = [];
		await foreach (int value in await fixture.Client.GetNumbersWrappedAsync(5, CancellationToken.None))
		{
			received.Add(value);
		}

		Assert.Equal(new[] { 0, 1, 2, 3, 4 }, received);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task EmptySequenceProducesNoValues(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		List<int> received = [];
		await foreach (int value in fixture.Client.GetNumbersAsync(0, CancellationToken.None))
		{
			received.Add(value);
		}

		Assert.Empty(received);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ReferenceTypeElementsRoundTrip(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		List<string> received = [];
		await foreach (string value in fixture.Client.GetWordsAsync(CancellationToken.None))
		{
			received.Add(value);
		}

		Assert.Equal(new[] { "alpha", "beta", "gamma" }, received);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task PrefetchDeliversValuesWithTheOriginatingMessage(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		IAsyncEnumerable<int> sequence = fixture.Client.GetNumbersWithSettingsAsync(5, minBatchSize: 1, maxReadAhead: 0, prefetch: 3, CancellationToken.None);

		await using IAsyncEnumerator<int> enumerator = sequence.GetAsyncEnumerator(CancellationToken.None);

		// Await the first value so the deferred request's response has definitely arrived before checking
		// how many values the server produced; the response only arrives once the prefetched values are ready.
		Assert.True(await enumerator.MoveNextAsync());
		Assert.Equal(0, enumerator.Current);

		// The server must have produced the prefetched values before it answered the original request.
		Assert.True(await fixture.Client.CountGeneratedValuesAsync(CancellationToken.None) >= 3);

		List<int> received = [0];
		while (await enumerator.MoveNextAsync())
		{
			received.Add(enumerator.Current);
		}

		Assert.Equal(new[] { 0, 1, 2, 3, 4 }, received);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task PrefetchOfEntireSequenceRequiresNoToken(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		IAsyncEnumerable<int> sequence = fixture.Client.GetNumbersWithSettingsAsync(3, minBatchSize: 1, maxReadAhead: 0, prefetch: 10, CancellationToken.None);

		List<int> received = [];
		await foreach (int value in sequence)
		{
			received.Add(value);
		}

		Assert.Equal(new[] { 0, 1, 2 }, received);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task MinBatchSizeStreamsValuesInBatches(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		IAsyncEnumerable<int> sequence = fixture.Client.GetNumbersWithSettingsAsync(10, minBatchSize: 4, maxReadAhead: 0, prefetch: 0, CancellationToken.None);

		await using IAsyncEnumerator<int> enumerator = sequence.GetAsyncEnumerator(CancellationToken.None);
		Assert.True(await enumerator.MoveNextAsync());
		Assert.Equal(0, enumerator.Current);

		// A single round trip must have produced at least MinBatchSize values.
		Assert.True(await fixture.Client.CountGeneratedValuesAsync(CancellationToken.None) >= 4);

		List<int> received = [0];
		while (await enumerator.MoveNextAsync())
		{
			received.Add(enumerator.Current);
		}

		Assert.Equal(Enumerable.Range(0, 10), received);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task MaxReadAheadProducesValuesBeforeTheyAreRequested(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		IAsyncEnumerable<int> sequence = fixture.Client.GetNumbersWithSettingsAsync(20, minBatchSize: 1, maxReadAhead: 5, prefetch: 0, CancellationToken.None);

		await using IAsyncEnumerator<int> enumerator = sequence.GetAsyncEnumerator(CancellationToken.None);
		Assert.True(await enumerator.MoveNextAsync());
		Assert.Equal(0, enumerator.Current);

		List<int> received = [0];
		while (await enumerator.MoveNextAsync())
		{
			received.Add(enumerator.Current);
		}

		Assert.Equal(Enumerable.Range(0, 20), received);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task EarlyDisposalReleasesTheGenerator(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		IAsyncEnumerable<int> sequence = fixture.Client.GetNumbersAsync(1000, CancellationToken.None);

		await foreach (int value in sequence)
		{
			if (value == 2)
			{
				break;
			}
		}

		// The abort travels as a notification, so poll until the server observes it.
		while (!await fixture.Client.IsGeneratorDisposedAsync(CancellationToken.None))
		{
			await Task.Delay(10);
		}
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task SequenceMayOnlyBeEnumeratedOnce(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		IAsyncEnumerable<int> sequence = fixture.Client.GetNumbersAsync(3, CancellationToken.None);

		await foreach (int value in sequence)
		{
		}

		Assert.Throws<InvalidOperationException>(() => sequence.GetAsyncEnumerator(CancellationToken.None));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task GeneratorFailurePropagatesToTheConsumer(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		IAsyncEnumerable<int> sequence = fixture.Client.GetFailingSequenceAsync(2, CancellationToken.None);

		List<int> received = [];
		RemoteInvocationException exception = await Assert.ThrowsAsync<RemoteInvocationException>(async () =>
		{
			await foreach (int value in sequence)
			{
				received.Add(value);
			}
		});

		Assert.Equal(typeof(InvalidOperationException).FullName, exception.RemoteException!.TypeName);
		Assert.Equal("The sequence failed as requested.", exception.RemoteException.Message);
		Assert.Equal(new[] { 0, 1 }, received);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task SequencePassedAsArgumentIsPulledByTheServer(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		Assert.Equal(10, await fixture.Client.SumAsync(Enumerable.Range(0, 5).AsAsyncEnumerable(), CancellationToken.None));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task MultipleSequenceArgumentsAreIndependent(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		IAsyncEnumerable<int> first = Enumerable.Range(0, 5).AsAsyncEnumerable();
		IAsyncEnumerable<int> second = Enumerable.Range(5, 4).AsAsyncEnumerable();
		Assert.Equal(10 + 26, await fixture.Client.SumTwoAsync(first, second, CancellationToken.None));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task PrefetchedArgumentSequenceIsPulledByTheServer(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		IAsyncEnumerable<int> prefetched = await Enumerable.Range(0, 5).AsAsyncEnumerable().WithPrefetchAsync(3, CancellationToken.None);
		Assert.Equal(10, await fixture.Client.SumAsync(prefetched, CancellationToken.None));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task SequenceRejectedInNotifications(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		Assert.Throws<InvalidOperationException>(() => fixture.Client.NotifyWithSequence(Enumerable.Range(0, 3).AsAsyncEnumerable()));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ConnectionLossEndsEnumeration(JsonRpcEncoding encoding)
	{
		Fixture fixture = new(encoding);
		IAsyncEnumerable<int> sequence = fixture.Client.GetNumbersAsync(1000, CancellationToken.None);
		await using IAsyncEnumerator<int> enumerator = sequence.GetAsyncEnumerator(CancellationToken.None);
		Assert.True(await enumerator.MoveNextAsync());

		fixture.Dispose();

		await Assert.ThrowsAnyAsync<Exception>(async () =>
		{
			while (await enumerator.MoveNextAsync())
			{
			}
		});
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task NestedSequencesRoundTripThroughGeneratorResponses(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		List<List<int>> received = [];
		await foreach (IAsyncEnumerable<int> inner in fixture.Client.GetNestedSequencesAsync(CancellationToken.None))
		{
			List<int> innerValues = [];
			await foreach (int value in inner)
			{
				innerValues.Add(value);
			}

			received.Add(innerValues);
		}

		Assert.Equal(2, received.Count);
		Assert.Equal(new[] { 0, 1, 2 }, received[0]);
		Assert.Equal(new[] { 10, 11, 12 }, received[1]);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task SequenceRejectedInBatchNotifications(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		using JsonRpcBatch batch = fixture.ClientRpc.CreateBatch();
		IAsyncEnumerableService batchClient = batch.Attach<IAsyncEnumerableService>();
		Assert.Throws<InvalidOperationException>(() => batchClient.NotifyWithSequence(Enumerable.Range(0, 3).AsAsyncEnumerable()));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ArgumentSequenceReleasedWhenRequestFailsBeforeSending(JsonRpcEncoding encoding)
	{
		// Attach a proxy to a JsonRpc instance that is never started, so argument marshaling succeeds
		// but the request fails before AsyncEnumerableManager.RegisterOutboundRequest ever runs.
		(IDuplexPipe clientPipe, _) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(encoding == JsonRpcEncoding.Json
			? new JsonRpcJsonChannel(clientPipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited)
			: new JsonRpcMessagePackChannel(clientPipe));
		IAsyncEnumerableService client = clientRpc.Attach<IAsyncEnumerableService>();
		TrackingSequence argument = new();
		await Assert.ThrowsAnyAsync<InvalidOperationException>(
			() => client.SumAsync(argument, CancellationToken.None));

		Assert.True(argument.EnumeratorDisposed);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ArgumentSequenceReleasedWhenBatchEntryIsCanceledBeforeSending(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		using JsonRpcBatch batch = fixture.ClientRpc.CreateBatch();
		IAsyncEnumerableService batchClient = batch.Attach<IAsyncEnumerableService>();
		TrackingSequence argument = new();
		using CancellationTokenSource cts = new();

		Task<int> sumTask = batchClient.SumAsync(argument, cts.Token);
		cts.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sumTask);
		Assert.True(argument.EnumeratorDisposed);
	}

	/// <summary>
	/// Dropping the connection while a client is enumerating disposes the server iterator and cancels its token.
	/// </summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <param name="disconnect">How the connection is severed.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.ClientDisposed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.ClientDisposed)]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.TransportClosed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.TransportClosed)]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.ServerDisposed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.ServerDisposed)]
	public async Task ConnectionLossDisposesAndCancelsServerIterator(JsonRpcEncoding encoding, ConnectionLoss disconnect)
	{
		using Fixture fixture = new(encoding);
		Task enumeration = this.EnumerateAsync(fixture.Client.GetNumbersUntilCanceledAsync(this.TimeoutToken));
		await fixture.Service.CancelableEnumerationWaiting.Task.WithCancellation(this.TimeoutToken);

		await fixture.SeverAsync(disconnect);

		await fixture.Service.CancelableEnumerationDisposed.Task.WithCancellation(this.TimeoutToken);
		Assert.True(fixture.Service.CancelableEnumerationCanceled);
		await Assert.ThrowsAnyAsync<Exception>(() => enumeration);
		await this.AwaitCompletionAsync(fixture.ServerRpc.Completion);
		await this.AwaitCompletionAsync(fixture.ClientRpc.Completion);
	}

	/// <summary>
	/// An iterator that ignores cancellation and blocks in an await must not hold connection shutdown open, and it is disposed once it yields.
	/// </summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <param name="disconnect">How the connection is severed.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.ClientDisposed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.ClientDisposed)]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.TransportClosed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.TransportClosed)]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.ServerDisposed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.ServerDisposed)]
	public async Task ConnectionLossDoesNotWaitForIteratorThatIgnoresCancellation(JsonRpcEncoding encoding, ConnectionLoss disconnect)
	{
		using Fixture fixture = new(encoding);
		Task enumeration = this.EnumerateAsync(fixture.Client.GetNumbersIgnoringCancellationAsync(this.TimeoutToken));
		try
		{
			await fixture.Service.BlockingEnumerationWaiting.Task.WithCancellation(this.TimeoutToken);
			await fixture.SeverAsync(disconnect);

			// Shutdown finishes while the iterator is still blocked on an await that does not observe cancellation.
			await this.AwaitCompletionAsync(fixture.ServerRpc.Completion);
			Assert.False(fixture.Service.BlockingEnumerationDisposed.Task.IsCompleted);

			fixture.Service.ReleaseBlockingEnumeration.TrySetResult(true);
			await fixture.Service.BlockingEnumerationDisposed.Task.WithCancellation(this.TimeoutToken);
			Assert.True(fixture.Service.BlockingEnumerationCanceled);
			await Assert.ThrowsAnyAsync<Exception>(() => enumeration);
			await this.AwaitCompletionAsync(fixture.ClientRpc.Completion);
		}
		finally
		{
			fixture.Service.ReleaseBlockingEnumeration.TrySetResult(true);
		}
	}

	/// <summary>
	/// A canceled multi-value batch must not start another move after the in-flight one yields.
	/// </summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <param name="disconnect">How the connection is severed.</param>
	/// <returns>A task representing the test.</returns>
	/// <remarks>
	/// The batch size is 3 so the move in flight when the connection drops is not the last slot.
	/// An iterator that ignores cancellation would otherwise block on the next advance and hold disposal open.
	/// </remarks>
	[Test]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.ClientDisposed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.ClientDisposed)]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.TransportClosed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.TransportClosed)]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.ServerDisposed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.ServerDisposed)]
	public async Task ConnectionLossDoesNotAdvanceAnIncompleteBatch(JsonRpcEncoding encoding, ConnectionLoss disconnect)
	{
		const int minBatchSize = 3;
		using Fixture fixture = new(encoding);
		Task enumeration = this.EnumerateAsync(fixture.Client.GetBlockingBatchAsync(minBatchSize, this.TimeoutToken));
		try
		{
			await fixture.Service.BlockingBatchWaiting.Task.WithCancellation(this.TimeoutToken);
			await fixture.SeverAsync(disconnect);
			await this.AwaitCompletionAsync(fixture.ServerRpc.Completion);
			Assert.False(fixture.Service.BlockingBatchDisposed.Task.IsCompleted);
			Assert.False(fixture.Service.BlockingBatchSecondMoveWaiting.Task.IsCompleted);

			fixture.Service.ReleaseBlockingBatch.TrySetResult(true);
			await fixture.Service.BlockingBatchDisposed.Task.WithCancellation(this.TimeoutToken);
			Assert.True(fixture.Service.BlockingBatchCanceled);
			Assert.False(fixture.Service.BlockingBatchSecondMoveWaiting.Task.IsCompleted);
			await Assert.ThrowsAnyAsync<Exception>(() => enumeration);
			await this.AwaitCompletionAsync(fixture.ClientRpc.Completion);
		}
		finally
		{
			fixture.Service.ReleaseBlockingBatch.TrySetResult(true);
			fixture.Service.ReleaseBlockingBatchSecondMove.TrySetResult(true);
		}
	}

	/// <summary>
	/// A cancellation callback that waits for connection completion must not run on the shutdown path that signals that completion.
	/// </summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <param name="disconnect">How the connection is severed.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.ClientDisposed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.ClientDisposed)]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.TransportClosed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.TransportClosed)]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.ServerDisposed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.ServerDisposed)]
	public async Task ConnectionLossSurvivesCancellationCallbackThatWaitsForCompletion(JsonRpcEncoding encoding, ConnectionLoss disconnect)
	{
		using Fixture fixture = new(encoding);
		fixture.Service.CancellationCallbackBlocksOn = fixture.ServerRpc.Completion;
		Task enumeration = this.EnumerateAsync(fixture.Client.GetPrefetchedSequenceWhoseCancellationBlocksAsync(this.TimeoutToken));
		await fixture.Service.PrefetchedCancellationBlockWaiting.Task.WithCancellation(this.TimeoutToken);

		await fixture.SeverAsync(disconnect);

		await this.AwaitCompletionAsync(fixture.ServerRpc.Completion);
		await fixture.Service.BlockingCancellationFinished.Task.WithCancellation(this.TimeoutToken);
		await fixture.Service.PrefetchedCancellationBlockDisposed.Task.WithCancellation(this.TimeoutToken);
		await Assert.ThrowsAnyAsync<Exception>(() => enumeration);
		await this.AwaitCompletionAsync(fixture.ClientRpc.Completion);
	}

	/// <summary>Prefetching again before enumeration keeps the buffered prefix and the values that follow it.</summary>
	/// <returns>A task representing the test.</returns>
	[Test]
	public async Task RepeatedPrefetchKeepsBufferedValues()
	{
		IAsyncEnumerable<int> sequence = await Enumerable.Range(0, 5).AsAsyncEnumerable().WithPrefetchAsync(2, this.TimeoutToken);
		sequence = await sequence.WithPrefetchAsync(4, this.TimeoutToken);
		List<int> values = [];
		await foreach (int value in sequence.WithCancellation(this.TimeoutToken))
		{
			values.Add(value);
		}

		Assert.Equal(new[] { 0, 1, 2, 3, 4 }, values);
	}

	/// <summary>
	/// A partially prefetched sequence has one owner. Enumerating it again, or prefetching after that, is rejected
	/// without dropping the values the first enumerator still has to produce.
	/// </summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task PartiallyPrefetchedSequenceRejectsASecondOwner(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		IAsyncEnumerable<int> sequence = await Enumerable.Range(0, 5).AsAsyncEnumerable().WithPrefetchAsync(2, this.TimeoutToken);
		await using IAsyncEnumerator<int> first = sequence.GetAsyncEnumerator(this.TimeoutToken);
		Assert.True(await first.MoveNextAsync());
		Assert.Equal(0, first.Current);
		Assert.Throws<InvalidOperationException>(() => sequence.GetAsyncEnumerator(CancellationToken.None));
		await Assert.ThrowsAsync<InvalidOperationException>(() => sequence.WithPrefetchAsync(4, this.TimeoutToken).AsTask());
		Exception serialization = await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.SumAsync(sequence, this.TimeoutToken));
		Assert.IsType<InvalidOperationException>(serialization.InnerException);

		List<int> values = [first.Current];
		while (await first.MoveNextAsync())
		{
			values.Add(first.Current);
		}

		Assert.Equal(new[] { 0, 1, 2, 3, 4 }, values);
	}

	/// <summary>
	/// An iterator suspended at a yield after prefetch must observe cancellation when the connection drops, even if disposal wins the race with the cancel worker.
	/// </summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <param name="disconnect">How the connection is severed.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.ClientDisposed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.ClientDisposed)]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.TransportClosed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.TransportClosed)]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.ServerDisposed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.ServerDisposed)]
	public async Task ConnectionLossCancelsIteratorSuspendedAfterPrefetch(JsonRpcEncoding encoding, ConnectionLoss disconnect)
	{
		using Fixture fixture = new(encoding);
		await using IAsyncEnumerator<int> enumerator = fixture.Client.GetPrefetchedSuspendedYieldAsync(this.TimeoutToken).GetAsyncEnumerator(this.TimeoutToken);
		Assert.True(await enumerator.MoveNextAsync());
		Assert.Equal(0, enumerator.Current);

		await fixture.SeverAsync(disconnect);

		await fixture.Service.SuspendedYieldDisposed.Task.WithCancellation(this.TimeoutToken);
		Assert.True(fixture.Service.SuspendedYieldCanceled);
		await this.AwaitCompletionAsync(fixture.ServerRpc.Completion);
		await this.AwaitCompletionAsync(fixture.ClientRpc.Completion);
	}

	/// <summary>
	/// A cancellation callback registered by an in-flight iterator must not keep that iterator alive by waiting for its disposal.
	/// </summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <param name="disconnect">How the connection is severed.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.ClientDisposed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.ClientDisposed)]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.TransportClosed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.TransportClosed)]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.ServerDisposed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.ServerDisposed)]
	public async Task ConnectionLossDisposesIteratorWhenCancellationCallbackWaitsForDisposal(JsonRpcEncoding encoding, ConnectionLoss disconnect)
	{
		using Fixture fixture = new(encoding);
		await using IAsyncEnumerator<int> enumerator = fixture.Client.GetPrefetchedSequenceWhoseCancellationWaitsForDisposalAsync(this.TimeoutToken).GetAsyncEnumerator(this.TimeoutToken);
		Assert.True(await enumerator.MoveNextAsync());
		Assert.Equal(0, enumerator.Current);

		await fixture.SeverAsync(disconnect);

		await fixture.Service.PrefetchedDisposalWaitDisposed.Task.WithCancellation(this.TimeoutToken);
		Assert.True(fixture.Service.DisposalWaitCanceled);
		await fixture.Service.DisposalWaitCallbackFinished.Task.WithCancellation(this.TimeoutToken);
		await this.AwaitCompletionAsync(fixture.ServerRpc.Completion);
		await this.AwaitCompletionAsync(fixture.ClientRpc.Completion);
	}

	/// <summary>A prefetch whose token is already canceled does not produce values from a synchronous sequence.</summary>
	/// <returns>A task representing the test.</returns>
	[Test]
	public async Task PrefetchHonorsAnAlreadyCanceledToken()
	{
		using CancellationTokenSource canceled = new();
		canceled.Cancel();
		ValueTask<IAsyncEnumerable<int>> prefetch = Enumerable.Range(0, 5).AsAsyncEnumerable().WithPrefetchAsync(5, canceled.Token);
		await Assert.ThrowsAsync<OperationCanceledException>(() => prefetch.AsTask());
	}

	/// <summary>
	/// A callback registered on the iterator token must not block disposal, and <see langword="finally"/> still observes cancellation.
	/// </summary>
	/// <returns>A task representing the test.</returns>
	[Test]
	public async Task PrefetchedIteratorDisposalSurvivesCancellationCallback()
	{
		CancellationCallbackSequence sequence = new();
		IAsyncEnumerable<int> prefetched = await sequence.Enumerate().WithPrefetchAsync(1, CancellationToken.None);
		using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
		await using IAsyncEnumerator<int> enumerator = prefetched.GetAsyncEnumerator(stop.Token);
		Assert.True(await enumerator.MoveNextAsync());
		Assert.Equal(0, enumerator.Current);

		stop.Cancel();
		await enumerator.DisposeAsync();
		Assert.True(sequence.SawCanceled);
		await sequence.CallbackFinished.Task.WithCancellation(this.TimeoutToken);
	}

	/// <summary>
	/// A later enumeration token cancels the sequence's own source. The token the iterator captured stays usable,
	/// and a callback that blocks on <see langword="finally"/> still completes.
	/// </summary>
	/// <returns>A task representing the test.</returns>
	[Test]
	public async Task PrefetchedIteratorObservesDistinctEnumerationToken()
	{
		using CancellationTokenSource captured = new();
		DistinctTokenIterator iterator = new();
		IAsyncEnumerable<int> prefetched = await iterator.Produce(captured.Token).WithPrefetchAsync(1, captured.Token);
		using CancellationTokenSource enumeration = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
		await using IAsyncEnumerator<int> enumerator = prefetched.GetAsyncEnumerator(enumeration.Token);
		Assert.True(await enumerator.MoveNextAsync());
		Assert.Equal(0, enumerator.Current);

		enumeration.Cancel();
		await enumerator.DisposeAsync();
		Assert.False(captured.IsCancellationRequested);
		Assert.True(iterator.SawCanceled);
		await iterator.CallbackFinished.Task.WithCancellation(this.TimeoutToken);
	}

	/// <summary>
	/// A callback registered on the prefetch token ahead of the compiler's link must not let disposal drop that link.
	/// </summary>
	/// <returns>A task representing the test.</returns>
	[Test]
	public async Task PrefetchDisposalObservesCompilerLinkedTokenDespiteEarlierCallback()
	{
#if NET
		if (!RuntimeFeature.IsDynamicCodeSupported)
		{
			Skip.Test("NativeAOT does not expose the compiler-generated linked cancellation field this race reaches.");
			return;
		}
#endif

		using CancellationTokenSource captured = new();
		LinkedTokenRaceIterator iterator = new();
		IAsyncEnumerable<int> prefetched = await iterator.Produce(captured.Token).WithPrefetchAsync(1, captured.Token);
		using CancellationTokenSource enumeration = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
		IAsyncEnumerator<int> enumerator = prefetched.GetAsyncEnumerator(enumeration.Token);
		Assert.True(await enumerator.MoveNextAsync());
		Assert.Equal(1, enumerator.Current);

		enumeration.Cancel();
		Task dispose = enumerator.DisposeAsync().AsTask();
		try
		{
			await iterator.HoldingBeforeLink.Task.WithCancellation(this.TimeoutToken);
			await dispose.WithCancellation(this.TimeoutToken);
			Assert.False(captured.IsCancellationRequested);
			Assert.True(iterator.SawCanceled);
		}
		finally
		{
			iterator.ReleaseBeforeLink.TrySetResult(true);
		}

		await iterator.BeforeLinkFinished.Task.WithCancellation(this.TimeoutToken);
	}

	/// <summary>
	/// Canceling a call that is still pulling a prefetched argument cancels the compiler-linked token
	/// even when an earlier callback holds the prefetch token. Covers both wire encodings.
	/// </summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task PrefetchedArgumentObservesCompilerLinkedToken(JsonRpcEncoding encoding)
	{
#if NET
		if (!RuntimeFeature.IsDynamicCodeSupported)
		{
			Skip.Test("NativeAOT does not expose the compiler-generated linked cancellation field this race reaches.");
			return;
		}
#endif

		using Fixture fixture = new(encoding);
		using CancellationTokenSource captured = new();
		using CancellationTokenSource call = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
		LinkedTokenRaceIterator iterator = new();
		Task<int> sum = fixture.Client.SumAsync(
			await iterator.Produce(captured.Token, blockAfterFirst: true).WithPrefetchAsync(1, captured.Token),
			call.Token);
		await iterator.Blocked.Task.WithCancellation(this.TimeoutToken);
		call.Cancel();
		await Assert.ThrowsAnyAsync<Exception>(() => sum);

		try
		{
			await iterator.HoldingBeforeLink.Task.WithCancellation(this.TimeoutToken);
			await iterator.FinallyObserved.Task.WithCancellation(this.TimeoutToken);
			Assert.False(captured.IsCancellationRequested);
			Assert.True(iterator.SawCanceled);
		}
		finally
		{
			iterator.ReleaseBeforeLink.TrySetResult(true);
		}

		await iterator.BeforeLinkFinished.Task.WithCancellation(this.TimeoutToken);
	}

	/// <summary>
	/// A prefetch whose caller cancels inside <see cref="IAsyncEnumerable{T}.GetAsyncEnumerator"/> must not run
	/// linked-token callbacks on the prefetch stack. Those callbacks can wait for <see langword="finally"/>.
	/// </summary>
	/// <returns>A task representing the test.</returns>
	[Test]
	public async Task PrefetchCatchUpCancellationDoesNotDeadlock()
	{
#if NET
		if (!RuntimeFeature.IsDynamicCodeSupported)
		{
			Skip.Test("NativeAOT does not expose the compiler-generated linked cancellation field this race reaches.");
			return;
		}
#endif

		using CancellationTokenSource captured = new();
		using CancellationTokenSource caller = new();
		LinkedCatchUpIterator iterator = new();
		Task prefetch = iterator.Produce(captured.Token, caller).WithPrefetchAsync(1, caller.Token).AsTask();
		await Assert.ThrowsAsync<OperationCanceledException>(() => prefetch.WithCancellation(this.TimeoutToken));
		Assert.False(this.TimeoutToken.IsCancellationRequested);
		Assert.False(captured.IsCancellationRequested);
		Assert.True(iterator.SawCanceled);
		await iterator.CallbackFinished.Task.WithCancellation(this.TimeoutToken);
	}

	/// <summary>
	/// A linked-token callback that waits for <see langword="finally"/> must not keep the prefetch token from being canceled.
	/// <see langword="finally"/> waits for that prefetch token.
	/// </summary>
	/// <returns>A task representing the test.</returns>
	[Test]
	public async Task PrefetchCancelSurvivesLinkedCallbackWaitingForOriginalToken()
	{
#if NET
		if (!RuntimeFeature.IsDynamicCodeSupported)
		{
			Skip.Test("NativeAOT does not expose the compiler-generated linked cancellation field this race reaches.");
			return;
		}
#endif

		using CancellationTokenSource captured = new();
		LinkedCallbackWaitsForOriginalTokenIterator iterator = new();
		IAsyncEnumerable<int> prefetched = await iterator.Produce(captured.Token).WithPrefetchAsync(1, captured.Token);
		using CancellationTokenSource enumeration = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
		IAsyncEnumerator<int> enumerator = prefetched.GetAsyncEnumerator(enumeration.Token);
		Assert.True(await enumerator.MoveNextAsync());
		Assert.Equal(1, enumerator.Current);

		enumeration.Cancel();
		await enumerator.DisposeAsync().AsTask().WithCancellation(this.TimeoutToken);
		Assert.False(this.TimeoutToken.IsCancellationRequested);
		Assert.False(captured.IsCancellationRequested);
		Assert.True(iterator.SawCanceled);
		await iterator.CallbackFinished.Task.WithCancellation(this.TimeoutToken);
	}

	/// <summary>
	/// Canceling a call while a linked callback waits for <see langword="finally"/> still cancels the prefetch token
	/// that <see langword="finally"/> is waiting on. Covers both wire encodings.
	/// </summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task PrefetchedArgumentCancelSurvivesLinkedCallbackWaitingForOriginalToken(JsonRpcEncoding encoding)
	{
#if NET
		if (!RuntimeFeature.IsDynamicCodeSupported)
		{
			Skip.Test("NativeAOT does not expose the compiler-generated linked cancellation field this race reaches.");
			return;
		}
#endif

		using Fixture fixture = new(encoding);
		using CancellationTokenSource captured = new();
		using CancellationTokenSource call = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
		LinkedCallbackWaitsForOriginalTokenIterator iterator = new();
		Task<int> sum = fixture.Client.SumAsync(
			await iterator.Produce(captured.Token, blockAfterFirst: true).WithPrefetchAsync(1, captured.Token),
			call.Token);
		await iterator.Blocked.Task.WithCancellation(this.TimeoutToken);
		call.Cancel();
		await Assert.ThrowsAnyAsync<Exception>(() => sum);
		await iterator.FinallyFinished.Task.WithCancellation(this.TimeoutToken);
		Assert.False(this.TimeoutToken.IsCancellationRequested);
		Assert.False(captured.IsCancellationRequested);
		Assert.True(iterator.SawCanceled);
		await iterator.CallbackFinished.Task.WithCancellation(this.TimeoutToken);
	}

	/// <summary>
	/// Once the compiler-linked source has been disposed, disposal must not wait on the prefetch token.
	/// The linked callback is still inside cancellation and is waiting for that disposal.
	/// </summary>
	/// <returns>A task representing the test.</returns>
	[Test]
	public async Task DisposedLinkedSourceDoesNotWaitOnPrefetchToken()
	{
#if NET
		if (!RuntimeFeature.IsDynamicCodeSupported)
		{
			Skip.Test("NativeAOT does not expose the compiler-generated linked cancellation field this race reaches.");
			return;
		}
#endif

		using CancellationTokenSource captured = new();
		DisposedLinkedSourceIterator iterator = new();
		IAsyncEnumerable<int> prefetched = await iterator.Produce(captured.Token).WithPrefetchAsync(1, captured.Token);
		using CancellationTokenSource enumeration = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
		IAsyncEnumerator<int> enumerator = prefetched.GetAsyncEnumerator(enumeration.Token);
		Assert.True(await enumerator.MoveNextAsync());

		enumeration.Cancel();
		await iterator.Holding.Task.WithCancellation(this.TimeoutToken);
		Task dispose = enumerator.DisposeAsync().AsTask();
		try
		{
			await dispose.WithCancellation(this.TimeoutToken);
			Assert.False(this.TimeoutToken.IsCancellationRequested);
			Assert.False(captured.IsCancellationRequested);
			Assert.True(iterator.SawCanceled);
		}
		finally
		{
			iterator.Release.TrySetResult(true);
		}

		await iterator.CallbackFinished.Task.WithCancellation(this.TimeoutToken);
	}

	/// <summary>
	/// A prefetched argument whose compiler-linked source is disposed from a linked callback still finishes disposal
	/// when the call is canceled. Covers both wire encodings.
	/// </summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task PrefetchedArgumentDoesNotWaitOnPrefetchTokenAfterLinkedSourceDisposal(JsonRpcEncoding encoding)
	{
#if NET
		if (!RuntimeFeature.IsDynamicCodeSupported)
		{
			Skip.Test("NativeAOT does not expose the compiler-generated linked cancellation field this race reaches.");
			return;
		}
#endif

		using Fixture fixture = new(encoding);
		using CancellationTokenSource captured = new();
		using CancellationTokenSource call = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
		DisposedLinkedSourceIterator iterator = new();
		Task<int> sum = fixture.Client.SumAsync(
			await iterator.Produce(captured.Token, blockAfterFirst: true).WithPrefetchAsync(1, captured.Token),
			call.Token);
		await iterator.Blocked.Task.WithCancellation(this.TimeoutToken);
		call.Cancel();
		await iterator.Holding.Task.WithCancellation(this.TimeoutToken);
		try
		{
			await iterator.FinallyObserved.Task.WithCancellation(this.TimeoutToken);
			await Assert.ThrowsAnyAsync<Exception>(() => sum);
			Assert.False(this.TimeoutToken.IsCancellationRequested);
			Assert.False(captured.IsCancellationRequested);
			Assert.True(iterator.SawCanceled);
		}
		finally
		{
			iterator.Release.TrySetResult(true);
		}

		await iterator.CallbackFinished.Task.WithCancellation(this.TimeoutToken);
	}

	/// <summary>
	/// After prefetch, canceling the token that was supplied to prefetch does not cancel the token the iterator captured.
	/// </summary>
	/// <returns>A task representing the test.</returns>
	[Test]
	public async Task CancelingThePrefetchTokenLeavesTheCapturedTokenUsable()
	{
		using CancellationTokenSource captured = new();
		using CancellationTokenSource prefetch = new();
		DistinctCapturedIterator iterator = new();
		IAsyncEnumerable<int> prefetched = await iterator.Produce(captured.Token).WithPrefetchAsync(1, prefetch.Token);
		prefetch.Cancel();

		await using IAsyncEnumerator<int> enumerator = prefetched.GetAsyncEnumerator(this.TimeoutToken);
		Assert.True(await enumerator.MoveNextAsync());
		await enumerator.DisposeAsync();
		Assert.False(captured.IsCancellationRequested);
		Assert.False(iterator.SawCanceled);
	}

	/// <summary>
	/// Sending a partially prefetched sequence with the same token as the call must not cancel that token
	/// when the remote consumer finishes the sequence.
	/// </summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task PrefetchedArgumentKeepsTheSharedCallToken(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		using CancellationTokenSource shared = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
		SharedTokenIterator iterator = new();
		int sum = await fixture.Client.SumAsync(
			await iterator.Produce(shared.Token).WithPrefetchAsync(1, shared.Token),
			shared.Token);

		Assert.Equal(3, sum);
		await iterator.Disposed.Task.WithCancellation(this.TimeoutToken);
		Assert.False(shared.IsCancellationRequested);
	}

	/// <summary>
	/// Disposing the prefetch token after values were buffered must not prevent enumeration or strand the iterator.
	/// </summary>
	/// <returns>A task representing the test.</returns>
	[Test]
	public async Task DisposingThePrefetchTokenDoesNotStrandTheIterator()
	{
		OneValueThenSuspended sequence = new();
		CancellationTokenSource prefetch = new();
		IAsyncEnumerable<int> prefetched = await sequence.WithPrefetchAsync(1, prefetch.Token);
		Assert.True(sequence.Started);
		prefetch.Dispose();

		using CancellationTokenSource enumeration = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
		await using IAsyncEnumerator<int> enumerator = prefetched.GetAsyncEnumerator(enumeration.Token);
		Assert.True(await enumerator.MoveNextAsync());
		enumeration.Cancel();
		await enumerator.DisposeAsync();
		await sequence.Disposed.Task.WithCancellation(this.TimeoutToken);
		Assert.True(sequence.DisposeObservedCancellation);
	}

	/// <summary>
	/// Dropping the connection while prefetch is blocked must not run a completion-waiting callback on the shutdown path.
	/// </summary>
	/// <param name="encoding">The wire encoding.</param>
	/// <param name="disconnect">How the connection is severed.</param>
	/// <returns>A task representing the test.</returns>
	[Test]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.ClientDisposed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.ClientDisposed)]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.TransportClosed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.TransportClosed)]
	[Arguments(JsonRpcEncoding.Json, ConnectionLoss.ServerDisposed)]
	[Arguments(JsonRpcEncoding.MessagePack, ConnectionLoss.ServerDisposed)]
	public async Task ConnectionLossDuringBlockedPrefetchSurvivesCancellationCallback(JsonRpcEncoding encoding, ConnectionLoss disconnect)
	{
		using Fixture fixture = new(encoding);
		fixture.Service.CancellationCallbackBlocksOn = fixture.ServerRpc.Completion;
		Task enumeration = this.EnumerateAsync(fixture.Client.GetSequenceThatBlocksDuringPrefetchAsync(this.TimeoutToken));
		await fixture.Service.PrefetchDuringBlockWaiting.Task.WithCancellation(this.TimeoutToken);

		await fixture.SeverAsync(disconnect);

		await this.AwaitCompletionAsync(fixture.ServerRpc.Completion);
		await fixture.Service.BlockingCancellationFinished.Task.WithCancellation(this.TimeoutToken);
		await fixture.Service.PrefetchDuringBlockDisposed.Task.WithCancellation(this.TimeoutToken);
		await Assert.ThrowsAnyAsync<Exception>(() => enumeration);
		await this.AwaitCompletionAsync(fixture.ClientRpc.Completion);
	}

	/// <summary>A second prefetch that is already canceled still disposes the iterator started by the first prefetch.</summary>
	/// <returns>A task representing the test.</returns>
	[Test]
	public async Task CanceledRepeatPrefetchDisposesExistingIterator()
	{
		OneValueThenSuspended sequence = new();
		using CancellationTokenSource prefetchCancellation = new();
		IAsyncEnumerable<int> prefetched = await sequence.WithPrefetchAsync(1, prefetchCancellation.Token);
		Assert.True(sequence.Started);
		Assert.False(sequence.Disposed.Task.IsCompleted);

		using CancellationTokenSource canceled = new();
		canceled.Cancel();
		await Assert.ThrowsAsync<OperationCanceledException>(() => prefetched.WithPrefetchAsync(1, canceled.Token).AsTask());
		await sequence.Disposed.Task.WithCancellation(this.TimeoutToken);
		Assert.True(sequence.DisposeObservedCancellation);
	}

	/// <summary>Canceling prefetch unblocks disposal of an enumerator that waits on the token it was given.</summary>
	/// <returns>A task representing the test.</returns>
	[Test]
	public async Task CanceledPrefetchUnblocksEnumeratorDisposal()
	{
		DisposeBlocksUntilCanceled sequence = new();
		using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
		Task prefetch = sequence.WithPrefetchAsync(1, cancellation.Token).AsTask();
		await sequence.DisposeStarted.Task.WithCancellation(this.TimeoutToken);
		cancellation.Cancel();
		await Assert.ThrowsAsync<InvalidOperationException>(() => prefetch);
		Assert.True(sequence.DisposeObservedCancellation);
	}

	private async Task EnumerateAsync(IAsyncEnumerable<int> sequence)
	{
		await foreach (int value in sequence.WithCancellation(this.TimeoutToken))
		{
		}
	}

	private async Task AwaitCompletionAsync(Task completion)
	{
		try
		{
			await completion.WithCancellation(this.TimeoutToken);
		}
		catch (OperationCanceledException) when (this.TimeoutToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception)
		{
			// A dropped transport can fault Completion. Shutdown must still finish.
		}
	}

	private sealed class CancellationCallbackSequence
	{
		private int sawCanceled;

		internal bool SawCanceled => Volatile.Read(ref this.sawCanceled) != 0;

		internal TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal TaskCompletionSource<bool> CallbackFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public IAsyncEnumerable<int> Enumerate() => this.Run();

		private async IAsyncEnumerable<int> Run([EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			try
			{
				// Registered during prefetch, before the later enumeration token is canceled.
				cancellationToken.Register(
					static state =>
					{
						CancellationCallbackSequence sequence = (CancellationCallbackSequence)state!;
#pragma warning disable VSTHRD002 // The callback blocks on disposal; that is the hazard under test.
						sequence.Disposed.Task.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
						sequence.CallbackFinished.TrySetResult(true);
					},
					this);
				yield return 0;
				yield return 1;
			}
			finally
			{
				Volatile.Write(ref this.sawCanceled, cancellationToken.IsCancellationRequested ? 1 : 0);
				this.Disposed.TrySetResult(true);
			}
		}
	}

	private sealed class DistinctTokenIterator
	{
		private int sawCanceled;

		internal bool SawCanceled => Volatile.Read(ref this.sawCanceled) != 0;

		internal TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal TaskCompletionSource<bool> CallbackFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public IAsyncEnumerable<int> Produce(CancellationToken captured) => this.Run(captured);

		private async IAsyncEnumerable<int> Run([EnumeratorCancellation] CancellationToken cancellationToken)
		{
			try
			{
				cancellationToken.Register(
					static state =>
					{
						DistinctTokenIterator iterator = (DistinctTokenIterator)state!;
#pragma warning disable VSTHRD002 // The callback blocks on disposal; that is the hazard under test.
						iterator.Disposed.Task.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
						iterator.CallbackFinished.TrySetResult(true);
					},
					this);
				yield return 0;
				yield return 1;
			}
			finally
			{
				Volatile.Write(ref this.sawCanceled, cancellationToken.IsCancellationRequested ? 1 : 0);
				this.Disposed.TrySetResult(true);
			}
		}
	}

	/// <summary>
	/// Compiler iterator whose prefetch token gets a callback registered after the compiler link,
	/// so that callback runs first and can hold the prefetch token's callback list.
	/// </summary>
	private sealed class LinkedTokenRaceIterator
	{
		private int sawCanceled;

		internal bool SawCanceled => Volatile.Read(ref this.sawCanceled) != 0;

		internal TaskCompletionSource<bool> HoldingBeforeLink { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal TaskCompletionSource<bool> ReleaseBeforeLink { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal TaskCompletionSource<bool> BeforeLinkFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal TaskCompletionSource<bool> Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal TaskCompletionSource<bool> FinallyObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public IAsyncEnumerable<int> Produce(CancellationToken captured, bool blockAfterFirst = false) => new RaceEnumerable(this, captured, blockAfterFirst);

		private async IAsyncEnumerable<int> Run(bool blockAfterFirst, [EnumeratorCancellation] CancellationToken cancellationToken)
		{
			try
			{
				yield return 1;
				if (blockAfterFirst)
				{
					this.Blocked.TrySetResult(true);
					await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
				}

				yield return 2;
			}
			finally
			{
				Volatile.Write(ref this.sawCanceled, cancellationToken.IsCancellationRequested ? 1 : 0);
				this.FinallyObserved.TrySetResult(true);
			}
		}

		private sealed class RaceEnumerable(LinkedTokenRaceIterator owner, CancellationToken captured, bool blockAfterFirst) : IAsyncEnumerable<int>
		{
			public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
			{
				IAsyncEnumerator<int> inner = owner.Run(blockAfterFirst, captured).GetAsyncEnumerator(cancellationToken);
				if (cancellationToken.CanBeCanceled)
				{
					// Registered after the compiler link, so cancellation runs this first and can drop the link
					// if disposal proceeds from the prefetch token's wait handle.
					cancellationToken.Register(
						static state =>
						{
							LinkedTokenRaceIterator iterator = (LinkedTokenRaceIterator)state!;
							iterator.HoldingBeforeLink.TrySetResult(true);
#pragma warning disable VSTHRD002 // The callback holds the prefetch token until the test releases it.
							iterator.ReleaseBeforeLink.Task.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
							iterator.BeforeLinkFinished.TrySetResult(true);
						},
						owner);
				}

				return inner;
			}
		}
	}

	/// <summary>
	/// Starts a compiler iterator, registers a callback that waits for <see langword="finally"/>, then cancels prefetch.
	/// </summary>
	private sealed class LinkedCatchUpIterator
	{
		private int sawCanceled;

		internal bool SawCanceled => Volatile.Read(ref this.sawCanceled) != 0;

		internal TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal TaskCompletionSource<bool> CallbackFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public IAsyncEnumerable<int> Produce(CancellationToken captured, CancellationTokenSource caller) => new Enumerable(this, captured, caller);

		private async IAsyncEnumerable<int> Run([EnumeratorCancellation] CancellationToken cancellationToken)
		{
			try
			{
				cancellationToken.Register(
					static state =>
					{
						LinkedCatchUpIterator iterator = (LinkedCatchUpIterator)state!;
#pragma warning disable VSTHRD002 // The callback waits for finally; that is the hazard under test.
						iterator.Disposed.Task.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
						iterator.CallbackFinished.TrySetResult(true);
					},
					this);
				yield return 1;
				yield return 2;
			}
			finally
			{
				Volatile.Write(ref this.sawCanceled, cancellationToken.IsCancellationRequested ? 1 : 0);
				this.Disposed.TrySetResult(true);
			}
		}

		private sealed class Enumerable(LinkedCatchUpIterator owner, CancellationToken captured, CancellationTokenSource caller) : IAsyncEnumerable<int>
		{
			public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
			{
				IAsyncEnumerator<int> inner = owner.Run(captured).GetAsyncEnumerator(cancellationToken);
#pragma warning disable VSTHRD002 // The first move registers the linked-token callback before prefetch continues.
				inner.MoveNextAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
				caller.Cancel();
				return inner;
			}
		}
	}

	/// <summary>
	/// Linked callback waits until <see langword="finally"/> finishes, and <see langword="finally"/> waits for the prefetch token.
	/// </summary>
	private sealed class LinkedCallbackWaitsForOriginalTokenIterator
	{
		private int sawCanceled;

		internal bool SawCanceled => Volatile.Read(ref this.sawCanceled) != 0;

		internal TaskCompletionSource<bool> Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal TaskCompletionSource<bool> FinallyFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal TaskCompletionSource<bool> CallbackFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public IAsyncEnumerable<int> Produce(CancellationToken captured, bool blockAfterFirst = false) => new Enumerable(this, captured, blockAfterFirst);

		private async IAsyncEnumerable<int> Run(CancellationToken original, bool blockAfterFirst, [EnumeratorCancellation] CancellationToken cancellationToken)
		{
			static bool IsRequested(CancellationToken token)
			{
				try
				{
					return token.IsCancellationRequested;
				}
				catch (ObjectDisposedException)
				{
					return true;
				}
			}

			try
			{
				cancellationToken.Register(
					static state =>
					{
						LinkedCallbackWaitsForOriginalTokenIterator iterator = (LinkedCallbackWaitsForOriginalTokenIterator)state!;
#pragma warning disable VSTHRD002 // The callback waits for finally, which waits for the prefetch token.
						iterator.FinallyFinished.Task.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
						iterator.CallbackFinished.TrySetResult(true);
					},
					this);
				yield return 1;
				if (blockAfterFirst)
				{
					this.Blocked.TrySetResult(true);
					await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
				}

				yield return 2;
			}
			finally
			{
				Volatile.Write(ref this.sawCanceled, IsRequested(cancellationToken) ? 1 : 0);
				if (original.CanBeCanceled && !IsRequested(original))
				{
					// Await the flag instead of blocking a thread. A synchronous wait here sits on the
					// DisposeAsync caller and a regression then hangs the test past its timeout.
					TaskCompletionSource<bool> signaled = new(TaskCreationOptions.RunContinuationsAsynchronously);
					try
					{
						using CancellationTokenRegistration registration = original.Register(
							static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
							signaled);
						if (!IsRequested(original))
						{
							await signaled.Task.ConfigureAwait(false);
						}
					}
					catch (ObjectDisposedException)
					{
					}
				}

				this.FinallyFinished.TrySetResult(true);
			}
		}

		private sealed class Enumerable(LinkedCallbackWaitsForOriginalTokenIterator owner, CancellationToken captured, bool blockAfterFirst) : IAsyncEnumerable<int>
		{
			public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
				=> owner.Run(cancellationToken, blockAfterFirst, captured).GetAsyncEnumerator(cancellationToken);
		}
	}

	/// <summary>
	/// Disposes the compiler-linked source from a callback on that source, then waits for enumerator disposal.
	/// </summary>
	private sealed class DisposedLinkedSourceIterator
	{
		private int sawCanceled;

		internal bool SawCanceled => Volatile.Read(ref this.sawCanceled) != 0;

		internal TaskCompletionSource<bool> Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal TaskCompletionSource<bool> Holding { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal TaskCompletionSource<bool> FinallyObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal TaskCompletionSource<bool> CallbackFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public IAsyncEnumerable<int> Produce(CancellationToken captured, bool blockAfterFirst = false) => new Enumerable(this, captured, blockAfterFirst);

		private async IAsyncEnumerable<int> Run(bool blockAfterFirst, [EnumeratorCancellation] CancellationToken cancellationToken)
		{
			try
			{
				yield return 1;
				if (blockAfterFirst)
				{
					this.Blocked.TrySetResult(true);
					await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
				}

				yield return 2;
			}
			finally
			{
				bool canceled;
				try
				{
					canceled = cancellationToken.IsCancellationRequested;
				}
				catch (ObjectDisposedException)
				{
					canceled = true;
				}

				Volatile.Write(ref this.sawCanceled, canceled ? 1 : 0);
				this.FinallyObserved.TrySetResult(true);
			}
		}

		private sealed class Enumerable(DisposedLinkedSourceIterator owner, CancellationToken captured, bool blockAfterFirst) : IAsyncEnumerable<int>
		{
			public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
			{
#if NET
				[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Test-only lookup of the C# compiler's <>x__combinedTokens field.")]
				[System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Test-only lookup of the C# compiler's <>x__combinedTokens field.")]
#endif
				static CancellationTokenSource? TryGetCompilerLinkedSource(object enumerator)
				{
					FieldInfo? field = enumerator.GetType().GetField(
						"<>x__combinedTokens",
						BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
					return field?.GetValue(enumerator) as CancellationTokenSource;
				}

				IAsyncEnumerator<int> inner = owner.Run(blockAfterFirst, captured).GetAsyncEnumerator(cancellationToken);
				CancellationTokenSource? linked = TryGetCompilerLinkedSource(inner);
				if (linked is not null)
				{
					linked.Token.Register(
						static state => ((CallbackHold)state!).Run(),
						new CallbackHold(owner, linked));
				}

				return inner;
			}

			private sealed class CallbackHold(DisposedLinkedSourceIterator iterator, CancellationTokenSource source)
			{
				public void Run()
				{
					source.Dispose();
					iterator.Holding.TrySetResult(true);
#pragma warning disable VSTHRD002 // The callback holds cancellation until disposal has passed the linked source.
					iterator.Release.Task.GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
					iterator.CallbackFinished.TrySetResult(true);
				}
			}
		}
	}

	private sealed class DistinctCapturedIterator
	{
		private int sawCanceled;

		internal bool SawCanceled => Volatile.Read(ref this.sawCanceled) != 0;

		public IAsyncEnumerable<int> Produce(CancellationToken captured) => this.Run(captured);

		private async IAsyncEnumerable<int> Run([EnumeratorCancellation] CancellationToken cancellationToken)
		{
			try
			{
				yield return 0;
				yield return 1;
			}
			finally
			{
				Volatile.Write(ref this.sawCanceled, cancellationToken.IsCancellationRequested ? 1 : 0);
			}
		}
	}

	private sealed class SharedTokenIterator
	{
		internal TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public IAsyncEnumerable<int> Produce(CancellationToken captured) => this.Run(captured);

		private async IAsyncEnumerable<int> Run([EnumeratorCancellation] CancellationToken cancellationToken)
		{
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				yield return 1;
				yield return 2;
			}
			finally
			{
				this.Disposed.TrySetResult(true);
			}
		}
	}

	private sealed class OneValueThenSuspended : IAsyncEnumerable<int>
	{
		private int started;
		private int disposeObservedCancellation;

		internal bool Started => Volatile.Read(ref this.started) != 0;

		internal TaskCompletionSource<bool> Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal bool DisposeObservedCancellation => Volatile.Read(ref this.disposeObservedCancellation) != 0;

		public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator(this, cancellationToken);

		private sealed class Enumerator(OneValueThenSuspended owner, CancellationToken cancellationToken) : IAsyncEnumerator<int>
		{
			private int index;

			public int Current => this.index;

			public ValueTask<bool> MoveNextAsync()
			{
				if (this.index >= 2)
				{
					return new ValueTask<bool>(false);
				}

				this.index++;
				Volatile.Write(ref owner.started, 1);
				return new ValueTask<bool>(true);
			}

			public ValueTask DisposeAsync()
			{
				Volatile.Write(ref owner.disposeObservedCancellation, cancellationToken.IsCancellationRequested ? 1 : 0);
				owner.Disposed.TrySetResult(true);
				return default;
			}
		}
	}

	private sealed class DisposeBlocksUntilCanceled : IAsyncEnumerable<int>
	{
		private int disposeObservedCancellation;

		internal TaskCompletionSource<bool> DisposeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		internal bool DisposeObservedCancellation => Volatile.Read(ref this.disposeObservedCancellation) != 0;

		public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator(this, cancellationToken);

		private sealed class Enumerator(DisposeBlocksUntilCanceled owner, CancellationToken cancellationToken) : IAsyncEnumerator<int>
		{
			public int Current => 0;

			public ValueTask<bool> MoveNextAsync() => new(false);

			public async ValueTask DisposeAsync()
			{
				owner.DisposeStarted.TrySetResult(true);
				try
				{
					await Task.Delay(Timeout.Infinite, cancellationToken);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					Volatile.Write(ref owner.disposeObservedCancellation, 1);
					throw new InvalidOperationException("Disposal failed after observing cancellation.");
				}
			}
		}
	}

	private sealed class TrackingSequence : IAsyncEnumerable<int>
	{
		internal bool EnumeratorDisposed { get; private set; }

		public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator(this);

		private sealed class Enumerator(TrackingSequence owner) : IAsyncEnumerator<int>
		{
			public int Current => 0;

			public ValueTask<bool> MoveNextAsync() => new(false);

			public ValueTask DisposeAsync()
			{
				owner.EnumeratorDisposed = true;
				return default;
			}
		}
	}

	private sealed class Fixture : IDisposable
	{
		private readonly IDuplexPipe clientPipe;
		private readonly JsonRpc clientRpc;
		private readonly JsonRpc serverRpc;

		internal Fixture(JsonRpcEncoding encoding)
		{
			(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
			this.clientPipe = clientPipe;
			this.clientRpc = new(CreateChannel(clientPipe, encoding));
			this.serverRpc = new(CreateChannel(serverPipe, encoding));
			this.Service = new AsyncEnumerableService();
			this.serverRpc.AddRpcTarget<IAsyncEnumerableService>(this.Service);
			this.serverRpc.Start();
			this.clientRpc.Start();
			this.Client = this.clientRpc.Attach<IAsyncEnumerableService>();
		}

		internal JsonRpc ClientRpc => this.clientRpc;

		internal JsonRpc ServerRpc => this.serverRpc;

		internal IAsyncEnumerableService Client { get; }

		internal AsyncEnumerableService Service { get; }

		/// <inheritdoc/>
		public void Dispose()
		{
			this.clientRpc.Dispose();
			this.serverRpc.Dispose();
		}

		/// <summary>Severs the connection without assuming which side is still running.</summary>
		/// <param name="disconnect">How to sever the connection.</param>
		/// <returns>A task that completes when the transport close has been requested.</returns>
		internal async Task SeverAsync(ConnectionLoss disconnect)
		{
			switch (disconnect)
			{
				case ConnectionLoss.ClientDisposed:
					this.clientRpc.Dispose();
					break;
				case ConnectionLoss.TransportClosed:
					await this.clientPipe.Output.CompleteAsync();
					await this.clientPipe.Input.CompleteAsync();
					break;
				case ConnectionLoss.ServerDisposed:
					this.serverRpc.Dispose();
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(disconnect));
			}
		}

		private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding)
			=> encoding == JsonRpcEncoding.Json
				? new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited)
				: new JsonRpcMessagePackChannel(pipe);
	}
}
