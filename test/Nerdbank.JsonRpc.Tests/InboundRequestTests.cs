// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Channels;
using Microsoft.VisualStudio.Threading;
using PolyType;
using ShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests;

/// <summary>Tests consumption of inbound request operations and response submission failures.</summary>
public partial class InboundRequestTests : TestBase
{
	/// <summary>Verifies that response submission preserves cancellation and aggregate-fault behavior.</summary>
	/// <param name="suspend">Whether the response submission suspends before failing.</param>
	/// <param name="cancel">Whether the failure is cancellation rather than a connection fault.</param>
	[Test]
	[Arguments(false, false)]
	[Arguments(true, false)]
	[Arguments(false, true)]
	[Arguments(true, true)]
	public async Task ResponseSubmissionFailure(bool suspend, bool cancel)
	{
		(MockChannel<JsonRpcMessage> local, MockChannel<JsonRpcMessage> remote) = MockChannel<JsonRpcMessage>.CreatePair();
		FailureWriter writer = new(local.Writer, suspend, cancel);
		MockJsonRpcPipeChannel channel = new(new MockChannel<JsonRpcMessage>(local.Reader, writer), writeDirectly: true);
		using JsonRpc rpc = new(channel);
		rpc.AddRpcTarget(new Server(), new JsonRpcTargetOptions { MethodNameTransform = CommonMethodNameTransforms.Identity });
		rpc.Start();
		try
		{
			await remote.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = nameof(Server.Number) }, this.TimeoutToken);
			await writer.Started.Task.WithCancellation(this.TimeoutToken);
			if (suspend)
			{
				Assert.False(rpc.Completion.IsCompleted);
			}

