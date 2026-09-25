// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using PolyType;

/// <summary>
/// Tests for <see cref="JoinableTask"/> token propagation, which is wire compatible with StreamJsonRpc,
/// and for the top-level envelope property mechanism that carries it.
/// </summary>
public partial class JoinableTaskTokenTests : TestBase
{
	private const string TokenPropertyName = "joinableTaskToken";

	private static readonly ITypeShape<int> IntShape = PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32;

	public enum WireEncoding
	{
		Json,
		MessagePack,
	}

	[Test]
	[Arguments(WireEncoding.Json)]
	[Arguments(WireEncoding.MessagePack)]
	public async Task RequestCarriesTokenOnlyWithinJoinableTask(WireEncoding encoding)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel channel = CreateChannel(local, encoding);
		JoinableTaskContext context = CreateJoinableTaskContext();
		using JsonRpc client = new(channel) { JoinableTaskFactory = context.Factory };
		client.Start();

		_ = client.RequestAsync("outside", EmptyParams(encoding), this.TimeoutToken).AsTask();
		using (JsonDocument outside = await ReadMessageAsync(peer.Input, encoding, this.TimeoutToken))
		{
			Assert.False(outside.RootElement.TryGetProperty(TokenPropertyName, out _));
		}

		string? expectedToken = null;
		await context.Factory.RunAsync(async delegate
		{
			expectedToken = context.Capture();
			_ = client.RequestAsync("inside", EmptyParams(encoding), this.TimeoutToken).AsTask();
			await client.NotifyAsync("notification", EmptyParams(encoding), this.TimeoutToken);
		});

		using (JsonDocument inside = await ReadMessageAsync(peer.Input, encoding, this.TimeoutToken))
		{
			Assert.Equal("inside", inside.RootElement.GetProperty("method").GetString());
			Assert.NotNull(expectedToken);
			Assert.Equal(expectedToken, inside.RootElement.GetProperty(TokenPropertyName).GetString());
		}

