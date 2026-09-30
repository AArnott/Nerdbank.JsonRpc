// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using PolyType;

/// <summary>
/// Tests the ordering and concurrency guarantees that <see cref="JsonRpc.SynchronizationContext"/> provides
/// over the dispatch of inbound requests.
/// </summary>
public partial class DispatchOrderingTests : TestBase
{
	private readonly List<JsonRpc> connections = [];
	private readonly List<IDisposable> disposables = [];
	private readonly OrderTrackingServer server = new();

	[After(Test)]
	public void DisposeConnections()
	{
		// Release any RPC method still blocking a dispatcher thread so the connections can shut down.
		this.server.ReleaseBlockedCalls();

		foreach (JsonRpc connection in this.connections)
		{
			connection.Dispose();
		}

		foreach (IDisposable disposable in this.disposables)
		{
			disposable.Dispose();
		}
	}

	[Test]
	public async Task DefaultSynchronizationContext_StartsInvocationsInRequestOrder()
	{
		const int CallCount = 5;
		MockChannel<JsonRpcMessage> client = this.CreateConnection();

		for (int i = 0; i < CallCount; i++)
		{
			await client.Writer.WriteAsync(CreateRequest(i + 1, nameof(OrderTrackingServer.GatedAsync), i), this.TimeoutToken);
		}

		await this.server.WaitForStartCountAsync(CallCount, this.TimeoutToken);
		Assert.Equal([0, 1, 2, 3, 4], this.server.StartOrder);
	}

	[Test]
	public async Task DefaultSynchronizationContext_AllowsOutOfOrderCompletion()
	{
		const int CallCount = 3;
		MockChannel<JsonRpcMessage> client = this.CreateConnection();

		for (int i = 0; i < CallCount; i++)
		{
			await client.Writer.WriteAsync(CreateRequest(i + 1, nameof(OrderTrackingServer.GatedAsync), i), this.TimeoutToken);
		}

		// All three methods start (in order) without any of them having completed,
		// which proves that dispatch is not serialized end-to-end.
		await this.server.WaitForStartCountAsync(CallCount, this.TimeoutToken);
		Assert.Equal([0, 1, 2], this.server.StartOrder);

		// Complete them in an order unrelated to the order they started in, and observe
		// that responses arrive in that same completion order.
		foreach (int sequence in (int[])[2, 0, 1])
		{
			this.server.ReleaseGate(sequence);
			JsonRpcResult result = Assert.IsType<JsonRpcResult>(await client.Reader.ReadAsync(this.TimeoutToken));
			Assert.Equal(sequence + 1, result.Id);
		}
	}

	[Test]
	public async Task DefaultSynchronizationContext_SerializesInvocationsUntilTheyYield()
	{
		const int CallCount = 3;
		MockChannel<JsonRpcMessage> client = this.CreateConnection();

		// BlockUntilReleased never yields, so the ordered dispatcher cannot start the next call until it returns.
		for (int i = 0; i < CallCount; i++)
		{
			await client.Writer.WriteAsync(CreateRequest(i + 1, nameof(OrderTrackingServer.BlockUntilReleased), i), this.TimeoutToken);
		}

		await this.server.WaitForStartCountAsync(1, this.TimeoutToken);
		await Task.Delay(ExpectedTimeout, this.TimeoutToken);
		Assert.Equal([0], this.server.StartOrder);

		this.server.ReleaseBlockedCalls();
		await this.server.WaitForStartCountAsync(CallCount, this.TimeoutToken);
		Assert.Equal([0, 1, 2], this.server.StartOrder);
	}

	[Test]
	public async Task NullSynchronizationContext_DispatchesWithFullConcurrency()
	{
		const int CallCount = 3;
		MockChannel<JsonRpcMessage> client = this.CreateConnection(rpc => rpc.SynchronizationContext = null);

		// Without an ordering context every invocation is queued to the thread pool independently,
		// so all of them start even though none of them ever yields.
		for (int i = 0; i < CallCount; i++)
		{
			await client.Writer.WriteAsync(CreateRequest(i + 1, nameof(OrderTrackingServer.BlockUntilReleased), i), this.TimeoutToken);
		}

		await this.server.WaitForStartCountAsync(CallCount, this.TimeoutToken);
		this.server.ReleaseBlockedCalls();
	}

	[Test]
	public async Task CustomSynchronizationContext_StartsInvocationsOnThatContext()
	{
		const int CallCount = 3;
		DedicatedThreadSynchronizationContext syncContext = new();
		this.disposables.Add(syncContext);
		MockChannel<JsonRpcMessage> client = this.CreateConnection(rpc => rpc.SynchronizationContext = syncContext);

		for (int i = 0; i < CallCount; i++)
		{
			await client.Writer.WriteAsync(CreateRequest(i + 1, nameof(OrderTrackingServer.RecordThread), i), this.TimeoutToken);
		}

		await this.server.WaitForStartCountAsync(CallCount, this.TimeoutToken);
		Assert.Equal([0, 1, 2], this.server.StartOrder);
		Assert.All(this.server.InvocationThreadIds, id => Assert.Equal(syncContext.ThreadId, id));
	}

	[Test]
	public void SynchronizationContext_ThrowsWhenSetAfterStart()
	{
		this.CreateConnection();
		JsonRpc connection = this.connections[^1];
		Assert.Throws<InvalidOperationException>(() => connection.SynchronizationContext = null);
	}

