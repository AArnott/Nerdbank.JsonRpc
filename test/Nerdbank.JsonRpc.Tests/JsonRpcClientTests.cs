// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Channels;
using Microsoft.VisualStudio.Threading;
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
		this.jsonRpc = new(jsonRpcChannel);

		this.jsonRpc.Start();
	}

	[Fact]
	public async Task InvalidEnvelopeFaultsEveryPendingDirectAndBatchRequest()
	{
		Task first = this.jsonRpc.RequestAsync("First", NilMsgPack, this.TimeoutToken).AsTask();
		await this.channel.Reader.ReadAsync(this.TimeoutToken);
		JsonRpcBatch batch = this.jsonRpc.CreateBatch();
		Task second = batch.RequestAsync("Second", NilMsgPack, this.TimeoutToken).AsTask();
		await batch.SendAsync(this.TimeoutToken);
		await this.channel.Reader.ReadAsync(this.TimeoutToken);

		await this.channel.Writer.WriteAsync(new JsonRpcMessageBatch([]), this.TimeoutToken);
		await Assert.ThrowsAsync<System.Net.ProtocolViolationException>(() => first.WithCancellation(this.TimeoutToken));
		await Assert.ThrowsAsync<System.Net.ProtocolViolationException>(() => second.WithCancellation(this.TimeoutToken));
		await Assert.ThrowsAsync<System.Net.ProtocolViolationException>(() => this.jsonRpc.Completion.WithCancellation(this.TimeoutToken));
	}

	[Fact]
	public async Task UnreadableResultFaultsOnlyTheMatchingRequest()
	{
		Task<int> first = this.jsonRpc.RequestAsync<AddNamedArguments, int, Witness>("First", new AddNamedArguments { A = 1, B = 2 }, this.TimeoutToken).AsTask();
		JsonRpcRequest firstRequest = Assert.IsType<JsonRpcRequest>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		Task<int> second = this.jsonRpc.RequestAsync<AddNamedArguments, int, Witness>("Second", new AddNamedArguments { A = 3, B = 4 }, this.TimeoutToken).AsTask();
		JsonRpcRequest secondRequest = Assert.IsType<JsonRpcRequest>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		await this.channel.Writer.WriteAsync(new JsonRpcResult { Id = firstRequest.Id!.Value, Result = (RawMessagePack)new byte[] { 0xa1, 0x78 } }, this.TimeoutToken);
		await Assert.ThrowsAnyAsync<Exception>(() => first.WithCancellation(this.TimeoutToken));
		Assert.False(this.jsonRpc.Completion.IsCompleted);
		await this.channel.Writer.WriteAsync(new JsonRpcResult { Id = secondRequest.Id!.Value, Result = (RawMessagePack)this.jsonRpc.Serializer.Serialize<int, Witness>(7, this.TimeoutToken) }, this.TimeoutToken);
		Assert.Equal(7, await second.WithCancellation(this.TimeoutToken));
	}

	[Fact]
	public async Task RequestWithoutStartingFirst()
	{
		(_, Channel<JsonRpcMessage> jsonRpcChannel) = MockChannel<JsonRpcMessage>.CreatePair();
		JsonRpc jsonRpc = new(jsonRpcChannel);
		InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
			async () => await jsonRpc.RequestAsync<AddNamedArguments, int, Witness>("Add", new AddNamedArguments { A = 2, B = 3 }, this.TimeoutToken));
		this.Logger?.WriteLine(ex.Message);
	}

	[Fact]
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
			Result = (RawMessagePack)this.jsonRpc.Serializer.Serialize<int, Witness>(5, TestContext.Current.CancellationToken),
		};
		await this.channel.Writer.WriteAsync(resultMessage, this.TimeoutToken);

		int result = await resultTask.WithCancellation(this.TimeoutToken);
		Assert.Equal(5, result);
	}

	[Fact]
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
			Result = (RawMessagePack)this.jsonRpc.Serializer.Serialize<int, Witness>(5, TestContext.Current.CancellationToken),
		};
		await this.channel.Writer.WriteAsync(resultMessage, this.TimeoutToken);

		int result = await resultTask.WithCancellation(this.TimeoutToken);
		Assert.Equal(5, result);
	}

	[Fact]
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

	[Fact]
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
		int[]? args = this.jsonRpc.Serializer.Deserialize<int[], Witness>(cancelMessage.Arguments, this.TimeoutToken);
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
		JsonRpcException ex = await Assert.ThrowsAsync<JsonRpcException>(() => resultTask.WithCancellation(this.TimeoutToken));
		Assert.Equal(JsonRpcErrorCode.RequestCancelled, ex.ErrorDetails.Code);
	}

	[Fact]
	public async Task Notify()
	{
		await this.jsonRpc.NotifyAsync("Add", new AddNamedArguments { A = 2, B = 3 }, this.TimeoutToken);
		JsonRpcRequest requestMessage = Assert.IsAssignableFrom<JsonRpcRequest>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Null(requestMessage.Id);
		Assert.Equal("Add", requestMessage.Method);
		this.Log(requestMessage, this.jsonRpc);
	}

	[Fact]
	public async Task NotifyWithRawArgumentsHonorsCancellation()
	{
		using CancellationTokenSource cts = new();
		cts.Cancel();

		await Assert.ThrowsAsync<OperationCanceledException>(() => this.jsonRpc.NotifyAsync("Add", NilMsgPack, cts.Token).AsTask());
		Assert.False(this.channel.Reader.TryRead(out _));
	}

	public override void Dispose()
	{
		this.jsonRpc.Dispose();
		base.Dispose();
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
}
