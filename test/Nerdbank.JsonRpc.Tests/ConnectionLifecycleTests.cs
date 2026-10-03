// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using PolyType;

namespace Nerdbank.JsonRpc.Tests;

/// <summary>Exercises caller-visible shutdown semantics and the final protocol diagnostic.</summary>
public partial class ConnectionLifecycleTests : TestBase
{
	/// <summary>Verifies successful idle EOF, shutdown inspection, and the subsequent deliberate-disposal override.</summary>
	/// <param name="mode">The encoding and framing mode.</param>
	[Test]
	[Arguments(0)]
	[Arguments(1)]
	[Arguments(2)]
	[Arguments(3)]
	public async Task IdleEofSucceedsAndPreservesCauseAfterDispose(int mode)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel channel = CreateChannel(local, mode);
		using JsonRpc rpc = new(channel);
		rpc.Start();
		await peer.Output.CompleteAsync();
		await rpc.Completion.WithCancellation(this.TimeoutToken);
		Assert.True(rpc.IsDisposed);
		Assert.Equal(JsonRpcState.Disconnected, rpc.State);
		EndOfStreamException cause = Assert.IsType<EndOfStreamException>(rpc.TerminationException);
		await Assert.ThrowsAsync<EndOfStreamException>(async () => await rpc.RequestAsync("late", default, this.TimeoutToken));
		await Assert.ThrowsAsync<EndOfStreamException>(async () => await rpc.NotifyAsync("late", default, this.TimeoutToken));
		rpc.Dispose();
		await Assert.ThrowsAsync<ObjectDisposedException>(async () => await rpc.RequestAsync("late", default, this.TimeoutToken));
		await Assert.ThrowsAsync<ObjectDisposedException>(async () => await rpc.NotifyAsync("late", default, this.TimeoutToken));
		Assert.Same(cause, rpc.TerminationException);
		Assert.Equal(JsonRpcState.Disconnected, rpc.State);
		Assert.Equal(TaskStatus.RanToCompletion, rpc.Completion.Status);
		await peer.Input.CompleteAsync();
	}

	/// <summary>Verifies deliberate disposal before and after startup, including pending calls.</summary>
	/// <param name="start">Whether to start the connection and register a request.</param>
	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task LocalDisposalSucceedsAndFaultsPendingRequests(bool start)
	{
		(MockChannel<JsonRpcMessage> local, MockChannel<JsonRpcMessage> peer) = MockChannel<JsonRpcMessage>.CreatePair();
		await using MockJsonRpcPipeChannel channel = new(local);
		JsonRpc rpc = new(channel);
		Task? pending = null;
		if (start)
		{
			rpc.Start();
			pending = rpc.RequestAsync("pending", default, this.TimeoutToken).AsTask();
			await peer.Reader.ReadAsync(this.TimeoutToken);
		}

		rpc.Dispose();
		await rpc.Completion.WithCancellation(this.TimeoutToken);
		if (pending is not null)
		{
			await Assert.ThrowsAsync<ObjectDisposedException>(() => pending.WithCancellation(this.TimeoutToken));
		}

		Assert.Null(rpc.TerminationException);
		Assert.Equal(JsonRpcState.Disposed, rpc.State);
		Assert.True(rpc.IsDisposed);
		await Assert.ThrowsAsync<ObjectDisposedException>(async () => await rpc.RequestAsync("late", default, this.TimeoutToken));
		await Assert.ThrowsAsync<ObjectDisposedException>(async () => await rpc.NotifyAsync("late", default, this.TimeoutToken));
		rpc.Dispose();
		Assert.Equal(TaskStatus.RanToCompletion, rpc.Completion.Status);
	}

	/// <summary>Verifies EOF cause consistency for ordinary and batched requests across framing modes.</summary>
	/// <param name="mode">The encoding and framing mode.</param>
	[Test]
	[Arguments(0)]
	[Arguments(1)]
	[Arguments(2)]
	[Arguments(3)]
	public async Task PendingDirectAndBatchCallsShareEofCause(int mode)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel channel = CreateChannel(local, mode);
		await using JsonRpcPipeChannel peerChannel = CreateChannel(peer, mode);
		using JsonRpc rpc = new(channel);
		rpc.Start();
		peerChannel.Start();
		Task first = rpc.RequestAsync("direct", default, this.TimeoutToken).AsTask();
		await peerChannel.Reader.ReadAsync(this.TimeoutToken);
		using JsonRpcBatch batch = rpc.CreateBatch();
		Task second = batch.RequestAsync("batch", default, this.TimeoutToken).AsTask();
		await batch.SendAsync(this.TimeoutToken);
		await peerChannel.Reader.ReadAsync(this.TimeoutToken);
		await peer.Output.CompleteAsync();
		EndOfStreamException firstError = await Assert.ThrowsAsync<EndOfStreamException>(() => first.WithCancellation(this.TimeoutToken));
		EndOfStreamException secondError = await Assert.ThrowsAsync<EndOfStreamException>(() => second.WithCancellation(this.TimeoutToken));
		EndOfStreamException completionError = await Assert.ThrowsAsync<EndOfStreamException>(() => rpc.Completion.WithCancellation(this.TimeoutToken));
		Assert.Same(firstError, secondError);
		Assert.Same(firstError, completionError);
		Assert.Same(firstError, rpc.TerminationException);
		Assert.Contains("2 pending outbound requests", completionError.Message);
		Assert.Equal(JsonRpcState.Disconnected, rpc.State);
		rpc.Dispose();
		Assert.Same(completionError, rpc.TerminationException);
		Assert.True(rpc.Completion.IsFaulted);
	}

	/// <summary>Verifies an actual final wire notification and protocol-failure propagation before disconnect.</summary>
	/// <param name="mode">The encoding and framing mode.</param>
	[Test]
	[Arguments(0)]
	[Arguments(1)]
	[Arguments(2)]
	[Arguments(3)]
	public async Task ProtocolRejectionFlushesDiagnosticBeforeDisconnect(int mode)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel channel = CreateChannel(local, mode);
		await using JsonRpcPipeChannel peerChannel = CreateChannel(peer, mode);
		using JsonRpc rpc = new(channel);
		rpc.Start();
		peerChannel.Start();
		Task pending = rpc.RequestAsync("pending", default, this.TimeoutToken).AsTask();
		await peerChannel.Reader.ReadAsync(this.TimeoutToken);
		await peer.Output.WriteAsync(MalformedFrame(mode), this.TimeoutToken);
		JsonRpcRequest notification = Assert.IsType<JsonRpcRequest>(await peerChannel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal("$/protocolViolation", notification.Method);
		Assert.Null(notification.Id);
		Assert.True(notification.Arguments.HasValue);
		ProtocolViolationException error = await Assert.ThrowsAsync<ProtocolViolationException>(() => pending.WithCancellation(this.TimeoutToken));
		Assert.Same(error, await Assert.ThrowsAsync<ProtocolViolationException>(() => rpc.Completion.WithCancellation(this.TimeoutToken)));
		Assert.Same(error, rpc.TerminationException);
		Assert.True(rpc.IsDisposed);
		Assert.Equal(JsonRpcState.Faulted, rpc.State);
		await Assert.ThrowsAsync<ProtocolViolationException>(async () => await rpc.NotifyAsync("late", default, this.TimeoutToken));
		Assert.False(await peerChannel.Reader.WaitToReadAsync(this.TimeoutToken));
		rpc.Dispose();
		await Assert.ThrowsAsync<ObjectDisposedException>(async () => await rpc.NotifyAsync("late", default, this.TimeoutToken));
		Assert.Same(error, rpc.TerminationException);
	}

	/// <summary>Verifies internal peer-reported logging without reciprocal protocol shutdown.</summary>
	/// <param name="mode">The encoding and framing mode.</param>
	[Test]
	[Arguments(0)]
	[Arguments(1)]
	[Arguments(2)]
	[Arguments(3)]
	public async Task PeerLogsProtocolDiagnosticWithoutEcho(int mode)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel channel = CreateChannel(local, mode);
		await using JsonRpcPipeChannel peerChannel = CreateChannel(peer, mode);
		RecordingLogger logger = new();
		using JsonRpc rpc = new(channel);
		using JsonRpc peerRpc = new(peerChannel) { Logger = logger };
		rpc.Start();
		peerRpc.Start();
		await peer.Output.WriteAsync(MalformedFrame(mode), this.TimeoutToken);
		await Assert.ThrowsAsync<ProtocolViolationException>(() => rpc.Completion.WithCancellation(this.TimeoutToken));
		await peerRpc.Completion.WithCancellation(this.TimeoutToken);
		Assert.Contains(logger.Messages, m => m.Contains("Peer-reported JSON-RPC protocol violation"));
		Assert.Equal(LogLevel.Critical, await logger.ProtocolDiagnostic.Task.WithCancellation(this.TimeoutToken));
		Assert.Equal(JsonRpcState.Disconnected, peerRpc.State);
		Assert.DoesNotContain(logger.Messages, m => m.Contains("Could not flush the final"));
	}

	/// <summary>Verifies canonical peer-reported violations are critical diagnostics, not a request to terminate locally.</summary>
	/// <param name="mode">The encoding and framing mode.</param>
	/// <param name="batch">Whether the notification arrives within a batch.</param>
	[Test]
	[Arguments(0, false)]
	[Arguments(0, true)]
	[Arguments(2, true)]
	[Arguments(2, false)]
	public Task PeerReportedViolationIsCriticalAndConnectionRemainsUsable(int mode, bool batch)
		=> this.VerifyPeerReportedViolationIsCriticalAndConnectionRemainsUsable(mode, "$/protocolViolation", batch);

	/// <summary>Verifies original read and write exception instances and inner causes.</summary>
	/// <param name="write">Whether the transport fails while writing rather than reading.</param>
	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task TransportFailurePreservesOriginalCause(bool write)
	{
		IOException cause = new("transport failure", new InvalidOperationException("inner cause"));
		await using FailureChannel channel = new(cause, write);
		using JsonRpc rpc = new(channel);
		rpc.Start();
		Task pending = rpc.RequestAsync("pending", default, this.TimeoutToken).AsTask();
		await channel.RequestSent.Task.WithCancellation(this.TimeoutToken);
		channel.Fail.TrySetResult(true);
		Assert.Same(cause, await Assert.ThrowsAsync<IOException>(() => pending.WithCancellation(this.TimeoutToken)));
		Assert.Same(cause, await Assert.ThrowsAsync<IOException>(() => rpc.Completion.WithCancellation(this.TimeoutToken)));
		Assert.Same(cause, rpc.TerminationException);
		Assert.NotNull(cause.InnerException);
		await Assert.ThrowsAsync<IOException>(async () => await rpc.RequestAsync("late", default, this.TimeoutToken));
	}

	/// <summary>Verifies blocked final or already-started writes cannot prevent bounded protocol teardown.</summary>
	/// <param name="pendingRequest">Whether an ordinary request is already blocked on output.</param>
	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task BlockedDiagnosticIsBoundedAndDoesNotReplaceProtocolCause(bool pendingRequest)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		BlockingWriter writer = new(local.Output);
		await using JsonRpcJsonChannel channel = new(new DuplexPipe(local.Input, writer), new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited);
		RecordingLogger logger = new();
		using JsonRpc rpc = new(channel) { Logger = logger };
		rpc.Start();
		Task? pending = pendingRequest ? rpc.RequestAsync("pending", default, this.TimeoutToken).AsTask() : null;
		Stopwatch stopwatch = Stopwatch.StartNew();
		await peer.Output.WriteAsync(MalformedFrame(0), this.TimeoutToken);
		await writer.Started.Task.WithCancellation(this.TimeoutToken);
		ProtocolViolationException error = await Assert.ThrowsAsync<ProtocolViolationException>(() => rpc.Completion.WithCancellation(this.TimeoutToken));
		Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(800), TimeSpan.FromSeconds(3));
		Assert.Same(error, rpc.TerminationException);
		if (pending is not null)
		{
			Assert.Same(error, await Assert.ThrowsAsync<ProtocolViolationException>(() => pending.WithCancellation(this.TimeoutToken)));
		}

		Assert.Contains(logger.Messages, m => m.Contains("Could not flush the final protocol-violation notification"));
		await peer.Input.CompleteAsync();
		await peer.Output.CompleteAsync();
	}

	/// <summary>Verifies truncation is never classified as successful idle EOF.</summary>
	/// <param name="mode">The encoding and framing mode.</param>
	[Test]
	[Arguments(0)]
	[Arguments(1)]
	[Arguments(2)]
	[Arguments(3)]
	public async Task TruncatedFrameFaultsEvenWithoutPendingRequests(int mode)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel channel = CreateChannel(local, mode);
		using JsonRpc rpc = new(channel);
		rpc.Start();
		byte[] truncated = mode switch
		{
			0 => "{"u8.ToArray(),
			1 => "Content-Length: 2\r\n\r\n{"u8.ToArray(),
			2 => [0, 0, 0, 2, 0x81],
			3 => [0x81],
			_ => throw new ArgumentOutOfRangeException(nameof(mode)),
		};
		await peer.Output.WriteAsync(truncated, this.TimeoutToken);
		await peer.Output.CompleteAsync();
		Exception cause = mode < 2
			? await Assert.ThrowsAsync<ProtocolViolationException>(() => rpc.Completion.WithCancellation(this.TimeoutToken))
			: await Assert.ThrowsAsync<EndOfStreamException>(() => rpc.Completion.WithCancellation(this.TimeoutToken));
		Assert.Same(cause, rpc.TerminationException);
		Assert.Equal(JsonRpcState.Faulted, rpc.State);
		await peer.Input.CompleteAsync();
	}

	/// <summary>Verifies the full parser rejection message is transmitted while the parser exception is retained locally.</summary>
	[Test]
	public async Task JsonParserCauseIsRetainedAndMessageIsTransmitted()
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel channel = CreateChannel(local, 0);
		await using JsonRpcPipeChannel peerChannel = CreateChannel(peer, 0);
		using JsonRpc rpc = new(channel);
		rpc.Start();
		peerChannel.Start();
		await peer.Output.WriteAsync("{secret-credential\n"u8.ToArray(), this.TimeoutToken);
		JsonRpcRequest diagnostic = Assert.IsType<JsonRpcRequest>(await peerChannel.Reader.ReadAsync(this.TimeoutToken));
		string wireReason = Encoding.UTF8.GetString(diagnostic.Arguments.Bytes.ToArray());
		using JsonDocument document = JsonDocument.Parse(wireReason);
		string reason = document.RootElement.GetProperty("reason").GetString()!;
		ProtocolViolationException error = await Assert.ThrowsAsync<ProtocolViolationException>(() => rpc.Completion.WithCancellation(this.TimeoutToken));
		JsonException parser = Assert.IsAssignableFrom<JsonException>(error.Data["ParserException"]);
		Assert.Equal(error.Message, reason);
		Assert.Contains(parser.Message, reason);
	}

	/// <summary>Verifies dynamic rejection details are transmitted and logged without truncation or substitution.</summary>
	[Test]
	public async Task FullProtocolViolationDetailsReachPeerLogs()
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel channel = CreateChannel(local, 0);
		await using JsonRpcPipeChannel peerChannel = CreateChannel(peer, 0);
		RecordingLogger logger = new();
		using JsonRpc rpc = new(channel);
		using JsonRpc peerRpc = new(peerChannel) { Logger = logger };
		rpc.Start();
		peerRpc.Start();
		string rejectedId = new('x', 1024);
		await peerChannel.Writer.WriteAsync(new JsonRpcResult { Id = new RequestId(rejectedId), Result = JsonRpcValue.FromJson("null"u8.ToArray()) }, this.TimeoutToken);
		ProtocolViolationException error = await Assert.ThrowsAsync<ProtocolViolationException>(() => rpc.Completion.WithCancellation(this.TimeoutToken));
		await peerRpc.Completion.WithCancellation(this.TimeoutToken);
		Assert.Contains(rejectedId, error.Message);
		Assert.Contains(logger.Messages, message => message == $"Peer-reported JSON-RPC protocol violation: {error.Message}.");
		Assert.Equal(LogLevel.Critical, await logger.ProtocolDiagnostic.Task.WithCancellation(this.TimeoutToken));
	}

	/// <summary>Verifies explicit disposal overrides later sends without overwriting a protocol shutdown in progress.</summary>
	[Test]
	public async Task DisposeDuringProtocolDiagnosticDoesNotEraseOriginalCause()
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		BlockingWriter writer = new(local.Output);
		await using JsonRpcJsonChannel channel = new(new DuplexPipe(local.Input, writer), new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited);
		using JsonRpc rpc = new(channel);
		rpc.Start();
		await peer.Output.WriteAsync(MalformedFrame(0), this.TimeoutToken);
		await writer.Started.Task.WithCancellation(this.TimeoutToken);
		Exception original = Assert.IsType<ProtocolViolationException>(rpc.TerminationException);
		rpc.Dispose();
		await Assert.ThrowsAsync<ObjectDisposedException>(async () => await rpc.NotifyAsync("late", default, this.TimeoutToken));
		Assert.Same(original, await Assert.ThrowsAsync<ProtocolViolationException>(() => rpc.Completion.WithCancellation(this.TimeoutToken)));
		Assert.Equal(JsonRpcState.Faulted, rpc.State);
		await peer.Input.CompleteAsync();
		await peer.Output.CompleteAsync();
	}

	/// <summary>Verifies automatic teardown releases event subscriptions, owned objects, and generators.</summary>
	/// <param name="mode">The encoding and framing mode.</param>
	[Test]
	[Arguments(0)]
	[Arguments(2)]
	public async Task AutomaticShutdownReleasesTargetsGeneratorsAndOwnedObjects(int mode)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel channel = CreateChannel(local, mode);
		await using JsonRpcPipeChannel peerChannel = CreateChannel(peer, mode);
		using JsonRpc rpc = new(channel);
		using JsonRpc peerRpc = new(peerChannel);
		EventNotificationTests.EventfulTarget eventTarget = new();
		AsyncEnumerableService sequences = new();
		RemoteCounterService counters = new();
		rpc.AddRpcTarget(eventTarget, new JsonRpcTargetOptions());
		rpc.AddRpcTarget<IAsyncEnumerableService>(sequences);
		rpc.AddRpcTarget<IRemoteCounterService>(counters);
		rpc.Start();
		peerRpc.Start();
		IRemoteCounter counter = await peerRpc.Attach<IRemoteCounterService>().GetCounterAsync(this.TimeoutToken);
		IAsyncEnumerable<int> sequence = peerRpc.Attach<IAsyncEnumerableService>().GetNumbersWithSettingsAsync(1000, 1, 0, 3, this.TimeoutToken);
		await using IAsyncEnumerator<int> enumerator = sequence.GetAsyncEnumerator(this.TimeoutToken);
		Assert.True(await enumerator.MoveNextAsync());
		await peer.Output.CompleteAsync();
		await rpc.Completion.WithCancellation(this.TimeoutToken);
		await peerRpc.Completion.WithCancellation(this.TimeoutToken);
		Assert.True(counters.Counter.IsDisposed);
		Assert.True(await sequences.IsGeneratorDisposedAsync(this.TimeoutToken));
		eventTarget.RaiseValueChanged(4);
		await Assert.ThrowsAsync<EndOfStreamException>(() => counter.IncrementAsync(this.TimeoutToken));
		await Assert.ThrowsAsync<EndOfStreamException>(async () => await enumerator.MoveNextAsync());
	}

	/// <summary>Verifies cancellation callbacks do not attempt a send after shutdown.</summary>
	[Test]
	public async Task CancellationAfterShutdownDoesNotThrowFromTheCallback()
	{
		(MockChannel<JsonRpcMessage> local, MockChannel<JsonRpcMessage> peer) = MockChannel<JsonRpcMessage>.CreatePair();
		await using MockJsonRpcPipeChannel channel = new(local);
		using JsonRpc rpc = new(channel);
		rpc.Start();
		using CancellationTokenSource cancellation = new();
		Task pending = rpc.RequestAsync("pending", default, cancellation.Token).AsTask();
		await peer.Reader.ReadAsync(this.TimeoutToken);
		peer.Writer.TryComplete();
		await Assert.ThrowsAsync<EndOfStreamException>(() => rpc.Completion.WithCancellation(this.TimeoutToken));
		cancellation.Cancel();
		await Assert.ThrowsAsync<EndOfStreamException>(() => pending.WithCancellation(this.TimeoutToken));
	}

	/// <summary>Verifies secondary cleanup failures are logged without changing the EOF outcome.</summary>
	[Test]
	public async Task CleanupFailureDoesNotSkipOtherSubscriptionsOrReplaceEof()
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel channel = CreateChannel(local, 0);
		RecordingLogger logger = new();
		using JsonRpc rpc = new(channel) { Logger = logger };
		FailingSubscriptionTarget target = new();
		rpc.AddRpcTarget(target);
		rpc.Start();
		await peer.Output.CompleteAsync();
		await rpc.Completion.WithCancellation(this.TimeoutToken);
		Assert.True(target.BrokenRemoved);
		Assert.True(target.HealthyRemoved);
		Assert.IsType<EndOfStreamException>(rpc.TerminationException);
		Assert.Contains(logger.Messages, m => m.Contains("JSON-RPC cleanup failed"));
		await peer.Input.CompleteAsync();
	}

	/// <summary>Verifies accepted responses affect the pending count before EOF wins shutdown.</summary>
	/// <param name="respond">Whether a matching response precedes EOF.</param>
	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task ResponseAndEofUseThePendingCountAtTheTransition(bool respond)
	{
		(MockChannel<JsonRpcMessage> local, MockChannel<JsonRpcMessage> peer) = MockChannel<JsonRpcMessage>.CreatePair();
		await using MockJsonRpcPipeChannel channel = new(local);
		using JsonRpc rpc = new(channel);
		rpc.Start();
		Task pending = rpc.RequestAsync("pending", default, this.TimeoutToken).AsTask();
		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await peer.Reader.ReadAsync(this.TimeoutToken));
		if (respond)
		{
			await peer.Writer.WriteAsync(new JsonRpcResult { Id = request.Id!.Value, Result = NilMsgPack }, this.TimeoutToken);
		}

		peer.Writer.TryComplete();
		if (respond)
		{
			await pending.WithCancellation(this.TimeoutToken);
			await rpc.Completion.WithCancellation(this.TimeoutToken);
		}
		else
		{
			await Assert.ThrowsAsync<EndOfStreamException>(() => pending.WithCancellation(this.TimeoutToken));
			await Assert.ThrowsAsync<EndOfStreamException>(() => rpc.Completion.WithCancellation(this.TimeoutToken));
		}

		Assert.Equal(JsonRpcState.Disconnected, rpc.State);
	}

	/// <summary>Verifies no later batch entry is dispatched after an earlier entry rejects the protocol.</summary>
	[Test]
	public async Task ProtocolRejectionStopsRemainingBatchDispatch()
	{
		(MockChannel<JsonRpcMessage> local, MockChannel<JsonRpcMessage> peer) = MockChannel<JsonRpcMessage>.CreatePair();
		await using MockJsonRpcPipeChannel channel = new(local);
		using JsonRpc rpc = new(channel);
		FailingSubscriptionTarget target = new();
		rpc.AddRpcTarget(target);
		rpc.Start();
		JsonRpcMessageBatch batch = new([
			new JsonRpcResult { Id = 999, Result = NilMsgPack },
			new JsonRpcRequest { Method = "ping" },
		]);
		await peer.Writer.WriteAsync(batch, this.TimeoutToken);
		await Assert.ThrowsAsync<ProtocolViolationException>(() => rpc.Completion.WithCancellation(this.TimeoutToken));
		Assert.Equal(0, target.InvocationCount);
	}

	/// <summary>Verifies server-originated reverse calls use the same pending-EOF policy as ordinary calls.</summary>
	/// <param name="mode">The encoding and framing mode.</param>
	[Test]
	[Arguments(0)]
	[Arguments(2)]
	public async Task PendingReverseCallReceivesEof(int mode)
	{
		(IDuplexPipe serverPipe, IDuplexPipe clientPipe) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel serverChannel = CreateChannel(serverPipe, mode);
		await using JsonRpcPipeChannel clientChannel = CreateChannel(clientPipe, mode);
		using JsonRpc server = new(serverChannel);
		using JsonRpc client = new(clientChannel);
		RemoteCounterService service = new();
		RemoteCounter callback = new() { IncrementStarted = new(TaskCreationOptions.RunContinuationsAsynchronously), ContinueIncrement = new(TaskCreationOptions.RunContinuationsAsynchronously) };
		server.AddRpcTarget<IRemoteCounterService>(service);
		server.Start();
		client.Start();
		Assert.False(await client.Attach<IRemoteCounterService>().IsSameCounterAsync(callback, this.TimeoutToken));
		Task<int> reverse = service.LastExplicitProxy!.IncrementAsync(this.TimeoutToken);
		try
		{
			await callback.IncrementStarted.Task.WithCancellation(this.TimeoutToken);
			await clientPipe.Output.CompleteAsync();
			EndOfStreamException error = await Assert.ThrowsAsync<EndOfStreamException>(() => reverse.WithCancellation(this.TimeoutToken));
			Assert.Same(error, await Assert.ThrowsAsync<EndOfStreamException>(() => server.Completion.WithCancellation(this.TimeoutToken)));
			Assert.Contains("1 pending outbound requests", error.Message);
		}
		finally
		{
			callback.ContinueIncrement.TrySetResult(true);
		}
	}

	/// <summary>Verifies the EOF policy through real named-pipe stream adapters.</summary>
	/// <param name="mode">The encoding and framing mode.</param>
	[Test]
	[Arguments(0)]
	[Arguments(2)]
	public async Task NamedPipeEofPreservesPendingRequestCause(int mode)
	{
		string name = "jsonrpc-lifecycle-" + Guid.NewGuid().ToString("N");
		using NamedPipeServerStream serverStream = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
		using NamedPipeClientStream clientStream = new(".", name, PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
		Task accept = serverStream.WaitForConnectionAsync(this.TimeoutToken);
		await clientStream.ConnectAsync(5000, this.TimeoutToken);
		await accept;
		await using JsonRpcPipeChannel channel = CreateChannel(serverStream.UsePipe(), mode);
		await using JsonRpcPipeChannel peerChannel = CreateChannel(clientStream.UsePipe(), mode);
		using JsonRpc rpc = new(channel);
		rpc.Start();
		peerChannel.Start();
		Task pending = rpc.RequestAsync("pending", default, this.TimeoutToken).AsTask();
		await peerChannel.Reader.ReadAsync(this.TimeoutToken);
		clientStream.Dispose();
		EndOfStreamException error = await Assert.ThrowsAsync<EndOfStreamException>(() => pending.WithCancellation(this.TimeoutToken));
		Assert.Same(error, await Assert.ThrowsAsync<EndOfStreamException>(() => rpc.Completion.WithCancellation(this.TimeoutToken)));
	}

	/// <summary>Verifies non-exceptional flush cancellation/completion is still a connection failure.</summary>
	/// <param name="completed">Whether the peer completed output instead of canceling the flush.</param>
	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task FlushResultsAreNotMistakenForSuccessfulDelivery(bool completed)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel channel = CreateChannel(new DuplexPipe(local.Input, new ResultWriter(local.Output, completed)), 0);
		using JsonRpc rpc = new(channel);
		rpc.Start();
		Task pending = rpc.RequestAsync("pending", default, this.TimeoutToken).AsTask();
		Exception error = completed
			? await Assert.ThrowsAsync<EndOfStreamException>(() => pending.WithCancellation(this.TimeoutToken))
			: await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WithCancellation(this.TimeoutToken));
		Assert.Same(error, await Assert.ThrowsAnyAsync<Exception>(() => rpc.Completion.WithCancellation(this.TimeoutToken)));
		Assert.Equal(JsonRpcState.Faulted, rpc.State);
		await peer.Output.CompleteAsync();
		await peer.Input.CompleteAsync();
	}

	private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, int mode) => mode switch
	{
		0 => new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited),
		1 => new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.ContentLength),
		2 => new JsonRpcMessagePackChannel(pipe),
		3 => new JsonRpcMessagePackChannel(pipe, JsonRpcMessagePackChannel.DefaultSerializer, JsonRpcMessagePackFraming.SelfDelimiting),
		_ => throw new ArgumentOutOfRangeException(nameof(mode)),
	};

	private static byte[] MalformedFrame(int mode) => mode switch
	{
		0 => "{}\n"u8.ToArray(),
		1 => "Content-Length: 2\r\n\r\n{}"u8.ToArray(),
		2 => [0, 0, 0, 1, 0x80],
		3 => [0x80],
		_ => throw new ArgumentOutOfRangeException(nameof(mode)),
	};

	private async Task VerifyPeerReportedViolationIsCriticalAndConnectionRemainsUsable(int mode, string method, bool batch)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel channel = CreateChannel(local, mode);
		await using JsonRpcPipeChannel peerChannel = CreateChannel(peer, mode);
		RecordingLogger logger = new();
		using JsonRpc rpc = new(channel) { Logger = logger };
		rpc.Start();
		peerChannel.Start();
		JsonRpcRequest notification = new()
		{
			Method = method,
			Arguments = mode == 0
				? JsonRpcValue.FromJson("{\"reason\":\"recoverable\"}"u8.ToArray())
				: JsonRpcValue.FromMessagePack((RawMessagePack)new byte[] { 0x91, 0xab, 0x72, 0x65, 0x63, 0x6f, 0x76, 0x65, 0x72, 0x61, 0x62, 0x6c, 0x65 }),
		};
		await peerChannel.Writer.WriteAsync(batch ? new JsonRpcMessageBatch([notification]) : notification, this.TimeoutToken);
		Assert.Equal(LogLevel.Critical, await logger.ProtocolDiagnostic.Task.WithCancellation(this.TimeoutToken));
		Assert.False(rpc.Completion.IsCompleted);
		Assert.False(rpc.IsDisposed);
		Assert.Null(rpc.TerminationException);
		Task call = rpc.RequestAsync("stillConnected", default, this.TimeoutToken).AsTask();
		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await peerChannel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal("stillConnected", request.Method);
		JsonRpcResult response = new()
		{
			Id = request.Id!.Value,
			Result = mode == 0 ? JsonRpcValue.FromJson("null"u8.ToArray()) : NilMsgPack,
		};
		await peerChannel.Writer.WriteAsync(response, this.TimeoutToken);
		await call.WithCancellation(this.TimeoutToken);
		Assert.Equal(JsonRpcState.Running, rpc.State);
	}

	/// <summary>Exposes subscriptions with independently observable cleanup, including one failure.</summary>
	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class FailingSubscriptionTarget
	{
		/// <summary>An event whose remove accessor fails after removing the subscription.</summary>
		public event EventHandler Broken
		{
			add
			{
			}

			remove
			{
				this.BrokenRemoved = true;
				throw new InvalidOperationException("secondary cleanup failure");
			}
		}

		/// <summary>An event whose subscription must be removed despite another cleanup failure.</summary>
		public event EventHandler Healthy
		{
			add { }
			remove { this.HealthyRemoved = true; }
		}

		/// <summary>Gets a value indicating whether the failing event was unsubscribed.</summary>
		public bool BrokenRemoved { get; private set; }

		/// <summary>Gets a value indicating whether the other event was unsubscribed.</summary>
		public bool HealthyRemoved { get; private set; }

		/// <summary>Gets the number of dispatched pings.</summary>
		public int InvocationCount { get; private set; }

		/// <summary>Records an observable notification dispatch.</summary>
		public void Ping() => this.InvocationCount++;
	}

	private sealed class RecordingLogger : ILogger
	{
		/// <summary>Gets the observable rendered log entries.</summary>
		internal ConcurrentQueue<string> Messages { get; } = new();

		/// <summary>Gets the severity recorded for a received protocol diagnostic.</summary>
		internal TaskCompletionSource<LogLevel> ProtocolDiagnostic { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			string message = formatter(state, exception);
			this.Messages.Enqueue(message);
			if (message.StartsWith("Peer-reported JSON-RPC protocol violation", StringComparison.Ordinal))
			{
				this.ProtocolDiagnostic.TrySetResult(logLevel);
			}
		}
	}

	private sealed class FailureChannel : JsonRpcPipeChannel
	{
		private readonly Exception cause;
		private readonly bool write;

		/// <summary>Initializes a new instance of the <see cref="FailureChannel"/> class.</summary>
		/// <param name="cause">The primary failure.</param>
		/// <param name="write">Whether output rather than input fails.</param>
		internal FailureChannel(Exception cause, bool write)
			: base(FullDuplexStream.CreatePipePair().Item1, CreateInboundChannel(null), CreateOutboundChannel(null))
		{
			this.cause = cause;
			this.write = write;
		}

		public override JsonRpcEncoding Encoding => JsonRpcEncoding.MessagePack;

		public override JsonRpcSerializer Serializer { get; } = new MessagePackSerializerPlugin(JsonRpcMessagePackChannel.DefaultSerializer);

		/// <summary>Gets the signal that an ordinary request reached the output processor.</summary>
		internal TaskCompletionSource<bool> RequestSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		/// <summary>Gets the gate that triggers the transport failure.</summary>
		internal TaskCompletionSource<bool> Fail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		protected override async IAsyncEnumerable<JsonRpcMessage> ReceiveMessagesAsync(PipeReader reader, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
		{
			await this.Fail.Task.WithCancellation(cancellationToken);
			if (!this.write)
			{
				throw this.cause;
			}

			await Task.Delay(Timeout.Infinite, cancellationToken);
			yield break;
		}

		protected override async ValueTask SendMessageAsync(PipeWriter writer, JsonRpcMessage message, CancellationToken cancellationToken)
		{
			this.RequestSent.TrySetResult(true);
			if (this.write)
			{
				await this.Fail.Task.WithCancellation(cancellationToken);
				throw this.cause;
			}
		}
	}

	private sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
	{
		public PipeReader Input => input;

		public PipeWriter Output => output;
	}

	private sealed class ResultWriter(PipeWriter inner, bool completed) : PipeWriter
	{
		public override void Advance(int bytes) => inner.Advance(bytes);

		public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

		public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);

		public override void CancelPendingFlush() => inner.CancelPendingFlush();

		public override void Complete(Exception? exception = null) => inner.Complete(exception);

		public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => new(new FlushResult(isCanceled: !completed, isCompleted: completed));
	}

	private sealed class BlockingWriter(PipeWriter inner) : PipeWriter
	{
		/// <summary>Gets the signal that output is blocked in a flush.</summary>
		internal TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public override void Advance(int bytes) => inner.Advance(bytes);

		public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

		public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);

		public override void CancelPendingFlush() => inner.CancelPendingFlush();

		public override void Complete(Exception? exception = null) => inner.Complete(exception);

		public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
		{
			this.Started.TrySetResult(true);
			await Task.Delay(Timeout.Infinite, cancellationToken);
			throw new InvalidOperationException("Unreachable");
		}
	}
}
