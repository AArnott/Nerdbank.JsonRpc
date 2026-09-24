// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using PolyType;

public class JsonCodecTests : TestBase
{
	[Theory]
	[InlineData(JsonRpcJsonFraming.NewlineDelimited)]
	[InlineData(JsonRpcJsonFraming.ContentLength)]
	public async Task DirectGeneratedProxyAndBatch(JsonRpcJsonFraming framing)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		JsonSerializerPlugin clientPlugin = new(new Nerdbank.Json.JsonSerializer());
		JsonSerializerPlugin serverPlugin = new(new Nerdbank.Json.JsonSerializer());
		await using JsonRpcJsonChannel clientChannel = new(clientPipe, clientPlugin, framing, LoggerFactory.CreateLogger("client"));
		await using JsonRpcJsonChannel serverChannel = new(serverPipe, serverPlugin, framing, LoggerFactory.CreateLogger("server"));
		using JsonRpc client = new(clientChannel) { Serializer = clientPlugin };
		using JsonRpc server = new(serverChannel) { Serializer = serverPlugin };
		Calculator calculator = new();
		server.AddRpcTarget<ICalculator>(calculator);
		server.AddRpcTarget<INamedCalculator>(new NamedCalculator(), PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.INamedCalculator);
		client.Start();
		server.Start();