	private static JsonRpcRequest CreateRequest(RequestId id, string method, int sequence)
	{
		Sequence<byte> seq = new();
		MessagePackWriter msgpackWriter = new(seq);
		msgpackWriter.WriteArrayHeader(1);
		msgpackWriter.Write(sequence);
		msgpackWriter.Flush();

		return new JsonRpcRequest { Id = id, Method = method, Arguments = (RawMessagePack)seq.AsReadOnlySequence };
	}

	private MockChannel<JsonRpcMessage> CreateConnection(Action<JsonRpc>? configure = null)
	{
		(MockChannel<JsonRpcMessage> client, Channel<JsonRpcMessage> serverChannel) = MockChannel<JsonRpcMessage>.CreatePair();
		JsonRpc connection = new(new MockJsonRpcPipeChannel(serverChannel));
		connection.AddRpcTarget(this.server, new JsonRpcTargetOptions { MethodNameTransform = CommonMethodNameTransforms.Identity });
		configure?.Invoke(connection);
		connection.Start();
		this.connections.Add(connection);
		return client;
	}

	/// <summary>An RPC target that records the order and thread on which its methods begin executing.</summary>
	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class OrderTrackingServer
	{
		private readonly List<int> startOrder = [];
		private readonly List<int> invocationThreadIds = [];
		private readonly ConcurrentDictionary<int, AsyncManualResetEvent> gates = new();
		private readonly ManualResetEventSlim blockRelease = new(initialState: false);

		/// <summary>Gets the sequence numbers of the calls that have begun executing, in the order they began.</summary>
		internal int[] StartOrder
		{
			get
			{
				lock (this.startOrder)
				{
					return [.. this.startOrder];
				}
			}
		}

		/// <summary>Gets the managed thread IDs that <see cref="RecordThread"/> invocations began on.</summary>
		internal int[] InvocationThreadIds
		{
			get
			{
				lock (this.startOrder)
				{
					return [.. this.invocationThreadIds];
				}
			}
		}

		/// <summary>Records that this call started, then waits for its own gate to be released.</summary>
		/// <param name="sequence">The caller-assigned sequence number of this call.</param>
		/// <param name="cancellationToken">A token to cancel the wait.</param>
		/// <returns>The <paramref name="sequence"/> that was passed in.</returns>
		public async Task<int> GatedAsync(int sequence, CancellationToken cancellationToken)
		{
			this.RecordStart(sequence);
			await this.GateFor(sequence).WaitAsync(cancellationToken);
			return sequence;
		}

		/// <summary>Records that this call started, then blocks its thread (without ever yielding) until released.</summary>
		/// <param name="sequence">The caller-assigned sequence number of this call.</param>
		/// <param name="cancellationToken">A token to cancel the wait.</param>
		/// <returns>The <paramref name="sequence"/> that was passed in.</returns>
		public int BlockUntilReleased(int sequence, CancellationToken cancellationToken)
		{
			this.RecordStart(sequence);
			this.blockRelease.Wait(cancellationToken);
			return sequence;
		}

		/// <summary>Records that this call started and the thread it started on, then returns immediately.</summary>
		/// <param name="sequence">The caller-assigned sequence number of this call.</param>
		/// <returns>The <paramref name="sequence"/> that was passed in.</returns>
		public int RecordThread(int sequence)
		{
			lock (this.startOrder)
			{
				this.invocationThreadIds.Add(Environment.CurrentManagedThreadId);
			}

			this.RecordStart(sequence);
			return sequence;
		}

		/// <summary>Allows the <see cref="GatedAsync"/> call with the given sequence number to complete.</summary>
		/// <param name="sequence">The sequence number of the call to release.</param>
		internal void ReleaseGate(int sequence) => this.GateFor(sequence).Set();

		/// <summary>Allows all <see cref="BlockUntilReleased"/> calls to complete.</summary>
		internal void ReleaseBlockedCalls() => this.blockRelease.Set();

		/// <summary>Waits until at least <paramref name="count"/> calls have begun executing.</summary>
		/// <param name="count">The number of calls to wait for.</param>
		/// <param name="cancellationToken">A token to cancel the wait.</param>
		/// <returns>A task that completes when enough calls have started.</returns>
		internal async Task WaitForStartCountAsync(int count, CancellationToken cancellationToken)
		{
			while (true)
			{
				lock (this.startOrder)
				{
					if (this.startOrder.Count >= count)
					{
						return;
					}
				}

				await Task.Delay(5, cancellationToken);
			}
		}

		private AsyncManualResetEvent GateFor(int sequence) => this.gates.GetOrAdd(sequence, static _ => new AsyncManualResetEvent());

		private void RecordStart(int sequence)
		{
			lock (this.startOrder)
			{
				this.startOrder.Add(sequence);
			}
		}
	}

	/// <summary>A <see cref="SynchronizationContext"/> that runs all posted callbacks on one dedicated thread.</summary>
	private sealed class DedicatedThreadSynchronizationContext : SynchronizationContext, IDisposable
	{
		private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
		private readonly Thread thread;

		internal DedicatedThreadSynchronizationContext()
		{
			this.thread = new Thread(this.Pump) { IsBackground = true, Name = "Test RPC dispatch thread" };
			this.thread.Start();
		}

		/// <summary>Gets the managed ID of the thread that posted callbacks run on.</summary>
		internal int ThreadId => this.thread.ManagedThreadId;

		/// <inheritdoc/>
		public override void Post(SendOrPostCallback d, object? state) => this.queue.Add((d, state));

		/// <inheritdoc/>
		public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

		/// <inheritdoc/>
		public void Dispose() => this.queue.CompleteAdding();

		private void Pump()
		{
			foreach ((SendOrPostCallback callback, object? state) in this.queue.GetConsumingEnumerable())
			{
				callback(state);
			}
		}
	}
}