			writer.Release.TrySetResult(true);
			if (cancel)
			{
				await remote.Writer.WriteAsync(new JsonRpcRequest { Id = 2, Method = nameof(Server.Number) }, this.TimeoutToken);
				JsonRpcResult response = Assert.IsType<JsonRpcResult>(await remote.Reader.ReadAsync(this.TimeoutToken));
				Assert.Equal(new RequestId(2), response.Id);
				Assert.False(rpc.Completion.IsCompleted);
			}
			else
			{
				AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => rpc.Completion.WithCancellation(this.TimeoutToken));
				Assert.Same(writer.Failure, Assert.Single(error.InnerExceptions));
			}
		}
		finally
		{
			writer.Release.TrySetResult(true);
		}
	}

	/// <summary>Verifies independent completion and reuse after bursts of suspended server response submissions.</summary>
	[Test]
	public async Task ConcurrentResponsesCompleteInReverseOrder()
	{
		const int Count = 96;
		(MockChannel<JsonRpcMessage> local, MockChannel<JsonRpcMessage> remote) = MockChannel<JsonRpcMessage>.CreatePair();
		BurstWriter writer = new(local.Writer, Count);
		MockJsonRpcPipeChannel channel = new(new MockChannel<JsonRpcMessage>(local.Reader, writer), writeDirectly: true);
		using JsonRpc rpc = new(channel);
		rpc.AddRpcTarget(new Server(), new JsonRpcTargetOptions { MethodNameTransform = CommonMethodNameTransforms.Identity });
		rpc.Start();
		for (int round = 0; round < 3; round++)
		{
			TaskCompletionSource<bool>[] gates = writer.BeginRound();
			try
			{
				for (int i = 0; i < Count; i++)
				{
					await remote.Writer.WriteAsync(new JsonRpcRequest { Id = i, Method = nameof(Server.Number) }, this.TimeoutToken);
				}

				await writer.Started.Task.WithCancellation(this.TimeoutToken);
				for (int i = Count - 1; i >= 0; i--)
				{
					gates[i].SetResult(true);
					JsonRpcResult response = Assert.IsType<JsonRpcResult>(await remote.Reader.ReadAsync(this.TimeoutToken));
					Assert.Equal(new RequestId(i), response.Id);
					int value = ((MessagePackSerializerPlugin)channel.Serializer).Serializer.Deserialize(response.Result.AsMessagePack(), ShapeProvider.Default.Int32, this.TimeoutToken);
					Assert.Equal(42, value);
				}

				Assert.False(rpc.Completion.IsCompleted);
			}
			finally
			{
				foreach (TaskCompletionSource<bool> gate in gates)
				{
					gate.TrySetResult(true);
				}
			}
		}
	}

	/// <summary>A synchronous application target dispatched asynchronously by the connection.</summary>
	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class Server
	{
		/// <summary>Returns a fixed result.</summary>
		/// <returns>The result.</returns>
		public int Number() => 42;
	}

	private sealed class FailureWriter(ChannelWriter<JsonRpcMessage> inner, bool suspend, bool cancel) : ChannelWriter<JsonRpcMessage>
	{
		private int writes;

		/// <summary>Gets the signal that the first response submission started.</summary>
		internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <summary>Gets the first response submission's continuation gate.</summary>
		internal TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <summary>Gets the expected response submission failure.</summary>
		internal Exception Failure { get; } = cancel ? new OperationCanceledException("Response submission canceled.") : new InvalidOperationException("Response submission failed.");

		/// <inheritdoc/>
		public override bool TryComplete(Exception? error = null) => inner.TryComplete(error);

		/// <inheritdoc/>
		public override bool TryWrite(JsonRpcMessage item) => inner.TryWrite(item);

		/// <inheritdoc/>
		public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) => inner.WaitToWriteAsync(cancellationToken);

		/// <inheritdoc/>
		public override async ValueTask WriteAsync(JsonRpcMessage item, CancellationToken cancellationToken = default)
		{
			if (Interlocked.Increment(ref this.writes) == 1)
			{
				this.Started.TrySetResult(true);
				if (suspend)
				{
					await this.Release.Task.WithCancellation(cancellationToken);
				}

				throw this.Failure;
			}

			await inner.WriteAsync(item, cancellationToken);
		}
	}

	private sealed class BurstWriter(ChannelWriter<JsonRpcMessage> inner, int count) : ChannelWriter<JsonRpcMessage>
	{
		private readonly Dictionary<RequestId, TaskCompletionSource<bool>> gates = [];
		private int writes;

		/// <summary>Gets the signal that all response submissions in a round started.</summary>
		internal TaskCompletionSource<bool> Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <inheritdoc/>
		public override bool TryComplete(Exception? error = null) => inner.TryComplete(error);

		/// <inheritdoc/>
		public override bool TryWrite(JsonRpcMessage item) => inner.TryWrite(item);

		/// <inheritdoc/>
		public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) => inner.WaitToWriteAsync(cancellationToken);

		/// <inheritdoc/>
		public override async ValueTask WriteAsync(JsonRpcMessage item, CancellationToken cancellationToken = default)
		{
			JsonRpcResult response = Assert.IsType<JsonRpcResult>(item);
			TaskCompletionSource<bool> gate = this.gates[response.Id];
			if (Interlocked.Increment(ref this.writes) == count)
			{
				this.Started.TrySetResult(true);
			}

			await gate.Task.WithCancellation(cancellationToken);
			await inner.WriteAsync(item, cancellationToken);
		}

		/// <summary>Creates new response submission gates for a round.</summary>
		/// <returns>The gates indexed by request ID.</returns>
		internal TaskCompletionSource<bool>[] BeginRound()
		{
			this.writes = 0;
			this.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
			TaskCompletionSource<bool>[] roundGates = new TaskCompletionSource<bool>[count];
			this.gates.Clear();
			for (int i = 0; i < count; i++)
			{
				roundGates[i] = new(TaskCreationOptions.RunContinuationsAsynchronously);
				this.gates.Add(new RequestId(i), roundGates[i]);
			}

			return roundGates;
		}
	}
}