		ICalculator proxy = client.Attach<ICalculator>();
		Assert.Equal(7, await proxy.AddAsync(3, 4, this.TimeoutToken));
		Assert.Equal(12, await proxy.MultiplyAsync(3, 4, this.TimeoutToken));
		Assert.Equal(11, await client.RequestAsync(nameof(ICalculator.AddAsync), new JsonDirectArgs { A = 5, B = 6 }, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.JsonDirectArgs, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken));
		Assert.Equal(2, await client.Attach<INamedCalculator>().SubtractAsync(5, 3, this.TimeoutToken));
		JsonRpcBatch batch = client.CreateBatch();
		ICalculator batchProxy = batch.Attach<ICalculator>();
		Task<int> sum = batchProxy.AddAsync(20, 22, this.TimeoutToken).AsTask();
		Task<int> product = batchProxy.MultiplyAsync(2, 3, this.TimeoutToken);
		batchProxy.SetLastValue(99, this.TimeoutToken);
		await batch.SendAsync(this.TimeoutToken);
		Assert.Equal(42, await sum);
		Assert.Equal(6, await product);
		Assert.Equal(99, await calculator.NotificationReceived.Task.WithCancellation(this.TimeoutToken));
	}

	[Theory]
	[InlineData(JsonRpcJsonFraming.NewlineDelimited)]
	[InlineData(JsonRpcJsonFraming.ContentLength)]
	public async Task PreservesIdTypesAndValidatesEnvelope(JsonRpcJsonFraming framing)
	{
		(IDuplexPipe firstPipe, IDuplexPipe secondPipe) = FullDuplexStream.CreatePipePair();
		JsonSerializerPlugin firstPlugin = new(new Nerdbank.Json.JsonSerializer { WriteIndented = true });
		JsonSerializerPlugin secondPlugin = new(new Nerdbank.Json.JsonSerializer());
		await using JsonRpcJsonChannel first = new(firstPipe, firstPlugin, framing, LoggerFactory.CreateLogger("first"));
		await using JsonRpcJsonChannel second = new(secondPipe, secondPlugin, framing, LoggerFactory.CreateLogger("second"));
		foreach ((RequestId id, string expected) in new[] { (new RequestId("15"), "15"), (new RequestId(15), "15"), (default(RequestId), "null"), (default(RequestId), "null") })
		{
			await first.Writer.WriteAsync(new JsonRpcRequest { Method = "echo", Id = id, Arguments = JsonRpcValue.FromJson("[]"u8.ToArray()) }, this.TimeoutToken);
			JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await second.Reader.ReadAsync(this.TimeoutToken));
			Assert.True(request.HasId);
			Assert.Equal(id, request.Id);
			await second.Writer.WriteAsync(new JsonRpcResult { Id = request.Id!.Value, Result = JsonRpcValue.FromJson("null"u8.ToArray()) }, this.TimeoutToken);
			JsonRpcResult result = Assert.IsType<JsonRpcResult>(await first.Reader.ReadAsync(this.TimeoutToken));
			Assert.Equal(id, result.Id);
			Assert.Equal(expected, result.Id.ToString());
		}

		JsonRpcValue indented = firstPlugin.Serialize(new[] { 1, 2 }, PolyType.Abstractions.TypeShapeResolver.ResolveDynamicOrThrow<int[], JsonArrayWitness>(), this.TimeoutToken);
		Assert.Contains('\n', Encoding.UTF8.GetString(indented.Bytes.ToArray()));
		await first.Writer.WriteAsync(new JsonRpcResult { Id = new RequestId(99), Result = indented }, this.TimeoutToken);
		JsonRpcResult compactResult = Assert.IsType<JsonRpcResult>(await second.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal("[1,2]", Encoding.UTF8.GetString(compactResult.Result.Bytes.ToArray()));

		await second.Writer.WriteAsync(new JsonRpcError { Id = new RequestId("err"), Error = new() { Code = -32603, Message = "error", Data = JsonRpcValue.FromJson("null"u8.ToArray()) } }, this.TimeoutToken);
		JsonRpcError error = Assert.IsType<JsonRpcError>(await first.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(JsonRpcEncoding.Json, error.Error.Data!.Value.Encoding);
		Assert.Equal("null", Encoding.UTF8.GetString(error.Error.Data.Value.Bytes.ToArray()));

		await first.Writer.WriteAsync(new JsonRpcRequest { Method = "notify", Arguments = JsonRpcValue.FromJson("[]"u8.ToArray()) }, this.TimeoutToken);
		Assert.False(Assert.IsType<JsonRpcRequest>(await second.Reader.ReadAsync(this.TimeoutToken)).HasId);
	}

	[Theory]
	[InlineData(JsonRpcJsonFraming.NewlineDelimited)]
	[InlineData(JsonRpcJsonFraming.ContentLength)]
	public async Task BadResultOnlyFaultsMatchingRequest(JsonRpcJsonFraming framing)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		JsonSerializerPlugin plugin = new(new Nerdbank.Json.JsonSerializer());
		await using JsonRpcJsonChannel clientChannel = new(clientPipe, plugin, framing, LoggerFactory.CreateLogger("client"));
		await using JsonRpcJsonChannel serverChannel = new(serverPipe, new JsonSerializerPlugin(new Nerdbank.Json.JsonSerializer()), framing, LoggerFactory.CreateLogger("server"));
		using JsonRpc client = new(clientChannel) { Serializer = plugin };
		client.Start();
		ITypeShape<int> intShape = PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32;
		Task<int> first = client.RequestAsync("first", JsonRpcValue.FromJson("[]"u8.ToArray()), intShape, this.TimeoutToken).AsTask();
		Task<int> second = client.RequestAsync("second", JsonRpcValue.FromJson("[]"u8.ToArray()), intShape, this.TimeoutToken).AsTask();
		JsonRpcRequest firstRequest = Assert.IsType<JsonRpcRequest>(await serverChannel.Reader.ReadAsync(this.TimeoutToken));
		JsonRpcRequest secondRequest = Assert.IsType<JsonRpcRequest>(await serverChannel.Reader.ReadAsync(this.TimeoutToken));
		JsonRpcMessageBatch results = new([
			new JsonRpcResult { Id = firstRequest.Id!.Value, Result = JsonRpcValue.FromJson("\"not an integer\""u8.ToArray()) },
			new JsonRpcResult { Id = secondRequest.Id!.Value, Result = JsonRpcValue.FromJson("42"u8.ToArray()) },
		]);
		await serverChannel.Writer.WriteAsync(results, this.TimeoutToken);
		await Assert.ThrowsAnyAsync<Exception>(() => first.WithCancellation(this.TimeoutToken));
		Assert.Equal(42, await second.WithCancellation(this.TimeoutToken));
		Assert.False(client.Completion.IsFaulted);
	}

	[Fact]
	public async Task RawValueOwnsBytesAndRejectsWrongEncoding()
	{
		byte[] buffer = "[1]"u8.ToArray();
		JsonRpcValue json = JsonRpcValue.FromJson(buffer);
		buffer[1] = (byte)'9';
		Assert.Equal("[1]", Encoding.UTF8.GetString(json.Bytes.ToArray()));
		Assert.Throws<InvalidOperationException>(() => json.AsMessagePack());
		Assert.ThrowsAny<Exception>(() => JsonRpcValue.FromJson("[1] garbage"u8.ToArray()));
		Assert.False(default(JsonRpcValue).HasValue);
		Assert.True(JsonRpcValue.FromJson("null"u8.ToArray()).HasValue);
		byte[] msgpackBytes = [(byte)MessagePackCode.Nil];
		JsonRpcValue msgpack = JsonRpcValue.FromMessagePack((RawMessagePack)msgpackBytes);
		msgpackBytes[0] = 1;
		Assert.Equal((byte)MessagePackCode.Nil, msgpack.Bytes.Span[0]);
		Assert.ThrowsAny<Exception>(() => JsonRpcValue.FromMessagePack((RawMessagePack)new byte[] { (byte)MessagePackCode.Nil, (byte)MessagePackCode.Nil }));

		JsonSerializerPlugin plugin = new(new Nerdbank.Json.JsonSerializer());
		(IDuplexPipe local, _) = FullDuplexStream.CreatePipePair();
		JsonRpcJsonChannel channel = new(local, plugin, JsonRpcJsonFraming.NewlineDelimited, LoggerFactory.CreateLogger("local"));
		using JsonRpc rpc = new(channel) { Serializer = plugin };
		rpc.Start();
		Assert.Throws<ArgumentException>(() => rpc.NotifyAsync("method", JsonRpcValue.FromMessagePack(NilMsgPack), this.TimeoutToken));
		Assert.Throws<ArgumentException>(() => rpc.NotifyAsync("method", JsonRpcValue.FromJson("42"u8.ToArray()), this.TimeoutToken));
		JsonRpcBatch batch = rpc.CreateBatch();
		Assert.Throws<ArgumentException>(() => batch.RequestAsync("method", JsonRpcValue.FromMessagePack(NilMsgPack), this.TimeoutToken));
		await channel.DisposeAsync();
	}

	[Theory]
	[InlineData("")]
	[InlineData("  ")]
	[InlineData("[1] true")]
	[InlineData("[1] garbage")]
	[InlineData("[1,")]
	[InlineData("{\"key\":}")]
	[InlineData("\"unterminated")]
	public void RawJsonRejectsIncompleteOrMultipleValues(string json)
	{
		Assert.ThrowsAny<System.Text.Json.JsonException>(() => JsonRpcValue.FromJson(Encoding.UTF8.GetBytes(json)));
	}

	[Theory]
	[InlineData("null ")]
	[InlineData("  {\"name\": [1, 2]} \n")]
	[InlineData("\"hello\"")]
	public void RawJsonAcceptsCompleteValueWithWhitespace(string json)
	{
		Assert.Equal(json, Encoding.UTF8.GetString(JsonRpcValue.FromJson(Encoding.UTF8.GetBytes(json)).Bytes.ToArray()));
	}

	[Theory]
	[InlineData(JsonRpcJsonFraming.NewlineDelimited)]
	[InlineData(JsonRpcJsonFraming.ContentLength)]
	public async Task BatchCancellationUsesSelectedSerializer(JsonRpcJsonFraming framing)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		Nerdbank.Json.JsonSerializer configured = new();
		await using JsonRpcJsonChannel clientChannel = new(clientPipe, configured, framing, LoggerFactory.CreateLogger("client"));
		await using JsonRpcJsonChannel serverChannel = new(serverPipe, new Nerdbank.Json.JsonSerializer(), framing, LoggerFactory.CreateLogger("server"));
		using JsonRpc client = new(clientChannel) { Serializer = configured };
		Assert.Same(configured, Assert.IsType<JsonSerializerPlugin>(client.Serializer).Serializer);
		client.Start();

		JsonRpcBatch batch = client.CreateBatch();
		Task<int> pending = batch.RequestAsync<int>("slow", JsonRpcValue.FromJson("[]"u8.ToArray()), PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken).AsTask();
		await batch.SendAsync(this.TimeoutToken);
		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(Assert.Single(Assert.IsType<JsonRpcMessageBatch>(await serverChannel.Reader.ReadAsync(this.TimeoutToken)).Messages));
		await batch.CancelAllAsync();
		JsonRpcRequest cancel = Assert.IsType<JsonRpcRequest>(Assert.Single(Assert.IsType<JsonRpcMessageBatch>(await serverChannel.Reader.ReadAsync(this.TimeoutToken)).Messages));
		Assert.Equal("$/cancelRequest", cancel.Method);
		Assert.Equal(JsonRpcEncoding.Json, cancel.Arguments.Encoding);
		Assert.Contains(request.Id!.Value.ToString(), Encoding.UTF8.GetString(cancel.Arguments.Bytes.ToArray()));
		await serverChannel.Writer.WriteAsync(new JsonRpcResult { Id = request.Id!.Value, Result = JsonRpcValue.FromJson("1"u8.ToArray()) }, this.TimeoutToken);
		Assert.Equal(1, await pending.WithCancellation(this.TimeoutToken));
	}

	[Theory]
	[InlineData(JsonRpcJsonFraming.NewlineDelimited)]
	[InlineData(JsonRpcJsonFraming.ContentLength)]
	public async Task ServerObservesCancellation(JsonRpcJsonFraming framing)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		JsonSerializerPlugin clientPlugin = new(new Nerdbank.Json.JsonSerializer());
		JsonSerializerPlugin serverPlugin = new(new Nerdbank.Json.JsonSerializer());
		await using JsonRpcJsonChannel clientChannel = new(clientPipe, clientPlugin, framing, LoggerFactory.CreateLogger("client"));
		await using JsonRpcJsonChannel serverChannel = new(serverPipe, serverPlugin, framing, LoggerFactory.CreateLogger("server"));
		using JsonRpc client = new(clientChannel) { Serializer = clientPlugin };
		using JsonRpc server = new(serverChannel) { Serializer = serverPlugin };
		CancellableTarget target = new();
		server.AddRpcTarget<ICancellableTarget>(target, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.ICancellableTarget);
		server.Start();
		client.Start();

		JsonRpcBatch batch = client.CreateBatch();
		Task<int> pending = batch.RequestAsync<int>(nameof(ICancellableTarget.WaitAsync), JsonRpcValue.FromJson("[]"u8.ToArray()), PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken).AsTask();
		await batch.SendAsync(this.TimeoutToken);
		await target.Started.Task.WithCancellation(this.TimeoutToken);
		await batch.CancelAllAsync();
		JsonRpcException exception = await Assert.ThrowsAsync<JsonRpcException>(() => pending.WithCancellation(this.TimeoutToken));
		Assert.Equal(JsonRpcErrorCode.RequestCancelled, exception.ErrorDetails.Code);
	}

	[Theory]
	[InlineData(JsonRpcJsonFraming.NewlineDelimited)]
	[InlineData(JsonRpcJsonFraming.ContentLength)]
	public async Task RequestArgumentErrorEchoesPresentIdWithoutTerminatingServer(JsonRpcJsonFraming framing)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		JsonSerializerPlugin clientPlugin = new(new Nerdbank.Json.JsonSerializer());
		JsonSerializerPlugin serverPlugin = new(new Nerdbank.Json.JsonSerializer());
		await using JsonRpcJsonChannel clientChannel = new(clientPipe, clientPlugin, framing, LoggerFactory.CreateLogger("client"));
		await using JsonRpcJsonChannel serverChannel = new(serverPipe, serverPlugin, framing, LoggerFactory.CreateLogger("server"));
		using JsonRpc server = new(serverChannel) { Serializer = serverPlugin };
		server.AddRpcTarget<ICalculator>(new Calculator(), PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.ICalculator);
		server.Start();

		foreach (RequestId id in new[] { new RequestId("15"), default(RequestId), new RequestId(ulong.MaxValue) })
		{
			await clientChannel.Writer.WriteAsync(new JsonRpcRequest { Method = nameof(ICalculator.AddAsync), Id = id, Arguments = JsonRpcValue.FromJson("[\"bad\",1]"u8.ToArray()) }, this.TimeoutToken);
			JsonRpcError error = Assert.IsType<JsonRpcError>(await clientChannel.Reader.ReadAsync(this.TimeoutToken));
			Assert.Equal(id, error.Id);
			Assert.Equal(JsonRpcErrorCode.InvalidParams, error.Error.Code);
		}

		await clientChannel.Writer.WriteAsync(new JsonRpcRequest { Method = nameof(ICalculator.AddAsync), Id = new RequestId(42), Arguments = JsonRpcValue.FromJson("[20,22]"u8.ToArray()) }, this.TimeoutToken);
		JsonRpcResult result = Assert.IsType<JsonRpcResult>(await clientChannel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(42, serverPlugin.Deserialize(result.Result, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken));
		Assert.False(server.Completion.IsFaulted);
	}

	[Theory]
	[InlineData(JsonRpcJsonFraming.NewlineDelimited)]
	[InlineData(JsonRpcJsonFraming.ContentLength)]
	public async Task HandlesFragmentedAndCoalescedFrames(JsonRpcJsonFraming framing)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcJsonChannel channel = new(local, new Nerdbank.Json.JsonSerializer(), framing, LoggerFactory.CreateLogger("local"));
		static byte[] Frame(string json, JsonRpcJsonFraming mode)
		{
			byte[] payload = Encoding.UTF8.GetBytes(json);
			return mode == JsonRpcJsonFraming.NewlineDelimited
				? Encoding.UTF8.GetBytes(json + "\n")
				: Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n").Concat(payload).ToArray();
		}

		byte[] first = Frame("{\"jsonrpc\":\"2.0\",\"method\":\"one\",\"id\":1}", framing);
		byte[] second = Frame("{\"jsonrpc\":\"2.0\",\"method\":\"two\",\"id\":2}", framing);
		peer.Output.Write(first.AsSpan(0, 8));
		await peer.Output.FlushAsync(this.TimeoutToken);
		Task<JsonRpcMessage> pending = channel.Reader.ReadAsync(this.TimeoutToken).AsTask();
		Assert.False(pending.IsCompleted);
		peer.Output.Write(first.AsSpan(8));
		peer.Output.Write(second);
		await peer.Output.FlushAsync(this.TimeoutToken);
		Assert.Equal("one", Assert.IsType<JsonRpcRequest>(await pending.WithCancellation(this.TimeoutToken)).Method);
		Assert.Equal("two", Assert.IsType<JsonRpcRequest>(await channel.Reader.ReadAsync(this.TimeoutToken)).Method);
	}

	[Theory]
	[InlineData(JsonRpcJsonFraming.NewlineDelimited, "{\"jsonrpc\":\"2.0\",\"method\":\"partial\"")]
	[InlineData(JsonRpcJsonFraming.ContentLength, "Content-Length: 100\r\n\r\n{}")]
	[InlineData(JsonRpcJsonFraming.NewlineDelimited, "")]
	[InlineData(JsonRpcJsonFraming.ContentLength, "Content-Length: 0\r\n\r\n")]
	public async Task InvalidOrIncompleteFramingFaultsTransport(JsonRpcJsonFraming framing, string incompleteFrame)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcJsonChannel channel = new(local, new Nerdbank.Json.JsonSerializer(), framing, LoggerFactory.CreateLogger("local"));
		peer.Output.Write(Encoding.UTF8.GetBytes(framing == JsonRpcJsonFraming.NewlineDelimited && incompleteFrame.Length == 0 ? "\n" : incompleteFrame));
		await peer.Output.CompleteAsync();
		await Assert.ThrowsAnyAsync<Exception>(() => channel.Reader.Completion.WithCancellation(this.TimeoutToken));
	}

	[Theory]
	[InlineData(JsonRpcJsonFraming.NewlineDelimited, "{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"params\":42}")]
	[InlineData(JsonRpcJsonFraming.ContentLength, "{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"params\":42}")]
	[InlineData(JsonRpcJsonFraming.NewlineDelimited, "{\"jsonrpc\":\"2.0\",\"jsonrpc\":\"2.0\",\"method\":\"echo\"}")]
	[InlineData(JsonRpcJsonFraming.ContentLength, "{\"jsonrpc\":\"2.0\",\"id\":1e0,\"method\":\"echo\"}")]
	[InlineData(JsonRpcJsonFraming.NewlineDelimited, "[{\"jsonrpc\":\"2.0\",\"method\":\"echo\"},42]")]
	public async Task InvalidJsonEnvelopeFaultsTransport(JsonRpcJsonFraming framing, string json)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		JsonSerializerPlugin plugin = new(new Nerdbank.Json.JsonSerializer());
		await using JsonRpcJsonChannel channel = new(local, plugin, framing, LoggerFactory.CreateLogger("local"));
		byte[] payload = Encoding.UTF8.GetBytes(json);
		if (framing == JsonRpcJsonFraming.NewlineDelimited)
		{
			peer.Output.Write(Encoding.UTF8.GetBytes(json + "\n"));
		}
		else
		{
			peer.Output.Write(Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n").Concat(payload).ToArray());
		}

		await peer.Output.FlushAsync(this.TimeoutToken);
		await Assert.ThrowsAnyAsync<Exception>(() => channel.Reader.Completion.WithCancellation(this.TimeoutToken));
	}

	[Theory]
	[InlineData(JsonRpcJsonFraming.NewlineDelimited)]
	[InlineData(JsonRpcJsonFraming.ContentLength)]
	public async Task MalformedEnvelopeFaultsPendingCalls(JsonRpcJsonFraming framing)
	{
		(IDuplexPipe clientPipe, IDuplexPipe peerPipe) = FullDuplexStream.CreatePipePair();
		JsonSerializerPlugin plugin = new(new Nerdbank.Json.JsonSerializer());
		await using JsonRpcJsonChannel channel = new(clientPipe, plugin, framing, LoggerFactory.CreateLogger("client"));
		using JsonRpc client = new(channel) { Serializer = plugin };
		client.Start();
		Task<int> pending = client.RequestAsync<int>("method", JsonRpcValue.FromJson("[]"u8.ToArray()), PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken).AsTask();
		byte[] invalid = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"result\":4} ");
		if (framing == JsonRpcJsonFraming.NewlineDelimited)
		{
			peerPipe.Output.Write(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(invalid) + "\n"));
		}
		else
		{
			peerPipe.Output.Write(Encoding.ASCII.GetBytes($"Content-Length: {invalid.Length}\r\n\r\n").Concat(invalid).ToArray());
		}

		await peerPipe.Output.FlushAsync(this.TimeoutToken);
		await Assert.ThrowsAnyAsync<Exception>(() => pending.WithCancellation(this.TimeoutToken));
		Assert.True(client.Completion.IsFaulted);
	}
}