		using (JsonDocument notification = await ReadMessageAsync(peer.Input, encoding, this.TimeoutToken))
		{
			Assert.Equal("notification", notification.RootElement.GetProperty("method").GetString());
			Assert.False(notification.RootElement.TryGetProperty(TokenPropertyName, out _));
		}
	}

	[Test]
	[Arguments(WireEncoding.Json)]
	[Arguments(WireEncoding.MessagePack)]
	public async Task NoTokenWithoutConfiguration(WireEncoding encoding)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel channel = CreateChannel(local, encoding);
		using JsonRpc client = new(channel);
		client.Start();

		JoinableTaskContext unrelated = CreateJoinableTaskContext();
		await unrelated.Factory.RunAsync(delegate
		{
			_ = client.RequestAsync("method", EmptyParams(encoding), this.TimeoutToken).AsTask();
			return Task.CompletedTask;
		});
		using JsonDocument request = await ReadMessageAsync(peer.Input, encoding, this.TimeoutToken);
		Assert.False(request.RootElement.TryGetProperty(TokenPropertyName, out _));
	}

	[Test]
	public async Task BatchRequestsCarryToken()
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel channel = CreateChannel(local, WireEncoding.Json);
		JoinableTaskContext context = CreateJoinableTaskContext();
		using JsonRpc client = new(channel) { JoinableTaskFactory = context.Factory };
		client.Start();

		string? expectedToken = null;
		await context.Factory.RunAsync(async delegate
		{
			expectedToken = context.Capture();
			JsonRpcBatch batch = client.CreateBatch();
			_ = batch.RequestAsync("first", EmptyParams(WireEncoding.Json), this.TimeoutToken).AsTask();
			await batch.NotifyAsync("second", EmptyParams(WireEncoding.Json), this.TimeoutToken);
			await batch.SendAsync(this.TimeoutToken);
		});

		using JsonDocument payload = await ReadMessageAsync(peer.Input, WireEncoding.Json, this.TimeoutToken);
		JsonElement[] entries = [.. payload.RootElement.EnumerateArray()];
		Assert.Equal(2, entries.Length);
		Assert.Equal(expectedToken, entries[0].GetProperty(TokenPropertyName).GetString());
		Assert.False(entries[1].TryGetProperty(TokenPropertyName, out _));
	}

	[Test]
	public async Task ConfigurationIsLockedAfterStart()
	{
		(IDuplexPipe local, _) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel channel = CreateChannel(local, WireEncoding.Json);
		using JsonRpc rpc = new(channel);
		JoinableTaskTokenTracker tracker = new();
		rpc.JoinableTaskTracker = tracker;
		Assert.Same(tracker, rpc.JoinableTaskTracker);
		Assert.Throws<ArgumentNullException>(() => rpc.JoinableTaskTracker = null!);
		rpc.Start();
		Assert.Throws<InvalidOperationException>(() => rpc.JoinableTaskFactory = CreateJoinableTaskContext().Factory);
		Assert.Throws<InvalidOperationException>(() => rpc.JoinableTaskTracker = new());
	}

	/// <summary>
	/// Simulates two processes: A has a main thread and a <see cref="JoinableTaskFactory"/>; B has neither.
	/// A blocks its main thread on a request to B, which calls back into A with a request that needs A's main thread.
	/// </summary>
	[Test]
	public async Task CallbackThatRequiresMainThreadDoesNotDeadlock()
	{
		Assert.Equal(42, await this.RunMainThreadCallbackScenarioAsync(configureJoinableTaskFactory: true, this.TimeoutToken));
	}

	/// <summary>Verifies that <see cref="CallbackThatRequiresMainThreadDoesNotDeadlock"/> would deadlock without the feature.</summary>
	[Test]
	public async Task CallbackThatRequiresMainThreadDeadlocksWithoutJoinableTaskFactory()
	{
		using CancellationTokenSource cts = new(ExpectedTimeout);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this.RunMainThreadCallbackScenarioAsync(configureJoinableTaskFactory: false, cts.Token));
	}

	/// <summary>
	/// Verifies that a process without a <see cref="JoinableTaskFactory"/> forwards the token from an inbound request
	/// to the outbound requests it makes while servicing it, isolated between concurrent requests.
	/// </summary>
	/// <param name="isolatedTracker">Whether the inbound connection uses a private <see cref="JoinableTaskTokenTracker"/>.</param>
	/// <param name="shareTracker">Whether the outbound connection shares the inbound connection's tracker.</param>
	[Test]
	[Arguments(false, true)]
	[Arguments(true, false)]
	[Arguments(true, true)]
	public async Task TokenIsForwardedThroughIntermediary(bool isolatedTracker, bool shareTracker)
	{
		(IDuplexPipe upstreamLocal, IDuplexPipe upstreamPeer) = FullDuplexStream.CreatePipePair();
		(IDuplexPipe downstreamLocal, IDuplexPipe downstreamPeer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel upstreamChannel = CreateChannel(upstreamLocal, WireEncoding.Json);
		await using JsonRpcPipeChannel downstreamChannel = CreateChannel(downstreamLocal, WireEncoding.Json);
		JoinableTaskTokenTracker? tracker = isolatedTracker ? new() : null;
		using JsonRpc downstream = new(downstreamChannel);
		using JsonRpc upstream = new(upstreamChannel);
		if (tracker is not null)
		{
			upstream.JoinableTaskTracker = tracker;
			if (shareTracker)
			{
				downstream.JoinableTaskTracker = tracker;
			}
		}

		Intermediary intermediary = new(downstream, expectedConcurrency: 3);
		upstream.AddRpcTarget(intermediary);
		downstream.Start();
		upstream.Start();

		await WriteJsonAsync(upstreamPeer.Output, """{"jsonrpc":"2.0","id":1,"method":"ForwardAsync","params":["a"],"joinableTaskToken":"token-a"}""", this.TimeoutToken);
		await WriteJsonAsync(upstreamPeer.Output, """{"jsonrpc":"2.0","id":2,"method":"ForwardAsync","params":["b"]}""", this.TimeoutToken);
		await WriteJsonAsync(upstreamPeer.Output, """{"jsonrpc":"2.0","id":3,"method":"ForwardAsync","params":["c"],"joinableTaskToken":"token-c"}""", this.TimeoutToken);

		Dictionary<string, string?> forwarded = [];
		for (int i = 0; i < 3; i++)
		{
			using JsonDocument leaf = await ReadMessageAsync(downstreamPeer.Input, WireEncoding.Json, this.TimeoutToken);
			string tag = leaf.RootElement.GetProperty("params")[0].GetString()!;
			forwarded[tag] = leaf.RootElement.TryGetProperty(TokenPropertyName, out JsonElement token) ? token.GetString() : null;
			await WriteJsonAsync(downstreamPeer.Output, $$"""{"jsonrpc":"2.0","id":{{leaf.RootElement.GetProperty("id").GetRawText()}},"result":{{i}}}""", this.TimeoutToken);
		}

		bool expectForwarding = !isolatedTracker || shareTracker;
		Assert.Equal(expectForwarding ? "token-a" : null, forwarded["a"]);
		Assert.Null(forwarded["b"]);
		Assert.Equal(expectForwarding ? "token-c" : null, forwarded["c"]);

		for (int i = 0; i < 3; i++)
		{
			using JsonDocument response = await ReadMessageAsync(upstreamPeer.Input, WireEncoding.Json, this.TimeoutToken);
			Assert.True(response.RootElement.TryGetProperty("result", out _));
			Assert.False(response.RootElement.TryGetProperty(TokenPropertyName, out _));
		}
	}

	[Test]
	public async Task JsonTopLevelPrimitivePropertiesAreRetainedAndRelayed()
	{
		string[] inputs =
		[
			"""{"jsonrpc":"2.0","method":"m","id":1,"joinableTaskToken":"tok","traceparent":"00-abc","count":5,"neg":-3,"obj":{"a":1},"arr":[1],"big":18446744073709551615,"float":1.5,"bool":true,"nothing":null}""",
			"""{"jsonrpc":"2.0","result":1,"id":1,"traceparent":"00-abc","count":5,"neg":-3,"obj":{"a":1},"arr":[1],"big":18446744073709551615,"float":1.5,"bool":true,"nothing":null}""",
			"""{"jsonrpc":"2.0","error":{"code":1,"message":"m"},"id":1,"traceparent":"00-abc","count":5,"neg":-3,"obj":{"a":1},"arr":[1],"big":18446744073709551615,"float":1.5,"bool":true,"nothing":null}""",
			"""[{"jsonrpc":"2.0","method":"m","id":1,"traceparent":"00-abc","count":5,"neg":-3,"obj":{"a":1},"arr":[1],"big":18446744073709551615,"float":1.5,"bool":true,"nothing":null},{"jsonrpc":"2.0","method":"n","joinableTaskToken":null}]""",
		];

		foreach (string input in inputs)
		{
			using JsonDocument output = await this.RelayAsync(WireEncoding.Json, System.Text.Encoding.UTF8.GetBytes(input + "\n"));
			JsonElement first = output.RootElement.ValueKind == JsonValueKind.Array ? output.RootElement[0] : output.RootElement;
			AssertRetainedPrimitives(first);
			if (output.RootElement.ValueKind == JsonValueKind.Array)
			{
				Assert.False(output.RootElement[1].TryGetProperty(TokenPropertyName, out _));
			}
		}
	}

	[Test]
	public async Task MessagePackTopLevelPrimitivePropertiesAreRetainedAndRelayed()
	{
		Sequence<byte> input = new();
		MessagePackWriter writer = new(input);
		writer.WriteMapHeader(12);
		writer.Write("jsonrpc");
		writer.Write("2.0");
		writer.Write("method");
		writer.Write("m");
		writer.Write("id");
		writer.Write(1);
		writer.Write(TokenPropertyName);
		writer.Write("tok");
		writer.Write("traceparent");
		writer.Write("00-abc");
		writer.Write("count");
		writer.Write(5);
		writer.Write("neg");
		writer.Write(-3);
		writer.Write("obj");
		writer.WriteMapHeader(1);
		writer.Write("a");
		writer.Write(1);
		writer.Write("big");
		writer.Write(ulong.MaxValue);
		writer.Write("float");
		writer.Write(1.5);
		writer.Write("bool");
		writer.Write(true);
		writer.Write("nothing");
		writer.WriteNil();
		writer.Flush();

		using JsonDocument output = await this.RelayAsync(WireEncoding.MessagePack, input.AsReadOnlySequence.ToArray());
		AssertRetainedPrimitives(output.RootElement);
		Assert.Equal("tok", output.RootElement.GetProperty(TokenPropertyName).GetString());
	}

	[Test]
	[Arguments("""{"jsonrpc":"2.0","method":"m","id":1,"joinableTaskToken":5}""")]
	[Arguments("""{"jsonrpc":"2.0","method":"m","id":1,"joinableTaskToken":{}}""")]
	[Arguments("""{"jsonrpc":"2.0","method":"m","id":1,"joinableTaskToken":"a","joinableTaskToken":"b"}""")]
	[Arguments("""{"jsonrpc":"2.0","method":"m","id":1,"count":1,"count":2}""")]
	public async Task InvalidTopLevelPropertiesFaultJsonTransport(string json)
	{
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel channel = CreateChannel(local, WireEncoding.Json);
		await WriteJsonAsync(peer.Output, json, this.TimeoutToken);
		await Assert.ThrowsAnyAsync<Exception>(() => channel.Reader.Completion.WithCancellation(this.TimeoutToken));
	}

	[Test]
	[Arguments(0)]
	[Arguments(1)]
	[Arguments(2)]
	public async Task InvalidTopLevelPropertiesFaultMessagePackTransport(int caseNumber)
	{
		Sequence<byte> input = new();
		MessagePackWriter writer = new(input);
		writer.WriteMapHeader(5);
		writer.Write("jsonrpc");
		writer.Write("2.0");
		writer.Write("method");
		writer.Write("m");
		writer.Write("id");
		writer.Write(1);
		switch (caseNumber)
		{
			case 0:
				writer.Write(TokenPropertyName);
				writer.Write(5);
				writer.Write("other");
				writer.Write(1);
				break;
			case 1:
				writer.Write(TokenPropertyName);
				writer.WriteArrayHeader(0);
				writer.Write("other");
				writer.Write(1);
				break;
			default:
				writer.Write("count");
				writer.Write(1);
				writer.Write("count");
				writer.Write(2);
				break;
		}

		writer.Flush();
		(IDuplexPipe local, IDuplexPipe peer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel channel = CreateChannel(local, WireEncoding.MessagePack);
		await peer.Output.WriteAsync(input.AsReadOnlySequence.ToArray(), this.TimeoutToken);
		await Assert.ThrowsAnyAsync<Exception>(() => channel.Reader.Completion.WithCancellation(this.TimeoutToken));
	}

	/// <summary>Creates a context with a main thread, without which <see cref="JoinableTaskContext.Capture"/> produces no tokens.</summary>
	private static JoinableTaskContext CreateJoinableTaskContext() => new(Thread.CurrentThread, new SynchronizationContext());

	private static void AssertRetainedPrimitives(JsonElement message)
	{
		Assert.Equal("00-abc", message.GetProperty("traceparent").GetString());
		Assert.Equal(5, message.GetProperty("count").GetInt64());
		Assert.Equal(-3, message.GetProperty("neg").GetInt64());
		foreach (string dropped in new[] { "obj", "arr", "big", "float", "bool", "nothing" })
		{
			Assert.False(message.TryGetProperty(dropped, out _), $"Unexpected property '{dropped}' was relayed.");
		}
	}

	private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, WireEncoding encoding) => encoding switch
	{
		WireEncoding.Json => new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited, LoggerFactory.CreateLogger("test")),
		_ => new JsonRpcMessagePackChannel(pipe, LoggerFactory.CreateLogger("test")),
	};

	private static JsonRpcValue EmptyParams(WireEncoding encoding) => encoding == WireEncoding.Json ? JsonRpcValue.FromJson("[]"u8.ToArray()) : JsonRpcValue.FromMessagePack(EmptyParamsMsgPack);

	private static async Task WriteJsonAsync(PipeWriter writer, string json, CancellationToken cancellationToken)
	{
		await writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json + "\n"), cancellationToken);
	}

	/// <summary>Reads one raw message from the wire, and presents it as JSON.</summary>
	private static async Task<JsonDocument> ReadMessageAsync(PipeReader reader, WireEncoding encoding, CancellationToken cancellationToken)
	{
		while (true)
		{
			ReadResult result = await reader.ReadAsync(cancellationToken);
			ReadOnlySequence<byte> buffer = result.Buffer;
			if (encoding == WireEncoding.Json)
			{
				if (buffer.PositionOf((byte)'\n') is SequencePosition newline)
				{
					byte[] line = buffer.Slice(0, newline).ToArray();
					reader.AdvanceTo(buffer.GetPosition(1, newline));
					return JsonDocument.Parse(line);
				}
			}
			else if (TryReadMessagePackStructure(buffer, out ReadOnlySequence<byte> structure))
			{
				string json = new MessagePackSerializer().ConvertToJson(structure);
				reader.AdvanceTo(structure.End);
				return JsonDocument.Parse(json);
			}

			if (result.IsCompleted)
			{
				throw new EndOfStreamException();
			}

			reader.AdvanceTo(buffer.Start, buffer.End);
		}
	}

	private static bool TryReadMessagePackStructure(ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> structure)
	{
		MessagePackReader reader = new(buffer);
		try
		{
			reader.Skip(new SerializationContext());
		}
		catch (EndOfStreamException)
		{
			structure = default;
			return false;
		}

		structure = buffer.Slice(0, reader.Position);
		return true;
	}

	private async Task<JsonDocument> RelayAsync(WireEncoding encoding, byte[] input)
	{
		(IDuplexPipe inLocal, IDuplexPipe inPeer) = FullDuplexStream.CreatePipePair();
		(IDuplexPipe outLocal, IDuplexPipe outPeer) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel inbound = CreateChannel(inLocal, encoding);
		await using JsonRpcPipeChannel outbound = CreateChannel(outLocal, encoding);
		await inPeer.Output.WriteAsync(input, this.TimeoutToken);
		JsonRpcMessage message = await inbound.Reader.ReadAsync(this.TimeoutToken);

		// Complete the raw peer so the channel's inbound loop can end before it is disposed.
		await inPeer.Output.CompleteAsync();
		await outbound.Writer.WriteAsync(message, this.TimeoutToken);
		return await ReadMessageAsync(outPeer.Input, encoding, this.TimeoutToken);
	}

	private async Task<int> RunMainThreadCallbackScenarioAsync(bool configureJoinableTaskFactory, CancellationToken cancellationToken)
	{
		(IDuplexPipe aPipe, IDuplexPipe bPipe) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel aChannel = CreateChannel(aPipe, WireEncoding.MessagePack);
		await using JsonRpcPipeChannel bChannel = CreateChannel(bPipe, WireEncoding.MessagePack);
		using JsonRpc b = new(bChannel);
		b.AddRpcTarget(new ProcessB(b));
		b.Start();

		TaskCompletionSource<int> outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Thread mainThread = new(() =>
		{
			try
			{
				SynchronizationContext.SetSynchronizationContext(new SingleThreadedSynchronizationContext());
				JoinableTaskContext context = new();
				using JsonRpc a = new(aChannel);
				if (configureJoinableTaskFactory)
				{
					a.JoinableTaskFactory = context.Factory;
				}

				a.AddRpcTarget(new ProcessA(context));
				a.Start();

				// Block the main thread until the round trip completes, as a synchronous caller would.
				int result = context.Factory.Run(() => a.RequestAsync("CallBackAsync", JsonRpcValue.FromMessagePack(EmptyParamsMsgPack), IntShape, cancellationToken).AsTask().WithCancellation(cancellationToken));
				outcome.SetResult(result);
			}
			catch (Exception ex)
			{
				outcome.SetException(ex);
			}
		});
		mainThread.IsBackground = true;
		mainThread.Start();

		return await outcome.Task.WithCancellation(this.TimeoutToken);
	}

	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class ProcessA(JoinableTaskContext context)
	{
		public async Task<int> OnMainThreadAsync(CancellationToken cancellationToken)
		{
			await context.Factory.SwitchToMainThreadAsync(cancellationToken);
			Assert.True(context.IsOnMainThread);
			return 42;
		}
	}

	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class ProcessB(JsonRpc rpc)
	{
		public async Task<int> CallBackAsync(CancellationToken cancellationToken)
			=> await rpc.RequestAsync("OnMainThreadAsync", JsonRpcValue.FromMessagePack(EmptyParamsMsgPack), IntShape, cancellationToken);
	}

	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class Intermediary(JsonRpc downstream, int expectedConcurrency)
	{
		private readonly object syncObject = new();
		private readonly TaskCompletionSource<bool> allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int started;

		public async Task<int> ForwardAsync(string tag, CancellationToken cancellationToken)
		{
			lock (this.syncObject)
			{
				this.started++;
				this.CheckAllStarted();
			}

			// Ensure all requests are in flight concurrently so that token isolation between them is exercised.
			await this.allStarted.Task.WithCancellation(cancellationToken);
			byte[] args = System.Text.Encoding.UTF8.GetBytes($"[\"{tag}\"]");
			return await downstream.RequestAsync("Leaf", JsonRpcValue.FromJson(args), IntShape, cancellationToken);
		}

		private void CheckAllStarted()
		{
			if (this.started >= expectedConcurrency)
			{
				this.allStarted.TrySetResult(true);
			}
		}
	}
}
