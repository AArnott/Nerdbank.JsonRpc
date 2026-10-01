// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Nerdbank.Streams;

public partial class AsyncEnumerableTests
{
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
		await Assert.ThrowsAsync<JsonRpcException>(async () =>
		{
			await foreach (int value in sequence)
			{
				received.Add(value);
			}
		});

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
		private readonly JsonRpc clientRpc;
		private readonly JsonRpc serverRpc;

		internal Fixture(JsonRpcEncoding encoding)
		{
			(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
			this.clientRpc = new(CreateChannel(clientPipe, encoding));
			this.serverRpc = new(CreateChannel(serverPipe, encoding));
			this.Service = new AsyncEnumerableService();
			this.serverRpc.AddRpcTarget<IAsyncEnumerableService>(this.Service);
			this.serverRpc.Start();
			this.clientRpc.Start();
			this.Client = this.clientRpc.Attach<IAsyncEnumerableService>();
		}

		internal JsonRpc ClientRpc => this.clientRpc;

		internal IAsyncEnumerableService Client { get; }

		internal AsyncEnumerableService Service { get; }

		public void Dispose()
		{
			this.clientRpc.Dispose();
			this.serverRpc.Dispose();
		}

		private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding)
			=> encoding == JsonRpcEncoding.Json
				? new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited)
				: new JsonRpcMessagePackChannel(pipe);
	}
}
