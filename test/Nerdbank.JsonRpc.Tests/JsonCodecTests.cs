// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
		using JsonRpc client = new(clientChannel);
		using JsonRpc server = new(serverChannel);
		Assert.Same(clientPlugin, ((IJsonRpcClient)client).Serializer);
		Assert.Same(serverPlugin, ((IJsonRpcClient)server).Serializer);
		Calculator calculator = new();
		server.AddRpcTarget<ICalculator>(calculator);
		server.AddRpcTarget<INamedCalculator>(new NamedCalculator(), PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.INamedCalculator);
		client.Start();
		server.Start();

		ICalculator proxy = client.Attach<ICalculator>();
		Assert.Equal(7, await proxy.AddAsync(3, 4, this.TimeoutToken));
		Assert.Equal(7, await client.Attach<ICalculator>(new JsonRpcProxyOptions { UseNamedArguments = true }).AddAsync(3, 4, this.TimeoutToken));
		Assert.Equal(12, await proxy.MultiplyAsync(3, 4, this.TimeoutToken));
		Assert.Equal(11, await client.RequestAsync(nameof(ICalculator.AddAsync), new JsonDirectArgs { A = 5, B = 6 }, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.JsonDirectArgs, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken));
		Assert.Equal(2, await client.Attach<INamedCalculator>(new JsonRpcProxyOptions { UseNamedArguments = true }).SubtractAsync(5, 3, this.TimeoutToken));
		JsonRpcBatch batch = client.CreateBatch();
		ICalculator batchProxy = batch.Attach<ICalculator>();
		Task<int> sum = batchProxy.AddAsync(20, 22, this.TimeoutToken).AsTask();
		Task<int> namedSum = batch.Attach<ICalculator>(new JsonRpcProxyOptions { UseNamedArguments = true }).AddAsync(20, 22, this.TimeoutToken).AsTask();
		Task<int> product = batchProxy.MultiplyAsync(2, 3, this.TimeoutToken);
		batchProxy.SetLastValue(99, this.TimeoutToken);
		await batch.SendAsync(this.TimeoutToken);
		Assert.Equal(42, await sum);
		Assert.Equal(42, await namedSum);
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
		using JsonRpc client = new(clientChannel);
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
	public async Task JsonChannelSharesConfiguredSerializerWithRpc()
	{
		(IDuplexPipe local, _) = FullDuplexStream.CreatePipePair();
		JsonSerializerPlugin plugin = new(new Nerdbank.Json.JsonSerializer());
		await using JsonRpcJsonChannel channel = new(local, plugin, JsonRpcJsonFraming.NewlineDelimited, LoggerFactory.CreateLogger("local"));
		using JsonRpc rpc = new(channel);
		Assert.Same(plugin, channel.Serializer);
		Assert.Same(plugin, ((IJsonRpcClient)rpc).Serializer);
	}

	[Fact]
	public async Task RawValueOwnsBytesAndRejectsWrongEncoding()
	{
		byte[] buffer = "[1]"u8.ToArray();
		JsonRpcValue json = JsonRpcValue.FromJson(buffer);
		buffer[1] = (byte)'9';
		Assert.Equal("[1]", Encoding.UTF8.GetString(json.Bytes.ToArray()));
		Assert.Throws<InvalidOperationException>(() => json.AsMessagePack());
		Assert.False(default(JsonRpcValue).HasValue);
		Assert.True(JsonRpcValue.FromJson("null"u8.ToArray()).HasValue);
		byte[] msgpackBytes = [(byte)MessagePackCode.Nil];
		JsonRpcValue msgpack = JsonRpcValue.FromMessagePack((RawMessagePack)msgpackBytes);
		msgpackBytes[0] = 1;
		Assert.Equal((byte)MessagePackCode.Nil, msgpack.Bytes.Span[0]);

		JsonSerializerPlugin plugin = new(new Nerdbank.Json.JsonSerializer());
		(IDuplexPipe local, _) = FullDuplexStream.CreatePipePair();
		JsonRpcJsonChannel channel = new(local, plugin, JsonRpcJsonFraming.NewlineDelimited, LoggerFactory.CreateLogger("local"));
		using JsonRpc rpc = new(channel);
		rpc.Start();
		ArgumentException jsonMismatch = Assert.Throws<ArgumentException>(() => rpc.NotifyAsync("method", JsonRpcValue.FromMessagePack(NilMsgPack), this.TimeoutToken));
		Assert.Contains("expected Json, actual MessagePack", jsonMismatch.Message);
		using JsonRpc messagePackRpc = new(new MockJsonRpcPipeChannel(Channel.CreateUnbounded<JsonRpcMessage>()));
		ArgumentException messagePackMismatch = Assert.Throws<ArgumentException>(() => messagePackRpc.NotifyAsync("method", JsonRpcValue.FromJson("[]"u8.ToArray()), this.TimeoutToken));
		Assert.Contains("expected MessagePack, actual Json", messagePackMismatch.Message);
		Assert.Throws<ArgumentException>(() => rpc.NotifyAsync("method", JsonRpcValue.FromJson("42"u8.ToArray()), this.TimeoutToken));
		JsonRpcBatch batch = rpc.CreateBatch();
		Assert.Throws<ArgumentException>(() => batch.RequestAsync("method", JsonRpcValue.FromMessagePack(NilMsgPack), this.TimeoutToken));
		await channel.DisposeAsync();
	}

	[Theory]
	[InlineData(JsonRpcEncoding.Json)]
	[InlineData(JsonRpcEncoding.MessagePack)]
	public async Task ErrorDataDistinguishesAbsentAndExplicitNull(JsonRpcEncoding encoding)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel client = encoding == JsonRpcEncoding.Json
			? new JsonRpcJsonChannel(clientPipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited, LoggerFactory.CreateLogger("client"))
			: new JsonRpcMessagePackChannel(clientPipe, LoggerFactory.CreateLogger("client"));
		await using JsonRpcPipeChannel server = encoding == JsonRpcEncoding.Json
			? new JsonRpcJsonChannel(serverPipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited, LoggerFactory.CreateLogger("server"))
			: new JsonRpcMessagePackChannel(serverPipe, LoggerFactory.CreateLogger("server"));
		await client.Writer.WriteAsync(new JsonRpcError { Id = 1, Error = new() { Code = -32603, Message = "oops", Data = default(JsonRpcValue) } }, this.TimeoutToken);
		JsonRpcError response = Assert.IsType<JsonRpcError>(await server.Reader.ReadAsync(this.TimeoutToken));
		Assert.Null(response.Error.Data);

		JsonRpcValue explicitNull = encoding == JsonRpcEncoding.Json
			? JsonRpcValue.FromJson("null"u8.ToArray())
			: JsonRpcValue.FromMessagePack(NilMsgPack);
		await client.Writer.WriteAsync(new JsonRpcError { Id = 2, Error = new() { Code = -32603, Message = "oops", Data = explicitNull } }, this.TimeoutToken);
		response = Assert.IsType<JsonRpcError>(await server.Reader.ReadAsync(this.TimeoutToken));
		Assert.True(response.Error.Data.HasValue);
		Assert.Equal(explicitNull, response.Error.Data.Value);

		JsonRpcValue payload = encoding == JsonRpcEncoding.Json
			? JsonRpcValue.FromJson("[1,2]"u8.ToArray())
			: JsonRpcValue.FromMessagePack((RawMessagePack)new byte[] { 0x92, 0x01, 0x02 });
		await client.Writer.WriteAsync(new JsonRpcError { Id = 3, Error = new() { Code = -32603, Message = "oops", Data = payload } }, this.TimeoutToken);
		response = Assert.IsType<JsonRpcError>(await server.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(payload, response.Error.Data);
	}

	[Fact]
	public async Task MessagePackErrorDataWirePresence()
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcMessagePackChannel channel = new(local, LoggerFactory.CreateLogger("local"));
		MessagePackSerializer serializer = new();
		JsonRpcErrorDetails error = new() { Code = -32603, Message = "oops" };
		await channel.Writer.WriteAsync(new JsonRpcError { Id = 1, Error = error }, this.TimeoutToken);
		ReadResult read = await peer.Input.ReadAsync(this.TimeoutToken);
		using (JsonDocument document = JsonDocument.Parse(serializer.ConvertToJson(read.Buffer.ToArray())))
		{
			Assert.Equal(2, document.RootElement.GetProperty("error").EnumerateObject().Count());
			Assert.False(document.RootElement.GetProperty("error").TryGetProperty("data", out _));
		}

		peer.Input.AdvanceTo(read.Buffer.End);
		error = new() { Code = -32603, Message = "oops", Data = JsonRpcValue.FromMessagePack(NilMsgPack) };
		await channel.Writer.WriteAsync(new JsonRpcError { Id = 2, Error = error }, this.TimeoutToken);
		read = await peer.Input.ReadAsync(this.TimeoutToken);
		using (JsonDocument document = JsonDocument.Parse(serializer.ConvertToJson(read.Buffer.ToArray())))
		{
			Assert.Equal(3, document.RootElement.GetProperty("error").EnumerateObject().Count());
			Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("error").GetProperty("data").ValueKind);
		}

		peer.Input.AdvanceTo(read.Buffer.End);
	}

	[Theory]
	[InlineData("")]
	[InlineData("  ")]
	[InlineData("[1] true")]
	[InlineData("[1] garbage")]
	[InlineData("[1,")]
	[InlineData("{\"key\":}")]
	[InlineData("\"unterminated")]
	public void RawJsonCopiesWithoutValidating(string json)
	{
		byte[] input = Encoding.UTF8.GetBytes(json);
		JsonRpcValue value = JsonRpcValue.FromJson(input);
		Assert.True(value.HasValue);
		Assert.Equal(input, value.Bytes.ToArray());
	}

	[Fact]
	public void RawMessagePackCopiesWithoutValidating()
	{
		byte[] input = [(byte)MessagePackCode.Nil, (byte)MessagePackCode.Nil];
		JsonRpcValue value = JsonRpcValue.FromMessagePack((RawMessagePack)input);
		input[0] = 1;
		Assert.True(value.HasValue);
		Assert.Equal(new byte[] { (byte)MessagePackCode.Nil, (byte)MessagePackCode.Nil }, value.Bytes.ToArray());
		Assert.True(JsonRpcValue.FromMessagePack((RawMessagePack)Array.Empty<byte>()).HasValue);
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
	[InlineData(JsonRpcEncoding.Json, false)]
	[InlineData(JsonRpcEncoding.Json, true)]
	[InlineData(JsonRpcEncoding.MessagePack, false)]
	[InlineData(JsonRpcEncoding.MessagePack, true)]
	public void ArgumentBuilderStreamsCompleteParameters(JsonRpcEncoding encoding, bool named)
	{
		JsonRpcSerializer serializer = encoding == JsonRpcEncoding.Json
			? new JsonSerializerPlugin(new Nerdbank.Json.JsonSerializer { WriteIndented = true })
			: new MessagePackSerializerPlugin(new Nerdbank.MessagePack.MessagePackSerializer());
		using JsonRpc rpc = new(new MockJsonRpcPipeChannel(System.Threading.Channels.Channel.CreateUnbounded<JsonRpcMessage>(), serializer));
		const string Name = "a\"\\\n";
		JsonRpcValue result;
		using (JsonRpcArgumentsBuilder builder = rpc.CreateArguments(named, 2, this.TimeoutToken))
		{
			builder.Add(named ? Name : null, 13, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
			builder.Add(named ? "second" : null, 42, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
			result = builder.Build();
		}

		Assert.Equal(encoding, result.Encoding);
		if (encoding == JsonRpcEncoding.Json)
		{
			using JsonDocument document = JsonDocument.Parse(result.Bytes);
			JsonElement root = document.RootElement;
			Assert.Equal(13, named ? root.GetProperty(Name).GetInt32() : root[0].GetInt32());
			Assert.Equal(42, named ? root.GetProperty("second").GetInt32() : root[1].GetInt32());
		}
		else
		{
			MessagePackReader reader = new(result.AsMessagePack());
			Assert.Equal(2, named ? reader.ReadMapHeader() : reader.ReadArrayHeader());
			if (named)
			{
				Assert.Equal(Name, reader.ReadString());
			}

			Assert.Equal(13, reader.ReadInt32());
			if (named)
			{
				Assert.Equal("second", reader.ReadString());
			}

			Assert.Equal(42, reader.ReadInt32());
			Assert.True(reader.End);
		}
	}

	[Theory]
	[InlineData(JsonRpcEncoding.Json)]
	[InlineData(JsonRpcEncoding.MessagePack)]
	public void ArgumentBuilderRequiresExactCountAndIsSingleUse(JsonRpcEncoding encoding)
	{
		JsonRpcSerializer serializer = encoding == JsonRpcEncoding.Json
			? new JsonSerializerPlugin(new Nerdbank.Json.JsonSerializer())
			: new MessagePackSerializerPlugin(new Nerdbank.MessagePack.MessagePackSerializer());
		using JsonRpc rpc = new(new MockJsonRpcPipeChannel(System.Threading.Channels.Channel.CreateUnbounded<JsonRpcMessage>(), serializer));
		Assert.Throws<ArgumentOutOfRangeException>(() => CreateNegativeCount(rpc));
		Assert.Throws<InvalidOperationException>(() => BuildIncomplete(rpc));
		Assert.Throws<InvalidOperationException>(() => AddTooMany(rpc));
		Assert.Throws<InvalidOperationException>(() => BuildTwice(rpc));
		Assert.Throws<ArgumentNullException>(() => AddUnnamedToNamed(rpc));
		Assert.ThrowsAny<OperationCanceledException>(() => AddCanceled(rpc));
		Assert.ThrowsAny<OperationCanceledException>(() => CancelBetweenArguments(rpc));

		static void CreateNegativeCount(JsonRpc rpc)
		{
			using JsonRpcArgumentsBuilder builder = rpc.CreateArguments(false, -1);
		}

		static void BuildIncomplete(JsonRpc rpc)
		{
			using JsonRpcArgumentsBuilder builder = rpc.CreateArguments(false, 1);
			builder.Build();
		}

		static void AddTooMany(JsonRpc rpc)
		{
			using JsonRpcArgumentsBuilder builder = rpc.CreateArguments(false, 1);
			builder.Add(null, 42, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
			builder.Add(null, 13, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
		}

		static void BuildTwice(JsonRpc rpc)
		{
			using JsonRpcArgumentsBuilder builder = rpc.CreateArguments(false, 0);
			Assert.True(builder.Build().HasValue);
			builder.Build();
		}

		static void AddUnnamedToNamed(JsonRpc rpc)
		{
			using JsonRpcArgumentsBuilder builder = rpc.CreateArguments(true, 1);
			builder.Add(null, 42, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
		}

		static void AddCanceled(JsonRpc rpc)
		{
			using CancellationTokenSource source = new();
			source.Cancel();
			using JsonRpcArgumentsBuilder builder = rpc.CreateArguments(false, 1, source.Token);
			builder.Add(null, 42, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
		}

		static void CancelBetweenArguments(JsonRpc rpc)
		{
			using CancellationTokenSource source = new();
			using JsonRpcArgumentsBuilder builder = rpc.CreateArguments(false, 2, source.Token);
			builder.Add(null, 42, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
			source.Cancel();
			builder.Add(null, 13, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
		}
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
		using JsonRpc client = new(clientChannel);
		Assert.Same(configured, Assert.IsType<JsonSerializerPlugin>(((IJsonRpcClient)client).Serializer).Serializer);
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
		using JsonRpc client = new(clientChannel);
		using JsonRpc server = new(serverChannel);
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
		using JsonRpc server = new(serverChannel);
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
	public async Task WritesExplicitProtocolVersionWithoutReplacingIt(JsonRpcJsonFraming framing)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcJsonChannel channel = new(local, new Nerdbank.Json.JsonSerializer(), framing, LoggerFactory.CreateLogger("local"));
		await channel.Writer.WriteAsync(new JsonRpcRequest { Method = "example", Version = "3.0" }, this.TimeoutToken);
		ReadResult read = await peer.Input.ReadAsync(this.TimeoutToken);
		Assert.Contains("\"jsonrpc\":\"3.0\"", Encoding.UTF8.GetString(read.Buffer.ToArray()));
		peer.Input.AdvanceTo(read.Buffer.End);
	}

	[Fact]
	public async Task InvalidJsonChannelConfigurationDoesNotStartTransport()
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		JsonSerializerPlugin serializer = new(new Nerdbank.Json.JsonSerializer());
		Assert.Throws<ArgumentOutOfRangeException>(() => new JsonRpcJsonChannel(local, serializer, (JsonRpcJsonFraming)int.MaxValue, LoggerFactory.CreateLogger("local")));
		Assert.Throws<ArgumentNullException>(() => new JsonRpcJsonChannel(local, (JsonSerializerPlugin)null!, JsonRpcJsonFraming.NewlineDelimited, LoggerFactory.CreateLogger("local")));
		peer.Output.Write("still available"u8);
		await peer.Output.FlushAsync(this.TimeoutToken);
		ReadResult read = await local.Input.ReadAsync(this.TimeoutToken);
		Assert.Equal("still available", Encoding.UTF8.GetString(read.Buffer.ToArray()));
		local.Input.AdvanceTo(read.Buffer.End);
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
	[InlineData(JsonRpcJsonFraming.NewlineDelimited, "{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"params\":null}")]
	[InlineData(JsonRpcJsonFraming.ContentLength, "{\"jsonrpc\":\"2.0\",\"method\":\"echo\",\"params\":null}")]
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
		using JsonRpc client = new(channel);
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
