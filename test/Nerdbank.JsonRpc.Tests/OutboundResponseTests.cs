// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using ShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests;

namespace Nerdbank.JsonRpc.Tests;

/// <summary>Exercises ownership and recycling of outbound response rendezvous through public request APIs.</summary>
public class OutboundResponseTests : TestBase
{
	/// <summary>Verifies success, remote errors, and cancellation acknowledgments across repeated requests.</summary>
	/// <param name="encoding">The payload encoding.</param>
	/// <param name="typed">Whether to deserialize the response or discard it.</param>
	[Test]
	[Arguments(JsonRpcEncoding.Json, true)]
	[Arguments(JsonRpcEncoding.Json, false)]
	[Arguments(JsonRpcEncoding.MessagePack, true)]
	[Arguments(JsonRpcEncoding.MessagePack, false)]
	public async Task RepeatedRequestsRecoverFromErrorsAndCancellation(JsonRpcEncoding encoding, bool typed)
	{
		using Fixture fixture = new(encoding);
		for (int i = 0; i < 96; i++)
		{
			using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
			ValueTask<int> typedResponse = typed ? fixture.RequestAsync(cancellation.Token) : default;
			ValueTask voidResponse = typed ? default : fixture.Rpc.RequestAsync("ping", fixture.Arguments, cancellation.Token);
			JsonRpcRequest request = await fixture.ReadRequestAsync(this.TimeoutToken);
			if (i % 3 == 2)
			{
				cancellation.Cancel();
				JsonRpcRequest cancel = await fixture.ReadRequestAsync(this.TimeoutToken);
				Assert.Equal("$/cancelRequest", cancel.Method);
				Assert.Null(cancel.Id);
				Assert.False(typed ? typedResponse.IsCompleted : voidResponse.IsCompleted);
			}

			if (i % 3 == 0)
			{
				await fixture.RespondAsync(request, i, this.TimeoutToken);
				if (typed)
				{
					Assert.Equal(i, await typedResponse);
				}
				else
				{
					await voidResponse;
				}
			}
			else
			{
				long code = i % 3 == 1 ? JsonRpcErrorCode.InternalError : JsonRpcErrorCode.RequestCancelled;
				JsonRpcError error = new() { Id = request.Id!.Value, Error = new() { Code = code, Message = "expected" } };
				await fixture.Remote.Writer.WriteAsync(error, this.TimeoutToken);
				JsonRpcException exception = typed
					? await Assert.ThrowsAsync<JsonRpcException>(() => typedResponse.AsTask())
					: await Assert.ThrowsAsync<JsonRpcException>(() => voidResponse.AsTask());
				Assert.Equal(code, exception.ErrorDetails.Code);
			}
		}
	}

	/// <summary>Verifies out-of-order completion and reuse after bursts larger than the retained pool.</summary>
	/// <param name="encoding">The payload encoding.</param>
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ConcurrentRequestsCompleteInReverseOrderAcrossRepeatedBursts(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		for (int round = 0; round < 3; round++)
		{
			ValueTask<int>[] results = new ValueTask<int>[96];
			JsonRpcRequest[] requests = new JsonRpcRequest[results.Length];
			for (int i = 0; i < results.Length; i++)
			{
				results[i] = fixture.RequestAsync(this.TimeoutToken);
				requests[i] = await fixture.ReadRequestAsync(this.TimeoutToken);
			}

			for (int i = requests.Length - 1; i >= 0; i--)
			{
				await fixture.RespondAsync(requests[i], i, this.TimeoutToken);
			}

			for (int i = 0; i < results.Length; i++)
			{
				Assert.Equal(i, await results[i]);
			}
		}
	}

	/// <summary>Verifies that a Task adapter keeps its result after the internal source has been reused.</summary>
	[Test]
	public async Task TaskAdapterCanBeAwaitedRepeatedlyAfterSourceReuse()
	{
		using Fixture fixture = new(JsonRpcEncoding.MessagePack);
		Task<int> original = fixture.RequestAsync(this.TimeoutToken).AsTask();
		JsonRpcRequest request = await fixture.ReadRequestAsync(this.TimeoutToken);
		await fixture.RespondAsync(request, 42, this.TimeoutToken);
		Assert.Equal(42, await original);
		for (int i = 0; i < 96; i++)
		{
			ValueTask<int> next = fixture.RequestAsync(this.TimeoutToken);
			await fixture.RespondAsync(await fixture.ReadRequestAsync(this.TimeoutToken), i, this.TimeoutToken);
			Assert.Equal(i, await next);
		}

		Assert.Equal(new[] { 42, 42 }, await Task.WhenAll(original, original));
	}

