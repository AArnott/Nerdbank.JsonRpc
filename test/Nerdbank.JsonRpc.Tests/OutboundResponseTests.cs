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
				if (code == JsonRpcErrorCode.RequestCancelled)
				{
					OperationCanceledException exception = typed
						? await Assert.ThrowsAsync<OperationCanceledException>(() => typedResponse.AsTask())
						: await Assert.ThrowsAsync<OperationCanceledException>(() => voidResponse.AsTask());
					Assert.Equal(cancellation.Token, exception.CancellationToken);
				}
				else
				{
					JsonRpcException exception = typed
						? await Assert.ThrowsAsync<JsonRpcException>(() => typedResponse.AsTask())
						: await Assert.ThrowsAsync<JsonRpcException>(() => voidResponse.AsTask());
					Assert.Equal(code, exception.ErrorDetails.Code);
				}
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

	/// <summary>Verifies that preserving public results once allows repeatable consumption after subsequent calls.</summary>
	[Test]
	public async Task PreservedTypedAndVoidResultsSurviveReuse()
	{
		using Fixture fixture = new(JsonRpcEncoding.MessagePack);
		ValueTask<int> typed = fixture.RequestAsync(this.TimeoutToken).Preserve();
		await fixture.RespondAsync(await fixture.ReadRequestAsync(this.TimeoutToken), 42, this.TimeoutToken);
		ValueTask untyped = fixture.Rpc.RequestAsync("ping", fixture.Arguments, this.TimeoutToken).Preserve();
		await fixture.RespondAsync(await fixture.ReadRequestAsync(this.TimeoutToken), 7, this.TimeoutToken);
		Assert.Equal(42, await typed);
		await untyped;

		for (int i = 0; i < 96; i++)
		{
			ValueTask<int> next = fixture.RequestAsync(this.TimeoutToken);
			await fixture.RespondAsync(await fixture.ReadRequestAsync(this.TimeoutToken), i, this.TimeoutToken);
			Assert.Equal(i, await next);
		}

		Assert.Equal(42, await typed);
		await untyped;
	}

	/// <summary>Verifies that ignoring a public result does not prevent encoded response cleanup or later calls.</summary>
	/// <param name="typed">Whether the ignored operation deserializes a result.</param>
	[Test]
	[Arguments(true)]
	[Arguments(false)]
	public async Task UnconsumedPublicResultReleasesEncodedResponse(bool typed)
	{
		using Fixture fixture = new(JsonRpcEncoding.MessagePack);
		ValueTask<int> typedResult = typed ? fixture.RequestAsync(this.TimeoutToken) : default;
		ValueTask voidResult = typed ? default : fixture.Rpc.RequestAsync("ping", fixture.Arguments, this.TimeoutToken);
		JsonRpcRequest request = await fixture.ReadRequestAsync(this.TimeoutToken);
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcMessagePackChannel decoder = new(local);
		await using JsonRpcMessagePackChannel encoder = new(peer);
		decoder.Start();
		encoder.Start();
		await encoder.Writer.WriteAsync(new JsonRpcResult { Id = request.Id!.Value, Result = (RawMessagePack)new byte[] { 42 } }, this.TimeoutToken);
		JsonRpcResult decoded = Assert.IsType<JsonRpcResult>(await decoder.Reader.ReadAsync(this.TimeoutToken));
		await fixture.Remote.Writer.WriteAsync(decoded, this.TimeoutToken);

		while (!(typed ? typedResult.IsCompleted : voidResult.IsCompleted))
		{
			this.TimeoutToken.ThrowIfCancellationRequested();
			await Task.Yield();
		}

		Assert.Throws<ObjectDisposedException>(() => _ = decoded.Result.Bytes);
		for (int i = 0; i < 96; i++)
		{
			ValueTask<int> next = fixture.RequestAsync(this.TimeoutToken);
			await fixture.RespondAsync(await fixture.ReadRequestAsync(this.TimeoutToken), i, this.TimeoutToken);
			Assert.Equal(i, await next);
		}
	}

	/// <summary>Verifies notification completion and recovery when outbound acceptance suspends.</summary>
	/// <param name="fail">Whether the first acceptance fails.</param>
	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task SuspendedNotificationCompletesAndAllowsReuse(bool fail)
	{
		using Fixture fixture = new(JsonRpcEncoding.MessagePack, gateFirstWrite: true);
		ValueTask first = fixture.Rpc.NotifyAsync("notify", fixture.Arguments, this.TimeoutToken);
		JsonRpcRequest notification = await fixture.ReadRequestAsync(this.TimeoutToken);
		Assert.Null(notification.Id);
		Assert.False(first.IsCompleted);
		if (fail)
		{
			fixture.WriteCompletion.SetException(new InvalidOperationException("write failed"));
			await Assert.ThrowsAsync<InvalidOperationException>(() => first.AsTask());
		}
		else
		{
			ValueTask preserved = first.Preserve();
			fixture.WriteCompletion.SetResult(true);
			await preserved;
			await preserved;
		}

		for (int i = 0; i < 96; i++)
		{
			await fixture.Rpc.NotifyAsync("notify", fixture.Arguments, this.TimeoutToken);
			Assert.Null((await fixture.ReadRequestAsync(this.TimeoutToken)).Id);
		}
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
			OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failed.AsTask());
			Assert.Equal(cancellation.Token, exception.CancellationToken);
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

	/// <summary>Verifies peer cancellation through direct, batched, and generated request APIs.</summary>
	/// <param name="encoding">The payload encoding.</param>
	/// <param name="batched">Whether to send the request in a batch.</param>
	[Test]
	[Arguments(JsonRpcEncoding.Json, false)]
	[Arguments(JsonRpcEncoding.Json, true)]
	[Arguments(JsonRpcEncoding.MessagePack, false)]
	[Arguments(JsonRpcEncoding.MessagePack, true)]
	public async Task PeerCancellationUsesCallerTokenOnlyWhenCanceled(JsonRpcEncoding encoding, bool batched)
	{
		using Fixture fixture = new(encoding);
		for (int tokenState = 0; tokenState < 3; tokenState++)
		{
			for (int api = 0; api < 4; api++)
			{
				using CancellationTokenSource cancellation = new();
				CancellationToken token = tokenState == 0 ? CancellationToken.None : cancellation.Token;
				using JsonRpcBatch batch = fixture.Rpc.CreateBatch();
				IJsonRpcClient client = batched ? batch : fixture.Rpc;
				ICalculator proxy = batched ? batch.Attach<ICalculator>() : fixture.Rpc.Attach<ICalculator>();
				Task pending = api switch
				{
					0 => client.RequestAsync("ping", fixture.Arguments, ShapeProvider.Default.Int32, token).AsTask(),
					1 => client.RequestAsync("ping", fixture.Arguments, token).AsTask(),
					2 => proxy.AddAsync(1, 2, token).AsTask(),
					_ => proxy.PingTaskAsync(token),
				};
				JsonRpcRequest request;
				if (batched)
				{
					await batch.SendAsync(this.TimeoutToken);
					JsonRpcMessageBatch payload = Assert.IsType<JsonRpcMessageBatch>(await fixture.Remote.Reader.ReadAsync(this.TimeoutToken));
					request = Assert.IsType<JsonRpcRequest>(Assert.Single(payload.Messages));
				}
				else
				{
					request = await fixture.ReadRequestAsync(this.TimeoutToken);
				}

				if (tokenState == 2)
				{
					cancellation.Cancel();
					Assert.Equal("$/cancelRequest", (await fixture.ReadRequestAsync(this.TimeoutToken)).Method);
				}

				JsonRpcError response = new()
				{
					Id = request.Id!.Value,
					Error = new() { Code = JsonRpcErrorCode.RequestCancelled, Message = "peer cancellation reason" },
				};
				await fixture.Remote.Writer.WriteAsync(response, this.TimeoutToken);
				OperationCanceledException exception = await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WithCancellation(this.TimeoutToken));
				Assert.Equal(tokenState == 2 ? token : CancellationToken.None, exception.CancellationToken);
				Assert.True(pending.IsCanceled);
				if (tokenState == 2)
				{
					Assert.Equal("peer cancellation reason", exception.Message);
				}
				else
				{
					Assert.Equal("The remote party canceled processing the request without the caller requesting cancellation.", exception.Message);
				}

				JsonRpcException remoteError = Assert.IsType<JsonRpcException>(exception.InnerException);
				Assert.Equal(JsonRpcErrorCode.RequestCancelled, remoteError.ErrorDetails.Code);
				Assert.Equal("peer cancellation reason", remoteError.Message);
			}
		}
	}

	/// <summary>Verifies local batch cancellation retains only a canceled request token.</summary>
	/// <param name="alreadyCanceled">Whether the token is canceled before adding the request.</param>
	/// <param name="cancelCaller">Whether the caller cancels the request token.</param>
	/// <param name="dispose">Whether to dispose rather than cancel the unsent batch.</param>
	[Test]
	[Arguments(true, true, false)]
	[Arguments(false, true, false)]
	[Arguments(false, false, false)]
	[Arguments(false, false, true)]
	public async Task UnsentBatchCancellationUsesOnlyCanceledCallerToken(bool alreadyCanceled, bool cancelCaller, bool dispose)
	{
		using Fixture fixture = new(JsonRpcEncoding.MessagePack);
		using CancellationTokenSource cancellation = new();
		if (alreadyCanceled)
		{
			cancellation.Cancel();
		}

		using JsonRpcBatch batch = fixture.Rpc.CreateBatch();
		Task typed = batch.RequestAsync("ping", fixture.Arguments, ShapeProvider.Default.Int32, cancellation.Token).AsTask();
		Task untyped = batch.RequestAsync("ping", fixture.Arguments, cancellation.Token).AsTask();
		if (cancelCaller)
		{
			cancellation.Cancel();
		}

		if (dispose)
		{
			batch.Dispose();
		}
		else
		{
			await batch.CancelAllAsync();
		}

		foreach (Task pending in new[] { typed, untyped })
		{
			OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WithCancellation(this.TimeoutToken));
			Assert.Equal(cancelCaller ? cancellation.Token : CancellationToken.None, exception.CancellationToken);
			Assert.True(pending.IsCanceled);
		}

		Assert.False(fixture.Remote.Reader.TryRead(out _));
	}

	/// <summary>Verifies that a canceled batch submission does not leak its token into individual requests.</summary>
	/// <param name="cancelCaller">Whether to cancel an individual request token during submission.</param>
	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task BatchSubmissionCancellationUsesEachRequestToken(bool cancelCaller)
	{
		using Fixture fixture = new(JsonRpcEncoding.MessagePack, gateFirstWrite: true);
		using CancellationTokenSource submission = new();
		using CancellationTokenSource caller = new();
		using JsonRpcBatch batch = fixture.Rpc.CreateBatch();
		Task typed = batch.RequestAsync("ping", fixture.Arguments, ShapeProvider.Default.Int32, caller.Token).AsTask();
		Task untyped = batch.RequestAsync("ping", fixture.Arguments, CancellationToken.None).AsTask();
		Task sending = batch.SendAsync(submission.Token).AsTask();
		Assert.IsType<JsonRpcMessageBatch>(await fixture.Remote.Reader.ReadAsync(this.TimeoutToken));
		if (cancelCaller)
		{
			caller.Cancel();
		}

		submission.Cancel();
		OperationCanceledException sendException = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending.WithCancellation(this.TimeoutToken));
		Assert.Equal(submission.Token, sendException.CancellationToken);
		OperationCanceledException typedException = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => typed.WithCancellation(this.TimeoutToken));
		Assert.Equal(cancelCaller ? caller.Token : CancellationToken.None, typedException.CancellationToken);
		OperationCanceledException voidException = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => untyped.WithCancellation(this.TimeoutToken));
		Assert.Equal(CancellationToken.None, voidException.CancellationToken);
	}

	/// <summary>Verifies cancellation exceptions from submission use the request token rather than an internal token.</summary>
	/// <param name="typed">Whether the request returns a value.</param>
	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task UnrequestedSubmissionCancellationOmitsCallerToken(bool typed)
	{
		using Fixture fixture = new(JsonRpcEncoding.MessagePack, gateFirstWrite: true);
		using CancellationTokenSource caller = new();
		using CancellationTokenSource transport = new();
		transport.Cancel();
		Task pending = typed ? fixture.RequestAsync(caller.Token).AsTask() : fixture.Rpc.RequestAsync("ping", fixture.Arguments, caller.Token).AsTask();
		await fixture.ReadRequestAsync(this.TimeoutToken);
		fixture.WriteCompletion.SetException(new OperationCanceledException("Transport canceled.", transport.Token));
		OperationCanceledException exception = await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WithCancellation(this.TimeoutToken));
		Assert.Equal(CancellationToken.None, exception.CancellationToken);
		Assert.Equal("Transport canceled.", exception.Message);
		Assert.False(caller.IsCancellationRequested);
		ValueTask<int> next = fixture.RequestAsync(this.TimeoutToken);
		await fixture.RespondAsync(await fixture.ReadRequestAsync(this.TimeoutToken), 23, this.TimeoutToken);
		Assert.Equal(23, await next);
	}

	/// <summary>Verifies shared connection cancellation is attributed separately to each request.</summary>
	/// <param name="batched">Whether the requests are sent as a batch.</param>
	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task ConnectionCancellationUsesEachRequestToken(bool batched)
	{
		using Fixture fixture = new(JsonRpcEncoding.MessagePack);
		using CancellationTokenSource canceledCaller = new();
		using CancellationTokenSource uncanceledCaller = new();
		using CancellationTokenSource transport = new();
		transport.Cancel();
		using JsonRpcBatch batch = fixture.Rpc.CreateBatch();
		IJsonRpcClient client = batched ? batch : fixture.Rpc;
		Task canceled = client.RequestAsync("ping", fixture.Arguments, ShapeProvider.Default.Int32, canceledCaller.Token).AsTask();
		Task uncanceled = client.RequestAsync("ping", fixture.Arguments, uncanceledCaller.Token).AsTask();
		Task noToken = client.RequestAsync("ping", fixture.Arguments, CancellationToken.None).AsTask();
		if (batched)
		{
			await batch.SendAsync(this.TimeoutToken);
			Assert.IsType<JsonRpcMessageBatch>(await fixture.Remote.Reader.ReadAsync(this.TimeoutToken));
		}
		else
		{
			for (int i = 0; i < 3; i++)
			{
				await fixture.ReadRequestAsync(this.TimeoutToken);
			}
		}

		canceledCaller.Cancel();
		Assert.Equal("$/cancelRequest", (await fixture.ReadRequestAsync(this.TimeoutToken)).Method);
		fixture.Remote.Writer.TryComplete(new OperationCanceledException("Transport canceled.", transport.Token));
		OperationCanceledException canceledException = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WithCancellation(this.TimeoutToken));
		Assert.Equal(canceledCaller.Token, canceledException.CancellationToken);
		foreach (Task pending in new[] { uncanceled, noToken })
		{
			OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WithCancellation(this.TimeoutToken));
			Assert.Equal(CancellationToken.None, exception.CancellationToken);
			Assert.Equal("Transport canceled.", exception.Message);
		}
	}

	/// <summary>Verifies an already-canceled direct request preserves the caller's token without sending.</summary>
	/// <param name="typed">Whether the request returns a value.</param>
	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task AlreadyCanceledDirectRequestPreservesCallerToken(bool typed)
	{
		using Fixture fixture = new(JsonRpcEncoding.MessagePack);
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();
		Task pending = typed ? fixture.RequestAsync(cancellation.Token).AsTask() : fixture.Rpc.RequestAsync("ping", fixture.Arguments, cancellation.Token).AsTask();
		OperationCanceledException exception = await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WithCancellation(this.TimeoutToken));
		Assert.Equal(cancellation.Token, exception.CancellationToken);
		Assert.False(fixture.Remote.Reader.TryRead(out _));
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
