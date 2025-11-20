// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Channels;
using Microsoft.VisualStudio.Threading;
using PolyType;

public partial class JsonRpcClientTests : TestBase
{
	private readonly JsonRpc jsonRpc;
	private readonly ChannelWriter<JsonRpcMessage> inboundWriter;

	public JsonRpcClientTests()
	{
		this.jsonRpc = new();

		Channel<JsonRpcMessage> channel = Channel.CreateUnbounded<JsonRpcMessage>();
		this.inboundWriter = channel.Writer;
		this.jsonRpc.Start(channel.Reader);
	}

	[Fact]
	public async Task RequestWithoutStartingFirst()
	{
		JsonRpc jsonRpc = new();
		InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
			async () => await jsonRpc.RequestAsync<AddNamedArguments, int, Witness>("Add", new AddNamedArguments { A = 2, B = 3 }, this.TimeoutToken));
		this.Logger?.WriteLine(ex.Message);
	}

	[Fact]
	public async Task RequestWithNamedArguments()
	{
		Task<int> resultTask = this.jsonRpc.RequestAsync<AddNamedArguments, int, Witness>("Add", new AddNamedArguments { A = 2, B = 3 }, this.TimeoutToken).AsTask();
		JsonRpcRequest requestMessage = Assert.IsAssignableFrom<JsonRpcRequest>(await this.jsonRpc.OutboundMessages.ReadAsync(this.TimeoutToken));
		this.Log(requestMessage, this.jsonRpc);
		Assert.NotNull(requestMessage.Id);
		Assert.Equal("Add", requestMessage.Method);

		JsonRpcResult resultMessage = new()
		{
			Id = requestMessage.Id.Value,
			Result = (RawMessagePack)this.jsonRpc.Serializer.Serialize<int, Witness>(5, TestContext.Current.CancellationToken),
		};
		await this.inboundWriter.WriteAsync(resultMessage, this.TimeoutToken);

		int result = await resultTask.WithCancellation(this.TimeoutToken);
		Assert.Equal(5, result);
	}

	[Fact]
	public async Task RequestWithPositionalArguments()
	{
		Task<int> resultTask = this.jsonRpc.RequestAsync<AddPositionalArguments, int, Witness>("Add", new AddPositionalArguments { A = 2, B = 3 }, this.TimeoutToken).AsTask();
		JsonRpcRequest requestMessage = Assert.IsAssignableFrom<JsonRpcRequest>(await this.jsonRpc.OutboundMessages.ReadAsync(this.TimeoutToken));
		this.Log(requestMessage, this.jsonRpc);
		Assert.NotNull(requestMessage.Id);
		Assert.Equal("Add", requestMessage.Method);

		JsonRpcResult resultMessage = new()
		{
			Id = requestMessage.Id.Value,
			Result = (RawMessagePack)this.jsonRpc.Serializer.Serialize<int, Witness>(5, TestContext.Current.CancellationToken),
		};
		await this.inboundWriter.WriteAsync(resultMessage, this.TimeoutToken);

		int result = await resultTask.WithCancellation(this.TimeoutToken);
		Assert.Equal(5, result);
	}

	[Fact]
	public async Task RequestWithNoReturnValue()
	{
		Task resultTask = this.jsonRpc.RequestAsync("Add", new AddNamedArguments { A = 2, B = 3 }, this.TimeoutToken).AsTask();
		JsonRpcRequest requestMessage = Assert.IsAssignableFrom<JsonRpcRequest>(await this.jsonRpc.OutboundMessages.ReadAsync(this.TimeoutToken));
		this.Log(requestMessage, this.jsonRpc);
		Assert.NotNull(requestMessage.Id);
		Assert.Equal("Add", requestMessage.Method);

		JsonRpcResult resultMessage = new()
		{
			Id = requestMessage.Id.Value,
			Result = NilMsgPack,
		};
		await this.inboundWriter.WriteAsync(resultMessage, this.TimeoutToken);

		await resultTask.WithCancellation(this.TimeoutToken);
	}

	[Fact]
	public async Task CancelPendingRequest()
	{
		CancellationTokenSource cts = new();
		Task resultTask = this.jsonRpc.RequestAsync("Add", new AddNamedArguments { A = 2, B = 3 }, cts.Token).AsTask();
		JsonRpcRequest requestMessage = Assert.IsAssignableFrom<JsonRpcRequest>(await this.jsonRpc.OutboundMessages.ReadAsync(this.TimeoutToken));
		this.Log(requestMessage, this.jsonRpc);
		Assert.NotNull(requestMessage.Id);
		Assert.Equal("Add", requestMessage.Method);

		// Cancel the request and verify that a cancellation notification is transmitted.
		cts.Cancel();
		JsonRpcRequest cancelMessage = Assert.IsAssignableFrom<JsonRpcRequest>(await this.jsonRpc.OutboundMessages.ReadAsync(this.TimeoutToken));
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
		await this.inboundWriter.WriteAsync(errorMessage, this.TimeoutToken);

		// Verify that the client finally resolves.
		JsonRpcException ex = await Assert.ThrowsAsync<JsonRpcException>(() => resultTask.WithCancellation(this.TimeoutToken));
		Assert.Equal(JsonRpcErrorCode.RequestCancelled, ex.ErrorDetails.Code);
	}

	[Fact]
	public async Task Notify()
	{
		this.jsonRpc.Notify("Add", new AddNamedArguments { A = 2, B = 3 }, this.TimeoutToken);
		JsonRpcRequest requestMessage = Assert.IsAssignableFrom<JsonRpcRequest>(await this.jsonRpc.OutboundMessages.ReadAsync(this.TimeoutToken));
		Assert.Null(requestMessage.Id);
		Assert.Equal("Add", requestMessage.Method);
		this.Log(requestMessage, this.jsonRpc);
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
