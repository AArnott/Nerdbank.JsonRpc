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
		await foreach (int value in await fixture.Client.GetNumbersAsync(5, CancellationToken.None))
		{
			received.Add(value);
		}

		Assert.Equal(new[] { 0, 1, 2, 3, 4 }, received);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task DirectlyReturnedSequenceStreamsAllValues(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		List<int> received = [];
		await foreach (int value in fixture.Client.GetNumbersDirect(5, CancellationToken.None))
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
		await foreach (int value in await fixture.Client.GetNumbersAsync(0, CancellationToken.None))
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
		await foreach (string value in await fixture.Client.GetWordsAsync(CancellationToken.None))
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
		IAsyncEnumerable<int> sequence = await fixture.Client.GetNumbersWithSettingsAsync(5, minBatchSize: 1, maxReadAhead: 0, prefetch: 3, CancellationToken.None);

		// The server must have produced the prefetched values before it answered the original request.
		Assert.True(await fixture.Client.CountGeneratedValuesAsync(CancellationToken.None) >= 3);

		List<int> received = [];
		await foreach (int value in sequence)
		{
			received.Add(value);
		}

		Assert.Equal(new[] { 0, 1, 2, 3, 4 }, received);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task PrefetchOfEntireSequenceRequiresNoToken(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		IAsyncEnumerable<int> sequence = await fixture.Client.GetNumbersWithSettingsAsync(3, minBatchSize: 1, maxReadAhead: 0, prefetch: 10, CancellationToken.None);

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
		IAsyncEnumerable<int> sequence = await fixture.Client.GetNumbersWithSettingsAsync(10, minBatchSize: 4, maxReadAhead: 0, prefetch: 0, CancellationToken.None);

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
		IAsyncEnumerable<int> sequence = await fixture.Client.GetNumbersWithSettingsAsync(20, minBatchSize: 1, maxReadAhead: 5, prefetch: 0, CancellationToken.None);

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
		IAsyncEnumerable<int> sequence = await fixture.Client.GetNumbersAsync(1000, CancellationToken.None);

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
		IAsyncEnumerable<int> sequence = await fixture.Client.GetNumbersAsync(3, CancellationToken.None);

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
		IAsyncEnumerable<int> sequence = await fixture.Client.GetFailingSequenceAsync(2, CancellationToken.None);

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
		IAsyncEnumerable<int> sequence = await fixture.Client.GetNumbersAsync(1000, CancellationToken.None);
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

		internal IAsyncEnumerableService Client { get; }

		internal AsyncEnumerableService Service { get; }

		public void Dispose()
		{
			this.clientRpc.Dispose();
			this.serverRpc.Dispose();
		}

		private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding)
			=> encoding == JsonRpcEncoding.Json
				? new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited, NullLogger.Instance)
				: new JsonRpcMessagePackChannel(pipe, NullLogger.Instance);
	}
}
