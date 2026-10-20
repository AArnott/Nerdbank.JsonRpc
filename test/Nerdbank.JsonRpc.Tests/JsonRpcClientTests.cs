// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using PolyType;

/// <summary>
/// Tests the client-side functions of <see cref="JsonRpc"/> by mocking the server
/// in order to directly analyze the requests made by the client and test its handling
/// of particular server responses.
/// </summary>
public partial class JsonRpcClientTests : TestBase
{
	private readonly JsonRpc jsonRpc;
	private readonly Channel<JsonRpcMessage> channel;

	public JsonRpcClientTests()
	{
		(this.channel, Channel<JsonRpcMessage> jsonRpcChannel) = MockChannel<JsonRpcMessage>.CreatePair();
		this.jsonRpc = new(new MockJsonRpcPipeChannel(jsonRpcChannel));

		this.jsonRpc.Start();
	}

	[Test]
	public async Task LoggerReceivesPreBufferedTransportMessage()
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcJsonChannel channel = new(local, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited);
		await peer.Output.WriteAsync("{\"jsonrpc\":\"2.0\",\"method\":\"test\"}\n"u8.ToArray(), this.TimeoutToken);

		RecordingLogger logger = new();
		using JsonRpc rpc = new(channel) { Logger = logger };
		rpc.Start();