	/// <summary>Verifies that a response arriving during a suspended write is retained until that write completes.</summary>
	/// <param name="failWrite">Whether the suspended write fails instead of completing.</param>
	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task ResponseArrivingBeforeWriteEndsRemainsOwnedByItsRequest(bool failWrite)
	{
		using Fixture fixture = new(JsonRpcEncoding.MessagePack, gateFirstWrite: true);
		ValueTask<int> first = fixture.RequestAsync(this.TimeoutToken);
		JsonRpcRequest firstRequest = await fixture.ReadRequestAsync(this.TimeoutToken);

		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcMessagePackChannel decoder = new(local);
		await using JsonRpcMessagePackChannel encoder = new(peer);
		decoder.Start();
		encoder.Start();
		JsonRpcResult response = new() { Id = firstRequest.Id!.Value, Result = (RawMessagePack)new byte[] { 42 } };
		await encoder.Writer.WriteAsync(response, this.TimeoutToken);
		JsonRpcResult decoded = Assert.IsType<JsonRpcResult>(await decoder.Reader.ReadAsync(this.TimeoutToken));
		await fixture.Remote.Writer.WriteAsync(decoded, this.TimeoutToken);

		// Completing a later response proves the reader has processed the first response while its write is suspended.
		ValueTask<int> second = fixture.RequestAsync(this.TimeoutToken);
		await fixture.RespondAsync(await fixture.ReadRequestAsync(this.TimeoutToken), 7, this.TimeoutToken);
		Assert.Equal(7, await second);
		Assert.False(first.IsCompleted);

		if (failWrite)
		{
			fixture.WriteCompletion.SetException(new InvalidOperationException("write failed"));
			await Assert.ThrowsAsync<InvalidOperationException>(() => first.AsTask());
		}
		else
		{
			fixture.WriteCompletion.SetResult(true);
			Assert.Equal(42, await first);
		}

		Assert.Throws<ObjectDisposedException>(() => _ = decoded.Result.Bytes);
		ValueTask<int> third = fixture.RequestAsync(this.TimeoutToken);
		await fixture.RespondAsync(await fixture.ReadRequestAsync(this.TimeoutToken), 9, this.TimeoutToken);
		Assert.Equal(9, await third);
	}

	/// <summary>Verifies that submission failure unregisters a request before its source is reused.</summary>
	/// <param name="cancelWrite">Whether the write is canceled instead of faulted.</param>
	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task FailedWriteDoesNotPoisonTheNextRequest(bool cancelWrite)
	{
		using Fixture fixture = new(JsonRpcEncoding.MessagePack, gateFirstWrite: true);
		using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
		ValueTask<int> failed = fixture.RequestAsync(cancellation.Token);
		await fixture.ReadRequestAsync(this.TimeoutToken);
		if (cancelWrite)
		{
			cancellation.Cancel();
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failed.AsTask());
		}
		else
		{
			fixture.WriteCompletion.SetException(new InvalidOperationException("write failed"));
			await Assert.ThrowsAsync<InvalidOperationException>(() => failed.AsTask());
		}

