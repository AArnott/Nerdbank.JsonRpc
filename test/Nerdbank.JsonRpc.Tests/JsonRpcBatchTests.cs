// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Channels;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using PolyType;

public partial class JsonRpcBatchTests : TestBase
{
	[Fact]
	public async Task ClientBatch_MixedRequestsAndNotifications()
	{
		(JsonRpc jsonRpc, Channel<JsonRpcMessage> channel) = CreateStartedRpcPair();
		JsonRpcBatch batch = jsonRpc.CreateBatch();
		Task<int> sumTask = batch.RequestAsync<int>("Add", NilMsgPack, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken).AsTask();
		Task voidTask = batch.RequestAsync("Ping", NilMsgPack, this.TimeoutToken).AsTask();
		await batch.NotifyAsync("Notify", NilMsgPack, this.TimeoutToken);

		await batch.SendAsync(this.TimeoutToken);

		JsonRpcMessageBatch sent = Assert.IsType<JsonRpcMessageBatch>(await channel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(3, sent.Messages.Length);
		JsonRpcRequest sumRequest = Assert.IsType<JsonRpcRequest>(sent.Messages[0]);
		JsonRpcRequest voidRequest = Assert.IsType<JsonRpcRequest>(sent.Messages[1]);
		JsonRpcRequest notification = Assert.IsType<JsonRpcRequest>(sent.Messages[2]);
		Assert.NotNull(sumRequest.Id);
		Assert.NotNull(voidRequest.Id);
		Assert.Null(notification.Id);

		JsonRpcMessageBatch responseBatch = new(
			[
				new JsonRpcError
				{
					Id = voidRequest.Id.Value,
					Error = new JsonRpcErrorDetails { Code = JsonRpcErrorCode.InternalError, Message = "failed" },
				},
				new JsonRpcResult
				{
					Id = sumRequest.Id.Value,
					Result = (RawMessagePack)jsonRpc.Serializer.Serialize<int, Witness>(5, this.TimeoutToken),
				},
			]);
		await channel.Writer.WriteAsync(responseBatch, this.TimeoutToken);

		Assert.Equal(5, await sumTask.WithCancellation(this.TimeoutToken));
		JsonRpcException ex = await Assert.ThrowsAsync<JsonRpcException>(() => voidTask.WithCancellation(this.TimeoutToken));
		Assert.Equal(JsonRpcErrorCode.InternalError, ex.ErrorDetails.Code);
	}

	[Fact]
	public async Task ClientBatch_RejectsEmptyDuplicateAndMutationAfterSend()
	{
		(JsonRpc jsonRpc, Channel<JsonRpcMessage> channel) = CreateStartedRpcPair();
		JsonRpcBatch emptyBatch = jsonRpc.CreateBatch();
		await Assert.ThrowsAsync<InvalidOperationException>(() => emptyBatch.SendAsync(this.TimeoutToken).AsTask());

		JsonRpcBatch batch = jsonRpc.CreateBatch();
		await batch.NotifyAsync("Notify", NilMsgPack, this.TimeoutToken);
		await batch.SendAsync(this.TimeoutToken);
		Assert.IsType<JsonRpcMessageBatch>(await channel.Reader.ReadAsync(this.TimeoutToken));

		await Assert.ThrowsAsync<InvalidOperationException>(() => batch.SendAsync(this.TimeoutToken).AsTask());
		await Assert.ThrowsAsync<InvalidOperationException>(() => batch.NotifyAsync("Notify", NilMsgPack, this.TimeoutToken).AsTask());
	}

	[Fact]
	public async Task ClientBatch_DisposeBeforeSendCancelsPendingRequests()
	{
		(JsonRpc jsonRpc, Channel<JsonRpcMessage> channel) = CreateStartedRpcPair();
		JsonRpcBatch batch = jsonRpc.CreateBatch();
		Task requestTask = batch.RequestAsync("Ping", NilMsgPack, this.TimeoutToken).AsTask();

		batch.Dispose();

		await Assert.ThrowsAsync<TaskCanceledException>(() => requestTask.WithCancellation(this.TimeoutToken));
		Assert.False(channel.Reader.TryRead(out _));
	}

	[Fact]
	public async Task ClientBatch_CancellationBeforeSendOmitsRequest()
	{
		(JsonRpc jsonRpc, Channel<JsonRpcMessage> channel) = CreateStartedRpcPair();
		JsonRpcBatch batch = jsonRpc.CreateBatch();
		using CancellationTokenSource cts = new();
		Task requestTask = batch.RequestAsync("Canceled", NilMsgPack, cts.Token).AsTask();
		await batch.NotifyAsync("Notify", NilMsgPack, this.TimeoutToken);

		cts.Cancel();
		await batch.SendAsync(this.TimeoutToken);

		JsonRpcMessageBatch sent = Assert.IsType<JsonRpcMessageBatch>(await channel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Single(sent.Messages);
		Assert.Equal("Notify", Assert.IsType<JsonRpcRequest>(sent.Messages[0]).Method);
		await Assert.ThrowsAsync<TaskCanceledException>(() => requestTask.WithCancellation(this.TimeoutToken));
	}

	[Fact]
	public async Task ClientBatch_SendBeforeStartThrows()
	{
		(_, Channel<JsonRpcMessage> jsonRpcChannel) = MockChannel<JsonRpcMessage>.CreatePair();
		JsonRpc jsonRpc = new(jsonRpcChannel);
		JsonRpcBatch batch = jsonRpc.CreateBatch();
		Task requestTask = batch.RequestAsync("Ping", NilMsgPack, this.TimeoutToken).AsTask();

		InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => batch.SendAsync(this.TimeoutToken).AsTask());

		Assert.Contains(JsonRpcState.NotStarted.ToString(), ex.Message, StringComparison.Ordinal);
		await Assert.ThrowsAsync<InvalidOperationException>(() => requestTask.WithCancellation(this.TimeoutToken));
	}

	[Fact]
	public async Task ClientBatch_CancelAllAfterSendUsesBatchedCancelRequests()
	{
		(JsonRpc jsonRpc, Channel<JsonRpcMessage> channel) = CreateStartedRpcPair();
		JsonRpcBatch batch = jsonRpc.CreateBatch();
		Task firstTask = batch.RequestAsync("First", NilMsgPack, this.TimeoutToken).AsTask();
		Task secondTask = batch.RequestAsync("Second", NilMsgPack, this.TimeoutToken).AsTask();
		await batch.NotifyAsync("Notify", NilMsgPack, this.TimeoutToken);
		await batch.SendAsync(this.TimeoutToken);

		JsonRpcMessageBatch sent = Assert.IsType<JsonRpcMessageBatch>(await channel.Reader.ReadAsync(this.TimeoutToken));
		JsonRpcRequest first = Assert.IsType<JsonRpcRequest>(sent.Messages[0]);
		JsonRpcRequest second = Assert.IsType<JsonRpcRequest>(sent.Messages[1]);

		await batch.CancelAllAsync();

		JsonRpcMessageBatch cancellationBatch = Assert.IsType<JsonRpcMessageBatch>(await channel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(2, cancellationBatch.Messages.Length);
		Assert.All(cancellationBatch.Messages, message => Assert.Equal("$/cancelRequest", Assert.IsType<JsonRpcRequest>(message).Method));

		JsonRpcMessageBatch cancellationResponseBatch = new(
			[
				new JsonRpcError
				{
					Id = first.Id!.Value,
					Error = new JsonRpcErrorDetails { Code = JsonRpcErrorCode.RequestCancelled, Message = "first cancelled" },
				},
				new JsonRpcError
				{
					Id = second.Id!.Value,
					Error = new JsonRpcErrorDetails { Code = JsonRpcErrorCode.RequestCancelled, Message = "second cancelled" },
				},
			]);
		await channel.Writer.WriteAsync(cancellationResponseBatch, this.TimeoutToken);

		JsonRpcException firstEx = await Assert.ThrowsAsync<JsonRpcException>(() => firstTask.WithCancellation(this.TimeoutToken));
		JsonRpcException secondEx = await Assert.ThrowsAsync<JsonRpcException>(() => secondTask.WithCancellation(this.TimeoutToken));
		Assert.Equal(JsonRpcErrorCode.RequestCancelled, firstEx.ErrorDetails.Code);
		Assert.Equal(JsonRpcErrorCode.RequestCancelled, secondEx.ErrorDetails.Code);
	}

	[Fact]
	public async Task ClientBatch_CancelAllWriteFailureFaultsPendingRequests()
	{
		JsonRpc jsonRpc = new(new FailingSecondWriteChannel());
		jsonRpc.Start();
		JsonRpcBatch batch = jsonRpc.CreateBatch();
		Task requestTask = batch.RequestAsync("LongRunning", NilMsgPack, this.TimeoutToken).AsTask();
		await batch.SendAsync(this.TimeoutToken);

		await Assert.ThrowsAsync<InvalidOperationException>(() => batch.CancelAllAsync().AsTask());
		await Assert.ThrowsAsync<InvalidOperationException>(() => requestTask.WithCancellation(this.TimeoutToken));
	}

	[Fact]
	public async Task ClientBatch_CancellationAfterSendUsesCancelRequest()
	{
		(JsonRpc jsonRpc, Channel<JsonRpcMessage> channel) = CreateStartedRpcPair();
		JsonRpcBatch batch = jsonRpc.CreateBatch();
		using CancellationTokenSource cts = new();
		Task requestTask = batch.RequestAsync("LongRunning", NilMsgPack, cts.Token).AsTask();
		await batch.SendAsync(this.TimeoutToken);

		JsonRpcMessageBatch sent = Assert.IsType<JsonRpcMessageBatch>(await channel.Reader.ReadAsync(this.TimeoutToken));
		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(Assert.Single(sent.Messages));
		cts.Cancel();

		JsonRpcRequest cancelRequest = Assert.IsType<JsonRpcRequest>(await channel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal("$/cancelRequest", cancelRequest.Method);
		Assert.Null(cancelRequest.Id);

		JsonRpcError cancellationError = new()
		{
			Id = request.Id!.Value,
			Error = new JsonRpcErrorDetails { Code = JsonRpcErrorCode.RequestCancelled, Message = "cancelled" },
		};
		await channel.Writer.WriteAsync(cancellationError, this.TimeoutToken);

		JsonRpcException ex = await Assert.ThrowsAsync<JsonRpcException>(() => requestTask.WithCancellation(this.TimeoutToken));
		Assert.Equal(JsonRpcErrorCode.RequestCancelled, ex.ErrorDetails.Code);
	}

	[Fact]
	public async Task ServerBatch_EmptyBatchReturnsInvalidRequest()
	{
		(_, Channel<JsonRpcMessage> channel) = CreateStartedServerPair();
		await channel.Writer.WriteAsync(new JsonRpcMessageBatch([]), this.TimeoutToken);

		JsonRpcError error = Assert.IsType<JsonRpcError>(await channel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(JsonRpcErrorCode.InvalidRequest, error.Error.Code);
	}

	[Fact]
	public async Task ServerBatch_NestedBatchEntryReturnsInvalidRequest()
	{
		(_, Channel<JsonRpcMessage> channel) = CreateStartedServerPair();
		JsonRpcMessageBatch requestBatch = new(
			[
				new JsonRpcMessageBatch(
					[
						new JsonRpcRequest { Id = 1, Method = nameof(MockServer.GetMagicNumber) },
					]),
			]);
		await channel.Writer.WriteAsync(requestBatch, this.TimeoutToken);

		JsonRpcError error = Assert.IsType<JsonRpcError>(await channel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(JsonRpcErrorCode.InvalidRequest, error.Error.Code);
	}

	[Fact]
	public async Task ServerBatch_MixedRequestsAndNotificationsAggregatesResponses()
	{
		(JsonRpc jsonRpc, Channel<JsonRpcMessage> channel) = CreateStartedServerPair();
		JsonRpcMessageBatch requestBatch = new(
			[
				new JsonRpcRequest { Id = 1, Method = nameof(MockServer.Add), Arguments = CreateTwoIntArguments(3, 2) },
				new JsonRpcRequest { Method = nameof(MockServer.Add), Arguments = CreateTwoIntArguments(8, 4) },
				new JsonRpcRequest { Id = 2, Method = nameof(MockServer.GetMagicNumber) },
			]);
		await channel.Writer.WriteAsync(requestBatch, this.TimeoutToken);

		JsonRpcMessageBatch responseBatch = Assert.IsType<JsonRpcMessageBatch>(await channel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(2, responseBatch.Messages.Length);
		JsonRpcResult first = Assert.IsType<JsonRpcResult>(responseBatch.Messages[0]);
		JsonRpcResult second = Assert.IsType<JsonRpcResult>(responseBatch.Messages[1]);
		Assert.Equal((RequestId)1, first.Id);
		Assert.Equal((RequestId)2, second.Id);
		Assert.Equal(5, jsonRpc.Serializer.Deserialize<int, Witness>(first.Result, this.TimeoutToken));
		Assert.Equal(42, jsonRpc.Serializer.Deserialize<int, Witness>(second.Result, this.TimeoutToken));
	}

	[Fact]
	public async Task ServerBatch_NotificationOnlySendsNoResponse()
	{
		(_, Channel<JsonRpcMessage> channel) = CreateStartedServerPair();
		JsonRpcMessageBatch notificationBatch = new(
			[
				new JsonRpcRequest { Method = nameof(MockServer.Add), Arguments = CreateTwoIntArguments(3, 2) },
			]);
		await channel.Writer.WriteAsync(notificationBatch, this.TimeoutToken);

		Task<JsonRpcMessage> responseTask = channel.Reader.ReadAsync(this.TimeoutToken).AsTask();
		await Task.Delay(ExpectedTimeout, this.TimeoutToken);
		Assert.False(responseTask.IsCompleted);
	}

	[Fact]
	public async Task ClientBatch_ResponseBatchFansOutToPendingRequests()
	{
		(JsonRpc jsonRpc, Channel<JsonRpcMessage> channel) = CreateStartedRpcPair();
		JsonRpcBatch batch = jsonRpc.CreateBatch();
		Task<int> firstTask = batch.RequestAsync<int>("first", NilMsgPack, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken).AsTask();
		Task<int> secondTask = batch.RequestAsync<int>("second", NilMsgPack, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken).AsTask();
		await batch.SendAsync(this.TimeoutToken);

		JsonRpcMessageBatch requestBatch = Assert.IsType<JsonRpcMessageBatch>(await channel.Reader.ReadAsync(this.TimeoutToken));
		JsonRpcRequest firstRequest = Assert.IsType<JsonRpcRequest>(requestBatch.Messages[0]);
		JsonRpcRequest secondRequest = Assert.IsType<JsonRpcRequest>(requestBatch.Messages[1]);

		JsonRpcMessageBatch responseBatch = new(
			[
				new JsonRpcResult { Id = secondRequest.Id!.Value, Result = (RawMessagePack)jsonRpc.Serializer.Serialize<int, Witness>(2, this.TimeoutToken) },
				new JsonRpcResult { Id = firstRequest.Id!.Value, Result = (RawMessagePack)jsonRpc.Serializer.Serialize<int, Witness>(1, this.TimeoutToken) },
			]);
		await channel.Writer.WriteAsync(responseBatch, this.TimeoutToken);

		Assert.Equal(1, await firstTask.WithCancellation(this.TimeoutToken));
		Assert.Equal(2, await secondTask.WithCancellation(this.TimeoutToken));
	}

	private static (JsonRpc Rpc, Channel<JsonRpcMessage> Channel) CreateStartedRpcPair()
	{
		(Channel<JsonRpcMessage> channel, Channel<JsonRpcMessage> jsonRpcChannel) = MockChannel<JsonRpcMessage>.CreatePair();
		JsonRpc jsonRpc = new(jsonRpcChannel);
		jsonRpc.Start();
		return (jsonRpc, channel);
	}

	private static (JsonRpc Rpc, Channel<JsonRpcMessage> Channel) CreateStartedServerPair()
	{
		(JsonRpc jsonRpc, Channel<JsonRpcMessage> channel) = CreateStartedRpcPair();
		jsonRpc.AddRpcTarget(new MockServer());
		return (jsonRpc, channel);
	}

	private static RawMessagePack CreateTwoIntArguments(int a, int b)
	{
		Sequence<byte> seq = new();
		MessagePackWriter msgpackWriter = new(seq);
		msgpackWriter.WriteArrayHeader(2);
		msgpackWriter.Write(a);
		msgpackWriter.Write(b);
		msgpackWriter.Flush();
		return (RawMessagePack)seq.AsReadOnlySequence;
	}

	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class MockServer
	{
		public int GetMagicNumber() => 42;

		public int Add(int a, int b) => a + b;
	}

	[GenerateShapeFor<int>]
	private partial class Witness;

	private sealed class FailingSecondWriteChannel : Channel<JsonRpcMessage>
	{
		internal FailingSecondWriteChannel()
		{
			Channel<JsonRpcMessage> inbound = Channel.CreateUnbounded<JsonRpcMessage>();
			this.Reader = inbound.Reader;
			this.Writer = new FailingSecondWriteChannelWriter();
		}
	}

	private sealed class FailingSecondWriteChannelWriter : ChannelWriter<JsonRpcMessage>
	{
		private int writeCount;

		public override bool TryComplete(Exception? error = null) => true;

		public override bool TryWrite(JsonRpcMessage item) => Interlocked.Increment(ref this.writeCount) == 1;

		public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) => new(true);

		public override ValueTask WriteAsync(JsonRpcMessage item, CancellationToken cancellationToken = default)
		{
			return Interlocked.Increment(ref this.writeCount) == 1
				? default
				: new ValueTask(Task.FromException(new InvalidOperationException("The outbound channel is closed.")));
		}
	}
}