		Assert.Equal("JSON-RPC JsonRpcRequest", await logger.MessageReceived.Task.WithCancellation(this.TimeoutToken));
	}

	[Test]
	public void LoggerConfiguresConnectionAndTransport()
	{
		Assert.Same(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, this.jsonRpc.Logger);

		Microsoft.Extensions.Logging.ILogger logger = LoggerFactory.CreateLogger("rpc");
		using JsonRpc configured = new(new MockJsonRpcPipeChannel(Channel.CreateUnbounded<JsonRpcMessage>())) { Logger = logger };
		Assert.Same(logger, configured.Logger);
		Assert.Throws<ArgumentNullException>(() => new JsonRpc(new MockJsonRpcPipeChannel(Channel.CreateUnbounded<JsonRpcMessage>())) { Logger = null! });
	}

	[Test]
	public void OutboundRequestTimeoutMustBePositiveOrInfinite()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => { _ = new JsonRpcOptions { OutboundRequestTimeout = TimeSpan.Zero }; });
		Assert.Throws<ArgumentOutOfRangeException>(() => { _ = new JsonRpcOptions { OutboundRequestTimeout = TimeSpan.FromSeconds(-1) }; });
		Assert.Throws<ArgumentOutOfRangeException>(() => { _ = new JsonRpcOptions { OutboundRequestTimeout = TimeSpan.FromTicks(-1) }; });
		Assert.Throws<ArgumentOutOfRangeException>(() => { _ = new JsonRpcOptions { OutboundRequestTimeout = TimeSpan.FromMilliseconds(-2) }; });
	}

	[Test]
	public void OutboundRequestTimeoutMustNotExceedTimerLimit()
	{
		TimeSpan maximumTimeout = TimeSpan.FromMilliseconds(int.MaxValue);
		Assert.Equal(maximumTimeout, new JsonRpcOptions { OutboundRequestTimeout = maximumTimeout }.OutboundRequestTimeout);
		Assert.Throws<ArgumentOutOfRangeException>(() => { _ = new JsonRpcOptions { OutboundRequestTimeout = maximumTimeout + TimeSpan.FromTicks(1) }; });
		Assert.Throws<ArgumentOutOfRangeException>(() => { _ = new JsonRpcOptions { OutboundRequestTimeout = TimeSpan.MaxValue }; });
	}

	[Test]
	public void OutboundRequestTimeoutDefaultsToInfinite()
	{
		Assert.Equal(Timeout.InfiniteTimeSpan, JsonRpcOptions.Default.OutboundRequestTimeout);
		Assert.Equal(Timeout.InfiniteTimeSpan, new JsonRpcOptions().OutboundRequestTimeout);
		JsonRpcOptions options = new() { OutboundRequestTimeout = ExpectedTimeout };
		Assert.Equal(ExpectedTimeout, options.OutboundRequestTimeout);
		Assert.Equal(Timeout.InfiniteTimeSpan, (options with { OutboundRequestTimeout = Timeout.InfiniteTimeSpan }).OutboundRequestTimeout);
	}

	[Test]
	public async Task InvalidEnvelopeFaultsEveryPendingDirectAndBatchRequest()
	{
		Task first = this.jsonRpc.RequestAsync("First", EmptyParamsMsgPack, this.TimeoutToken).AsTask();
		await this.channel.Reader.ReadAsync(this.TimeoutToken);
		JsonRpcBatch batch = this.jsonRpc.CreateBatch();
		Task second = batch.RequestAsync("Second", EmptyParamsMsgPack, this.TimeoutToken).AsTask();
		await batch.SendAsync(this.TimeoutToken);
		await this.channel.Reader.ReadAsync(this.TimeoutToken);

		await this.channel.Writer.WriteAsync(new JsonRpcMessageBatch([]), this.TimeoutToken);
		await Assert.ThrowsAsync<System.Net.ProtocolViolationException>(() => first.WithCancellation(this.TimeoutToken));
		await Assert.ThrowsAsync<System.Net.ProtocolViolationException>(() => second.WithCancellation(this.TimeoutToken));
		await Assert.ThrowsAsync<System.Net.ProtocolViolationException>(() => this.jsonRpc.Completion.WithCancellation(this.TimeoutToken));
	}

	[Test]
	public async Task UnreadableResultFaultsOnlyTheMatchingRequest()
	{
		Task<int> first = this.jsonRpc.RequestAsync<AddNamedArguments, int, Witness>("First", new AddNamedArguments { A = 1, B = 2 }, this.TimeoutToken).AsTask();
		JsonRpcRequest firstRequest = Assert.IsType<JsonRpcRequest>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		Task<int> second = this.jsonRpc.RequestAsync<AddNamedArguments, int, Witness>("Second", new AddNamedArguments { A = 3, B = 4 }, this.TimeoutToken).AsTask();
		JsonRpcRequest secondRequest = Assert.IsType<JsonRpcRequest>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		await this.channel.Writer.WriteAsync(new JsonRpcResult { Id = firstRequest.Id!.Value, Result = (RawMessagePack)new byte[] { 0xa1, 0x78 } }, this.TimeoutToken);
		await Assert.ThrowsAnyAsync<Exception>(() => first.WithCancellation(this.TimeoutToken));
		Assert.False(this.jsonRpc.Completion.IsCompleted);
		await this.channel.Writer.WriteAsync(new JsonRpcResult { Id = secondRequest.Id!.Value, Result = (RawMessagePack)((MessagePackSerializerPlugin)((IJsonRpcClient)this.jsonRpc).Serializer).Serializer.Serialize<int, Witness>(7, this.TimeoutToken) }, this.TimeoutToken);
		Assert.Equal(7, await second.WithCancellation(this.TimeoutToken));
	}

	[Test]
	public async Task RequestWithoutStartingFirst()
	{
		(_, Channel<JsonRpcMessage> jsonRpcChannel) = MockChannel<JsonRpcMessage>.CreatePair();
		JsonRpc jsonRpc = new(new MockJsonRpcPipeChannel(jsonRpcChannel));
		InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
			async () => await jsonRpc.RequestAsync<AddNamedArguments, int, Witness>("Add", new AddNamedArguments { A = 2, B = 3 }, this.TimeoutToken));
		this.Logger?.Invoke(ex.Message);
	}

	[Test]
	public async Task RequestWithNamedArguments()
	{
		Task<int> resultTask = this.jsonRpc.RequestAsync<AddNamedArguments, int, Witness>("Add", new AddNamedArguments { A = 2, B = 3 }, this.TimeoutToken).AsTask();
		JsonRpcRequest requestMessage = Assert.IsAssignableFrom<JsonRpcRequest>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		this.Log(requestMessage, this.jsonRpc);
		Assert.NotNull(requestMessage.Id);
		Assert.Equal("Add", requestMessage.Method);

		JsonRpcResult resultMessage = new()
		{
			Id = requestMessage.Id.Value,
			Result = (RawMessagePack)((MessagePackSerializerPlugin)((IJsonRpcClient)this.jsonRpc).Serializer).Serializer.Serialize<int, Witness>(5, TestContext.Current!.Execution.CancellationToken),
		};
		await this.channel.Writer.WriteAsync(resultMessage, this.TimeoutToken);

		int result = await resultTask.WithCancellation(this.TimeoutToken);
		Assert.Equal(5, result);
	}

	[Test]
	public async Task RequestWithPositionalArguments()
	{
		Task<int> resultTask = this.jsonRpc.RequestAsync<AddPositionalArguments, int, Witness>("Add", new AddPositionalArguments { A = 2, B = 3 }, this.TimeoutToken).AsTask();
		JsonRpcRequest requestMessage = Assert.IsAssignableFrom<JsonRpcRequest>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		this.Log(requestMessage, this.jsonRpc);
		Assert.NotNull(requestMessage.Id);
		Assert.Equal("Add", requestMessage.Method);

		JsonRpcResult resultMessage = new()
		{
			Id = requestMessage.Id.Value,
			Result = (RawMessagePack)((MessagePackSerializerPlugin)((IJsonRpcClient)this.jsonRpc).Serializer).Serializer.Serialize<int, Witness>(5, TestContext.Current!.Execution.CancellationToken),
		};
		await this.channel.Writer.WriteAsync(resultMessage, this.TimeoutToken);

		int result = await resultTask.WithCancellation(this.TimeoutToken);
		Assert.Equal(5, result);
	}

	[Test]
	public async Task RequestWithNoReturnValue()
	{
		Task resultTask = this.jsonRpc.RequestAsync("Add", new AddNamedArguments { A = 2, B = 3 }, this.TimeoutToken).AsTask();
		JsonRpcRequest requestMessage = Assert.IsAssignableFrom<JsonRpcRequest>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		this.Log(requestMessage, this.jsonRpc);
		Assert.NotNull(requestMessage.Id);
		Assert.Equal("Add", requestMessage.Method);

		JsonRpcResult resultMessage = new()
		{
			Id = requestMessage.Id.Value,
			Result = NilMsgPack,
		};
		await this.channel.Writer.WriteAsync(resultMessage, this.TimeoutToken);

		await resultTask.WithCancellation(this.TimeoutToken);
	}

	[Test]
	public async Task CancelPendingTypedRequestAndReuse()
	{
		using CancellationTokenSource cts = new();
		Task<int> resultTask = this.jsonRpc.RequestAsync<AddNamedArguments, int, Witness>("Add", new AddNamedArguments { A = 2, B = 3 }, cts.Token).AsTask();
		JsonRpcRequest requestMessage = Assert.IsType<JsonRpcRequest>(await this.channel.Reader.ReadAsync(this.TimeoutToken));

		cts.Cancel();
		JsonRpcRequest cancelMessage = Assert.IsType<JsonRpcRequest>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal("$/cancelRequest", cancelMessage.Method);

		JsonRpcError errorMessage = new()
		{
			Id = requestMessage.Id!.Value,
			Error = new JsonRpcErrorDetails { Code = JsonRpcErrorCode.RequestCancelled, Message = "cancelled" },
		};
		await this.channel.Writer.WriteAsync(errorMessage, this.TimeoutToken);
		OperationCanceledException exception = await Assert.ThrowsAsync<OperationCanceledException>(() => resultTask.WithCancellation(this.TimeoutToken));
		Assert.Equal(cts.Token, exception.CancellationToken);

		Task<int> nextResultTask = this.jsonRpc.RequestAsync<AddNamedArguments, int, Witness>("Add", new AddNamedArguments { A = 2, B = 3 }, this.TimeoutToken).AsTask();
		JsonRpcRequest nextRequestMessage = Assert.IsType<JsonRpcRequest>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		JsonRpcResult resultMessage = new()
		{
			Id = nextRequestMessage.Id!.Value,
			Result = (RawMessagePack)((MessagePackSerializerPlugin)((IJsonRpcClient)this.jsonRpc).Serializer).Serializer.Serialize<int, Witness>(5, this.TimeoutToken),
		};
		await this.channel.Writer.WriteAsync(resultMessage, this.TimeoutToken);

		Assert.Equal(5, await nextResultTask.WithCancellation(this.TimeoutToken));
	}

	[Test]
	public async Task CancelPendingRequest()
	{
		using CancellationTokenSource cts = new();
		Task resultTask = this.jsonRpc.RequestAsync("Add", new AddNamedArguments { A = 2, B = 3 }, cts.Token).AsTask();
		JsonRpcRequest requestMessage = Assert.IsAssignableFrom<JsonRpcRequest>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		this.Log(requestMessage, this.jsonRpc);
		Assert.NotNull(requestMessage.Id);
		Assert.Equal("Add", requestMessage.Method);

		// Cancel the request and verify that a cancellation notification is transmitted.
		cts.Cancel();
		JsonRpcRequest cancelMessage = Assert.IsAssignableFrom<JsonRpcRequest>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		this.Log(cancelMessage, this.jsonRpc);
		Assert.Equal("$/cancelRequest", cancelMessage.Method);
		Assert.Null(cancelMessage.Id);
		int[]? args = ((MessagePackSerializerPlugin)((IJsonRpcClient)this.jsonRpc).Serializer).Serializer.Deserialize<int[], Witness>(cancelMessage.Arguments.AsMessagePack(), this.TimeoutToken);
		Assert.Equal(requestMessage.Id, args?.Single());

		// Verify that the original client request only completes after a response is received.
		await Assert.ThrowsAsync<TimeoutException>(() => resultTask.WithTimeout(ExpectedTimeout));

		// Send the server message acknowledging the cancellation.
		JsonRpcError errorMessage = new()
		{
			Id = requestMessage.Id.Value,
			Error = new JsonRpcErrorDetails
			{
				Code = JsonRpcErrorCode.RequestCancelled,
				Message = "Request was cancelled.",
			},
		};
		await this.channel.Writer.WriteAsync(errorMessage, this.TimeoutToken);

		// Verify that the client finally resolves.
		OperationCanceledException ex = await Assert.ThrowsAsync<OperationCanceledException>(() => resultTask.WithCancellation(this.TimeoutToken));
		Assert.Equal(cts.Token, ex.CancellationToken);
	}

	[Test]
	public async Task OutboundRequestTimeoutCancelsAndDrainsLateResponse()
	{
		(Channel<JsonRpcMessage> channel, Channel<JsonRpcMessage> jsonRpcChannel) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc jsonRpc = new(new MockJsonRpcPipeChannel(jsonRpcChannel), new JsonRpcOptions { OutboundRequestTimeout = ExpectedTimeout });
		jsonRpc.Start();

		Task requestTask = jsonRpc.RequestAsync("Add", EmptyParamsMsgPack, this.TimeoutToken).AsTask();
		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await channel.Reader.ReadAsync(this.TimeoutToken));

		TimeoutException exception = await Assert.ThrowsAsync<TimeoutException>(() => requestTask.WithCancellation(this.TimeoutToken));
		Assert.Contains(nameof(JsonRpcOptions.OutboundRequestTimeout), exception.Message);
		JsonRpcRequest cancel = Assert.IsType<JsonRpcRequest>(await channel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal("$/cancelRequest", cancel.Method);

		await channel.Writer.WriteAsync(new JsonRpcResult { Id = request.Id!.Value, Result = NilMsgPack }, this.TimeoutToken);
		Task nextRequestTask = jsonRpc.RequestAsync("Next", EmptyParamsMsgPack, this.TimeoutToken).AsTask();
		JsonRpcRequest nextRequest = Assert.IsType<JsonRpcRequest>(await channel.Reader.ReadAsync(this.TimeoutToken));
		await channel.Writer.WriteAsync(new JsonRpcResult { Id = nextRequest.Id!.Value, Result = NilMsgPack }, this.TimeoutToken);
		await nextRequestTask.WithCancellation(this.TimeoutToken);
		Assert.False(jsonRpc.Completion.IsCompleted);
	}

	[Test]
	[Arguments(false, false)]
	[Arguments(false, true)]
	[Arguments(true, false)]
	[Arguments(true, true)]
	public async Task OutboundRequestTimeoutPreservesCallerCancellation(bool batched, bool acknowledgeCancellation)
	{
		(Channel<JsonRpcMessage> channel, Channel<JsonRpcMessage> jsonRpcChannel) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc jsonRpc = new(new MockJsonRpcPipeChannel(jsonRpcChannel), new JsonRpcOptions { OutboundRequestTimeout = TimeSpan.FromSeconds(30) });
		jsonRpc.Start();
		using CancellationTokenSource cancellationSource = new();
		using JsonRpcBatch batch = jsonRpc.CreateBatch();
		IJsonRpcClient client = batched ? batch : jsonRpc;
		Task requestTask = client.RequestAsync("Pending", EmptyParamsMsgPack, cancellationSource.Token).AsTask();
		if (batched)
		{
			await batch.SendAsync(this.TimeoutToken);
		}

		JsonRpcMessage message = await channel.Reader.ReadAsync(this.TimeoutToken);
		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(batched ? Assert.IsType<JsonRpcMessageBatch>(message).Messages.Single() : message);
		cancellationSource.Cancel();
		JsonRpcRequest cancel = Assert.IsType<JsonRpcRequest>(await channel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal("$/cancelRequest", cancel.Method);
		await Assert.ThrowsAsync<TimeoutException>(() => requestTask.WithTimeout(ExpectedTimeout));

		JsonRpcResponse response = acknowledgeCancellation
			? new JsonRpcError { Id = request.Id!.Value, Error = new JsonRpcErrorDetails { Code = JsonRpcErrorCode.RequestCancelled, Message = "Canceled." } }
			: new JsonRpcResult { Id = request.Id!.Value, Result = NilMsgPack };
		await channel.Writer.WriteAsync(response, this.TimeoutToken);
		if (acknowledgeCancellation)
		{
			OperationCanceledException exception = await Assert.ThrowsAsync<OperationCanceledException>(() => requestTask.WithCancellation(this.TimeoutToken));
			Assert.Equal(cancellationSource.Token, exception.CancellationToken);
		}
		else
		{
			await requestTask.WithCancellation(this.TimeoutToken);
		}
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task OutboundRequestTimeoutInfiniteDisablesTimeout(bool batched)
	{
		(Channel<JsonRpcMessage> channel, Channel<JsonRpcMessage> jsonRpcChannel) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc jsonRpc = new(new MockJsonRpcPipeChannel(jsonRpcChannel), new JsonRpcOptions { OutboundRequestTimeout = Timeout.InfiniteTimeSpan });
		jsonRpc.Start();
		using JsonRpcBatch batch = jsonRpc.CreateBatch();
		IJsonRpcClient client = batched ? batch : jsonRpc;
		Task requestTask = client.RequestAsync("Pending", EmptyParamsMsgPack, this.TimeoutToken).AsTask();
		if (batched)
		{
			await batch.SendAsync(this.TimeoutToken);
		}

		JsonRpcMessage message = await channel.Reader.ReadAsync(this.TimeoutToken);
		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(batched ? Assert.IsType<JsonRpcMessageBatch>(message).Messages.Single() : message);
		await Assert.ThrowsAsync<TimeoutException>(() => requestTask.WithTimeout(ExpectedTimeout));
		Assert.False(channel.Reader.TryRead(out _));

		await channel.Writer.WriteAsync(new JsonRpcResult { Id = request.Id!.Value, Result = NilMsgPack }, this.TimeoutToken);
		await requestTask.WithCancellation(this.TimeoutToken);
		Assert.False(jsonRpc.Completion.IsCompleted);
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task OutboundRequestTimeoutAcceptsMaximumTimerDelay(bool batched)
	{
		(Channel<JsonRpcMessage> channel, Channel<JsonRpcMessage> jsonRpcChannel) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc jsonRpc = new(new MockJsonRpcPipeChannel(jsonRpcChannel), new JsonRpcOptions { OutboundRequestTimeout = TimeSpan.FromMilliseconds(int.MaxValue) });
		jsonRpc.Start();
		using JsonRpcBatch batch = jsonRpc.CreateBatch();
		IJsonRpcClient client = batched ? batch : jsonRpc;
		Task requestTask = client.RequestAsync("Pending", EmptyParamsMsgPack, this.TimeoutToken).AsTask();
		if (batched)
		{
			await batch.SendAsync(this.TimeoutToken);
		}

		JsonRpcMessage message = await channel.Reader.ReadAsync(this.TimeoutToken);
		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(batched ? Assert.IsType<JsonRpcMessageBatch>(message).Messages.Single() : message);
		await channel.Writer.WriteAsync(new JsonRpcResult { Id = request.Id!.Value, Result = NilMsgPack }, this.TimeoutToken);
		await requestTask.WithCancellation(this.TimeoutToken);
		Assert.False(jsonRpc.Completion.IsCompleted);
	}

	[Test]
	public async Task Notify()
	{
		await this.jsonRpc.NotifyAsync("Add", new AddNamedArguments { A = 2, B = 3 }, this.TimeoutToken);
		JsonRpcRequest requestMessage = Assert.IsAssignableFrom<JsonRpcRequest>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Null(requestMessage.Id);
		Assert.Equal("Add", requestMessage.Method);
		this.Log(requestMessage, this.jsonRpc);
	}

	[Test]
	public async Task NotifyWithRawArgumentsHonorsCancellation()
	{
		using CancellationTokenSource cts = new();
		cts.Cancel();

		await Assert.ThrowsAsync<OperationCanceledException>(() => this.jsonRpc.NotifyAsync("Add", EmptyParamsMsgPack, cts.Token).AsTask());
		Assert.False(this.channel.Reader.TryRead(out _));
	}

	[After(Test)]
	public void DisposeJsonRpc()
	{
		this.jsonRpc.Dispose();
	}

	[GenerateShape]
	internal partial struct AddNamedArguments
	{
		public required int A { get; init; }

		public required int B { get; init; }
	}

	[GenerateShape]
	internal partial struct AddPositionalArguments
	{
		[Key(0)]
		public required int A { get; init; }

		[Key(1)]
		public required int B { get; init; }
	}

	[GenerateShapeFor<int>]
	[GenerateShapeFor<int[]>]
	private partial class Witness;

	private sealed class RecordingLogger : ILogger
	{
		internal TaskCompletionSource<string> MessageReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (eventId.Id == 2)
			{
				this.MessageReceived.TrySetResult(formatter(state, exception));
			}
		}
	}
}