		ValueTask<int> next = fixture.RequestAsync(this.TimeoutToken);
		await fixture.RespondAsync(await fixture.ReadRequestAsync(this.TimeoutToken), 23, this.TimeoutToken);
		Assert.Equal(23, await next);
		if (cancelWrite)
		{
			fixture.WriteCompletion.SetResult(true);
		}
	}

	/// <summary>Verifies that direct and batch completion sources coexist in the same response table.</summary>
	/// <param name="encoding">The payload encoding.</param>
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task DirectAndBatchRequestsCompleteIndependently(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		ValueTask<int> direct = fixture.RequestAsync(this.TimeoutToken);
		JsonRpcRequest directRequest = await fixture.ReadRequestAsync(this.TimeoutToken);
		using JsonRpcBatch batch = fixture.Rpc.CreateBatch();
		ValueTask<int> batched = batch.RequestAsync("ping", fixture.Arguments, ShapeProvider.Default.Int32, this.TimeoutToken);
		await batch.SendAsync(this.TimeoutToken);
		JsonRpcMessageBatch payload = Assert.IsType<JsonRpcMessageBatch>(await fixture.Remote.Reader.ReadAsync(this.TimeoutToken));
		JsonRpcRequest batchRequest = Assert.IsType<JsonRpcRequest>(Assert.Single(payload.Messages));

		await fixture.RespondAsync(batchRequest, 17, this.TimeoutToken);
		Assert.Equal(17, await batched);
		Assert.False(direct.IsCompleted);
		await fixture.RespondAsync(directRequest, 41, this.TimeoutToken);
		Assert.Equal(41, await direct);
	}

	/// <summary>Verifies that response/disposal races cannot complete another pending request with the wrong result.</summary>
	/// <param name="encoding">The payload encoding.</param>
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ResponsesRacingDisposalCompleteOnlyTheirOwnRequests(JsonRpcEncoding encoding)
	{
		for (int round = 0; round < 10; round++)
		{
			using Fixture fixture = new(encoding);
			Task<int>[] tasks = new Task<int>[16];
			JsonRpcRequest[] requests = new JsonRpcRequest[tasks.Length];
			for (int i = 0; i < tasks.Length; i++)
			{
				tasks[i] = fixture.RequestAsync(this.TimeoutToken).AsTask();
				requests[i] = await fixture.ReadRequestAsync(this.TimeoutToken);
			}

			Task responses = Task.Run(async () =>
			{
				for (int i = requests.Length - 1; i >= 0; i--)
				{
					await fixture.RespondAsync(requests[i], i, this.TimeoutToken);
				}
			});
			await Task.WhenAll(responses, Task.Run(fixture.Rpc.Dispose));
			for (int i = 0; i < tasks.Length; i++)
			{
				try
				{
					Assert.Equal(i, await tasks[i].WithCancellation(this.TimeoutToken));
				}
				catch (ObjectDisposedException)
				{
					Assert.True(fixture.Rpc.IsDisposed);
				}
			}
		}
	}

	private sealed class Fixture : IDisposable
	{
		private readonly JsonRpcEncoding encoding;

		/// <summary>Initializes a new instance of the <see cref="Fixture"/> class.</summary>
		/// <param name="encoding">The payload encoding.</param>
		/// <param name="gateFirstWrite">Whether the first write waits for explicit completion.</param>
		internal Fixture(JsonRpcEncoding encoding, bool gateFirstWrite = false)
		{
			this.encoding = encoding;
			(MockChannel<JsonRpcMessage> local, MockChannel<JsonRpcMessage> remote) = MockChannel<JsonRpcMessage>.CreatePair();
			this.Remote = remote;
			if (gateFirstWrite)
			{
				local = new(local.Reader, new GatedWriter(local.Writer, this.WriteCompletion));
			}

			JsonRpcSerializer serializer = encoding == JsonRpcEncoding.Json
				? new JsonSerializerPlugin(new Nerdbank.Json.JsonSerializer())
				: new MessagePackSerializerPlugin(JsonRpcMessagePackChannel.DefaultSerializer);
			this.Rpc = new(new MockJsonRpcPipeChannel(local, serializer, writeDirectly: true));
			this.Rpc.Start();
		}

		/// <summary>Gets the started client connection.</summary>
		internal JsonRpc Rpc { get; }

		/// <summary>Gets the peer that supplies responses.</summary>
		internal MockChannel<JsonRpcMessage> Remote { get; }

		/// <summary>Gets the completion gate for the first write.</summary>
		internal TaskCompletionSource<bool> WriteCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <summary>Gets empty arguments in the selected encoding.</summary>
		internal JsonRpcValue Arguments => this.encoding == JsonRpcEncoding.Json ? JsonRpcValue.FromJson("[]"u8.ToArray()) : EmptyParamsMsgPack;

		/// <inheritdoc/>
		public void Dispose() => this.Rpc.Dispose();

		/// <summary>Starts a typed request.</summary>
		/// <param name="cancellationToken">The request cancellation token.</param>
		/// <returns>The single-consumption response awaitable.</returns>
		internal ValueTask<int> RequestAsync(CancellationToken cancellationToken)
			=> this.Rpc.RequestAsync("ping", this.Arguments, ShapeProvider.Default.Int32, cancellationToken);

		/// <summary>Reads the next outbound request.</summary>
		/// <param name="cancellationToken">The read cancellation token.</param>
		/// <returns>The request.</returns>
		internal async Task<JsonRpcRequest> ReadRequestAsync(CancellationToken cancellationToken)
			=> Assert.IsType<JsonRpcRequest>(await this.Remote.Reader.ReadAsync(cancellationToken));

		/// <summary>Completes a request with an integer result.</summary>
		/// <param name="request">The request being completed.</param>
		/// <param name="value">The result.</param>
		/// <param name="cancellationToken">The write cancellation token.</param>
		/// <returns>The response submission awaitable.</returns>
		internal ValueTask RespondAsync(JsonRpcRequest request, int value, CancellationToken cancellationToken)
		{
			JsonRpcValue result = this.encoding == JsonRpcEncoding.Json
				? JsonRpcValue.FromJson(Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture)))
				: JsonRpcValue.FromMessagePack((RawMessagePack)new byte[] { checked((byte)value) });
			return this.Remote.Writer.WriteAsync(new JsonRpcResult { Id = request.Id!.Value, Result = result }, cancellationToken);
		}
	}

	private sealed class GatedWriter(ChannelWriter<JsonRpcMessage> inner, TaskCompletionSource<bool> completion) : ChannelWriter<JsonRpcMessage>
	{
		private int writes;

		/// <inheritdoc/>
		public override bool TryComplete(Exception? error = null) => inner.TryComplete(error);

		/// <inheritdoc/>
		public override bool TryWrite(JsonRpcMessage item) => inner.TryWrite(item);

		/// <inheritdoc/>
		public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) => inner.WaitToWriteAsync(cancellationToken);

		/// <inheritdoc/>
		public override async ValueTask WriteAsync(JsonRpcMessage item, CancellationToken cancellationToken = default)
		{
			await inner.WriteAsync(item, cancellationToken);
			if (Interlocked.Increment(ref this.writes) == 1)
			{
				await completion.Task.WithCancellation(cancellationToken);
			}
		}
	}
}
