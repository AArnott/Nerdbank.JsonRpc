// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Channels;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using PolyType;

/// <summary>
/// Tests the server-side functions of <see cref="JsonRpc"/> by mocking the
/// client request messages and directly analyzing the server response messages.
/// </summary>
public partial class JsonRpcServerTests : TestBase
{
	private readonly Channel<JsonRpcMessage> channel;
	private readonly JsonRpc jsonRpc;
	private readonly MockServer server = new();

	public JsonRpcServerTests()
	{
		(this.channel, Channel<JsonRpcMessage> jsonRpcChannel) = MockChannel<JsonRpcMessage>.CreatePair();
		this.jsonRpc = new(jsonRpcChannel);

		this.jsonRpc.AddRpcTarget(this.server);
		this.jsonRpc.Start();
	}

	[Fact]
	public async Task SimpleRequestResponse()
	{
		await this.channel.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = nameof(MockServer.GetMagicNumber) }, this.TimeoutToken);
		JsonRpcResult result = Assert.IsType<JsonRpcResult>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		int resultValue = this.jsonRpc.Serializer.Deserialize(result.Result, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken);
		Assert.Equal(42, resultValue);
	}

	[Fact]
	public async Task SimpleRequestResponse_StringId()
	{
		await this.channel.Writer.WriteAsync(new JsonRpcRequest { Id = "Abc", Method = nameof(MockServer.GetMagicNumber) }, this.TimeoutToken);
		JsonRpcResult result = Assert.IsType<JsonRpcResult>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal("Abc", result.Id);
		int resultValue = this.jsonRpc.Serializer.Deserialize(result.Result, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken);
		Assert.Equal(42, resultValue);
	}

	[Fact]
	public async Task PauseAsync_Unpause()
	{
		await this.channel.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = nameof(MockServer.PauseAsync) }, this.TimeoutToken);

		// Confirm that the response is not yet available.
		Task<JsonRpcMessage> responseMessageTask = this.channel.Reader.ReadAsync(this.TimeoutToken).AsTask();
		await Task.Delay(ExpectedTimeout, this.TimeoutToken);
		Assert.False(responseMessageTask.IsCompleted);

		// Unblock the server and confirm response.
		this.server.Unpause.Set();
		JsonRpcMessage responseMessage = await responseMessageTask;
		JsonRpcResult result = Assert.IsType<JsonRpcResult>(responseMessage);
		int resultValue = this.jsonRpc.Serializer.Deserialize(result.Result, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken);
		Assert.Equal(42, resultValue);
	}

	[Fact]
	public async Task PauseAsync_Cancel()
	{
		await this.channel.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = nameof(MockServer.PauseAsync) }, this.TimeoutToken);

		// Wait for the method to be invoked, then cancel it.
		await this.server.PauseReached.WaitAsync(this.TimeoutToken);
		await this.channel.Writer.WriteAsync(this.CreateCancellationRequest(1), this.TimeoutToken);

		JsonRpcMessage responseMessage = await this.channel.Reader.ReadAsync(this.TimeoutToken);
		JsonRpcError error = Assert.IsType<JsonRpcError>(responseMessage);
		this.Logger?.WriteLine($"Received error: {error.Error.Message}");
		Assert.Contains(MockServer.CancellationAcknowledgementMessage, error.Error.Message);
	}

	[Fact]
	public async Task Add_PositionalArgs()
	{
		Sequence<byte> seq = new();
		MessagePackWriter msgpackWriter = new(seq);
		msgpackWriter.WriteArrayHeader(2);
		msgpackWriter.Write(3);
		msgpackWriter.Write(2);
		msgpackWriter.Flush();

		await this.channel.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = nameof(MockServer.Add), Arguments = (RawMessagePack)seq.AsReadOnlySequence }, this.TimeoutToken);
		JsonRpcResult result = Assert.IsType<JsonRpcResult>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		int resultValue = this.jsonRpc.Serializer.Deserialize(result.Result, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken);
		Assert.Equal(5, resultValue);
	}

	[Fact]
	public async Task AddValueTask_PositionalArgs()
	{
		Sequence<byte> seq = new();
		MessagePackWriter msgpackWriter = new(seq);
		msgpackWriter.WriteArrayHeader(2);
		msgpackWriter.Write(3);
		msgpackWriter.Write(2);
		msgpackWriter.Flush();

		await this.channel.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = nameof(MockServer.AddValueTask), Arguments = (RawMessagePack)seq.AsReadOnlySequence }, this.TimeoutToken);
		JsonRpcResult result = Assert.IsType<JsonRpcResult>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		int resultValue = this.jsonRpc.Serializer.Deserialize(result.Result, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken);
		Assert.Equal(5, resultValue);
	}

	[Fact]
	public async Task AddTask_PositionalArgs()
	{
		Sequence<byte> seq = new();
		MessagePackWriter msgpackWriter = new(seq);
		msgpackWriter.WriteArrayHeader(2);
		msgpackWriter.Write(3);
		msgpackWriter.Write(2);
		msgpackWriter.Flush();

		await this.channel.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = nameof(MockServer.AddTask), Arguments = (RawMessagePack)seq.AsReadOnlySequence }, this.TimeoutToken);
		JsonRpcResult result = Assert.IsType<JsonRpcResult>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		int resultValue = this.jsonRpc.Serializer.Deserialize(result.Result, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken);
		Assert.Equal(5, resultValue);
	}

	[Fact]
	public async Task AddTask_PositionalArgs_TooFew()
	{
		Sequence<byte> seq = new();
		MessagePackWriter msgpackWriter = new(seq);
		msgpackWriter.WriteArrayHeader(1);
		msgpackWriter.Write(3);
		msgpackWriter.Flush();

		await this.channel.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = nameof(MockServer.AddTask), Arguments = (RawMessagePack)seq.AsReadOnlySequence }, this.TimeoutToken);
		JsonRpcError error = Assert.IsType<JsonRpcError>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(JsonRpcErrorCode.InvalidParams, error.Error.Code);
		this.Logger?.WriteLine(error.Error.Message);
	}

	[Fact]
	public async Task AddTask_PositionalArgs_TooMany()
	{
		Sequence<byte> seq = new();
		MessagePackWriter msgpackWriter = new(seq);
		msgpackWriter.WriteArrayHeader(3);
		msgpackWriter.Write(3);
		msgpackWriter.Write(2);
		msgpackWriter.Write(1);
		msgpackWriter.Flush();

		await this.channel.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = nameof(MockServer.AddTask), Arguments = (RawMessagePack)seq.AsReadOnlySequence }, this.TimeoutToken);
		JsonRpcError error = Assert.IsType<JsonRpcError>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(JsonRpcErrorCode.InvalidParams, error.Error.Code);
		this.Logger?.WriteLine(error.Error.Message);
	}

	[Fact]
	public async Task Add_NamedArgs()
	{
		Sequence<byte> seq = new();
		MessagePackWriter msgpackWriter = new(seq);
		msgpackWriter.WriteMapHeader(2);
		msgpackWriter.Write("a");
		msgpackWriter.Write(3);
		msgpackWriter.Write("b");
		msgpackWriter.Write(2);
		msgpackWriter.Flush();

		await this.channel.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = nameof(MockServer.Add), Arguments = (RawMessagePack)seq.AsReadOnlySequence }, this.TimeoutToken);
		JsonRpcResult result = Assert.IsType<JsonRpcResult>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		int resultValue = this.jsonRpc.Serializer.Deserialize(result.Result, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken);
		Assert.Equal(5, resultValue);
	}

	[Fact]
	public async Task Add_NamedArgs_TooFewArguments()
	{
		Sequence<byte> seq = new();
		MessagePackWriter msgpackWriter = new(seq);
		msgpackWriter.WriteMapHeader(1);
		msgpackWriter.Write("a");
		msgpackWriter.Write(3);
		msgpackWriter.Flush();

		await this.channel.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = nameof(MockServer.Add), Arguments = (RawMessagePack)seq.AsReadOnlySequence }, this.TimeoutToken);
		JsonRpcError error = Assert.IsType<JsonRpcError>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(JsonRpcErrorCode.InvalidParams, error.Error.Code);
		this.Logger?.WriteLine(error.Error.Message);
	}

	[Fact]
	public async Task Add_NamedArgs_TooManyArguments()
	{
		Sequence<byte> seq = new();
		MessagePackWriter msgpackWriter = new(seq);
		msgpackWriter.WriteMapHeader(3);
		msgpackWriter.Write("a");
		msgpackWriter.Write(3);
		msgpackWriter.Write("b");
		msgpackWriter.Write(2);
		msgpackWriter.Write("c");
		msgpackWriter.Write(2);
		msgpackWriter.Flush();

		await this.channel.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = nameof(MockServer.Add), Arguments = (RawMessagePack)seq.AsReadOnlySequence }, this.TimeoutToken);
		JsonRpcError error = Assert.IsType<JsonRpcError>(await this.channel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(JsonRpcErrorCode.InvalidParams, error.Error.Code);
		this.Logger?.WriteLine(error.Error.Message);
	}

	public override void Dispose()
	{
		this.jsonRpc.Dispose();
		base.Dispose();
	}

	private JsonRpcRequest CreateCancellationRequest(RequestId id)
	{
		Sequence<byte> seq = new();
		MessagePackWriter msgpackWriter = new(seq);
		msgpackWriter.WriteArrayHeader(1);
		this.jsonRpc.Serializer.Serialize(ref msgpackWriter, id, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.RequestId, this.TimeoutToken);
		msgpackWriter.Flush();

		return new JsonRpcRequest { Method = "$/cancelRequest", Arguments = (RawMessagePack)seq.AsReadOnlySequence };
	}

	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class MockServer
	{
		internal const string CancellationAcknowledgementMessage = "PauseAsync acknowledges cancellation";

		internal AsyncManualResetEvent PauseReached { get; } = new();

		internal AsyncManualResetEvent Unpause { get; } = new();

		public int GetMagicNumber() => 42;

		public int Add(int a, int b) => a + b;

		public ValueTask<int> AddValueTask(int a, int b) => new(a + b);

		public Task<int> AddTask(int a, int b) => Task.FromResult(a + b);

		public async Task<int> PauseAsync(CancellationToken cancellationToken)
		{
			this.PauseReached.Set();
			try
			{
				await this.Unpause.WaitAsync(cancellationToken);
				return 42;
			}
			catch (OperationCanceledException ex)
			{
				throw new OperationCanceledException(CancellationAcknowledgementMessage, ex);
			}
			finally
			{
				this.PauseReached.Reset();
			}
		}
	}

	[GenerateShapeFor<RequestId>]
	private partial class Witness;
}
