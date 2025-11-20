// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Channels;
using Microsoft.VisualStudio.Threading;
using Nerdbank.JsonRpc;
using Nerdbank.MessagePack;
using PolyType;
using Xunit;

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
		Assert.NotNull(requestMessage.Id);
		Assert.Equal("Add", requestMessage.Method);
		this.Log(requestMessage, this.jsonRpc);

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
		Assert.NotNull(requestMessage.Id);
		Assert.Equal("Add", requestMessage.Method);
		this.Log(requestMessage, this.jsonRpc);

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
		Assert.NotNull(requestMessage.Id);
		Assert.Equal("Add", requestMessage.Method);
		this.Log(requestMessage, this.jsonRpc);

		JsonRpcResult resultMessage = new()
		{
			Id = requestMessage.Id.Value,
			Result = NilMsgPack,
		};
		await this.inboundWriter.WriteAsync(resultMessage, this.TimeoutToken);

		await resultTask.WithCancellation(this.TimeoutToken);
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
	private partial class Witness;
}
