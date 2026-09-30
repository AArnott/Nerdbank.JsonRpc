// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using PolyType;

[Nerdbank.JsonRpc.RpcMarshalable]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IWireCounter : IDisposable
{
	Task<int> Add(int value, CancellationToken cancellationToken);
}

[Nerdbank.JsonRpc.RpcMarshalable(CallScopedLifetime = true)]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IWireCallScoped
{
	Task<int> Add(int value, CancellationToken cancellationToken);
}

[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IWireService
{
	Task Report(IProgress<int> progress, CancellationToken cancellationToken);

	Task<IWireCounter> GetCounter(CancellationToken cancellationToken);

	Task<int> UseCallScoped(IWireCallScoped counter, CancellationToken cancellationToken);

	Task<int> UseCounter(IWireCounter counter, CancellationToken cancellationToken);
}

/// <summary>
/// Pins the exact JSON-RPC wire shapes that Nerdbank.JsonRpc shares with StreamJsonRpc,
/// and verifies that the more lenient forms other peers may send are still accepted.
/// </summary>
public class WireCompatibilityTests : TestBase
{
	[Test]
	public async Task ProgressNotificationSendsNamedArguments()
	{
		using WirePeer peer = WirePeer.ConnectToNerdbankServer(new WireService());
		await peer.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"report","params":[5]}""");

		JsonElement notification = await peer.ReceiveAsync(this.TimeoutToken);
		Assert.Equal("$/progress", notification.GetProperty("method").GetString());
		JsonElement parameters = notification.GetProperty("params");
		Assert.Equal(JsonValueKind.Object, parameters.ValueKind);
		Assert.Equal(5, parameters.GetProperty("token").GetInt32());
		Assert.Equal(1, parameters.GetProperty("value").GetInt32());
	}

	[Test]
	public async Task ProgressNotificationAcceptsPositionalArguments()
	{
		using WirePeer peer = WirePeer.ConnectToNerdbankClient();
		RecordingProgress progress = new();
		Task request = peer.NerdbankClient.Report(progress, this.TimeoutToken);

		// StreamJsonRpc sends this form whenever the originating request used positional arguments.
		JsonElement call = await peer.ReceiveAsync(this.TimeoutToken);
		int token = call.GetProperty("params")[0].GetInt32();
		await peer.SendAsync($$$"""{"jsonrpc":"2.0","method":"$/progress","params":[{{{token}}},42]}""");
		await peer.SendAsync(WirePeer.Result(call, "null"));

		await request.WithCancellation(this.TimeoutToken);
		Assert.Equal(new[] { 42 }, await progress.WaitForAsync(1, this.TimeoutToken));
	}

	[Test]
	public async Task ProgressNotificationAcceptsNamedArguments()
	{
		using WirePeer peer = WirePeer.ConnectToNerdbankClient();
		RecordingProgress progress = new();
		Task request = peer.NerdbankClient.Report(progress, this.TimeoutToken);

		JsonElement call = await peer.ReceiveAsync(this.TimeoutToken);
		int token = call.GetProperty("params")[0].GetInt32();
		await peer.SendAsync($$$"""{"jsonrpc":"2.0","method":"$/progress","params":{"token":{{{token}}},"value":42}}""");
		await peer.SendAsync(WirePeer.Result(call, "null"));

		await request.WithCancellation(this.TimeoutToken);
		Assert.Equal(new[] { 42 }, await progress.WaitForAsync(1, this.TimeoutToken));
	}

	[Test]
	public async Task MarshaledObjectMarkerOmitsExplicitLifetime()
	{
		using WirePeer peer = WirePeer.ConnectToNerdbankServer(new WireService());
		await peer.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"getCounter","params":[]}""");

		JsonElement marker = (await peer.ReceiveAsync(this.TimeoutToken)).GetProperty("result");
		Assert.Equal(1, marker.GetProperty("__jsonrpc_marshaled").GetInt32());
		Assert.True(marker.TryGetProperty("handle", out _));

		// StreamJsonRpc treats a missing lifetime as "explicit", so omitting it keeps the payloads identical.
		Assert.False(marker.TryGetProperty("lifetime", out _));
	}

	[Test]
	public async Task MarshaledObjectMarkerDeclaresCallScopedLifetime()
	{
		using WirePeer peer = WirePeer.ConnectToNerdbankClient();
		Task<int> request = peer.NerdbankClient.UseCallScoped(new Counter(), this.TimeoutToken);

		JsonElement marker = (await peer.ReceiveAsync(this.TimeoutToken)).GetProperty("params")[0];
		Assert.Equal("call", marker.GetProperty("lifetime").GetString());

		await peer.SendAsync("""{"jsonrpc":"2.0","id":1,"result":0}""");
		await request.WithCancellation(this.TimeoutToken);
	}

	[Test]
	[Arguments("""{"__jsonrpc_marshaled":1,"handle":7}""")]
	[Arguments("""{"__jsonrpc_marshaled":1,"handle":7,"lifetime":"explicit"}""")]
	public async Task MarshaledObjectMarkerWithExplicitLifetimeIsAccepted(string marker)
	{
		WireService service = new();
		using WirePeer peer = WirePeer.ConnectToNerdbankServer(service);

		await peer.SendAsync($$"""{"jsonrpc":"2.0","id":1,"method":"useCounter","params":[{{marker}}]}""");

		JsonElement call = await peer.ReceiveAsync(this.TimeoutToken);
		Assert.Equal("$/invokeProxy/7/add", call.GetProperty("method").GetString());
		await peer.SendAsync($$"""{"jsonrpc":"2.0","id":{{call.GetProperty("id").GetInt32()}},"result":11}""");

		JsonElement response = await peer.ReceiveAsync(this.TimeoutToken);
		Assert.Equal(11, response.GetProperty("result").GetInt32());
	}

	[Test]
	public async Task ReleaseMarshaledObjectSendsNamedArguments()
	{
		using WirePeer peer = WirePeer.ConnectToNerdbankClient();
		Task<IWireCounter> request = peer.NerdbankClient.GetCounter(this.TimeoutToken);
		JsonElement call = await peer.ReceiveAsync(this.TimeoutToken);
		await peer.SendAsync(WirePeer.Result(call, """{"__jsonrpc_marshaled":1,"handle":3}"""));

		IWireCounter counter = await request.WithCancellation(this.TimeoutToken);
		counter.Dispose();

		JsonElement release = await peer.ReceiveAsync(this.TimeoutToken);
		Assert.Equal("$/releaseMarshaledObject", release.GetProperty("method").GetString());
		JsonElement parameters = release.GetProperty("params");
		Assert.Equal(JsonValueKind.Object, parameters.ValueKind);
		Assert.Equal(3, parameters.GetProperty("handle").GetInt32());
		Assert.False(parameters.GetProperty("ownedBySender").GetBoolean());
	}

	[Test]
	public async Task ReleaseMarshaledObjectAcceptsPositionalArguments()
	{
		WireService service = new();
		using WirePeer peer = WirePeer.ConnectToNerdbankServer(service);
		await peer.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"getCounter","params":[]}""");
		JsonElement marker = (await peer.ReceiveAsync(this.TimeoutToken)).GetProperty("result");
		int handle = marker.GetProperty("handle").GetInt32();

		await peer.SendAsync($$"""{"jsonrpc":"2.0","method":"$/releaseMarshaledObject","params":[{{handle}},false]}""");

		await service.Counter.Disposed.WithCancellation(this.TimeoutToken);
	}

	[Test]
	[Arguments(false, "add")]
	[Arguments(true, "Add")]
	public async Task MarshaledObjectUsesConfiguredTargetName(bool streamJsonRpcNaming, string methodName)
	{
		using WirePeer peer = WirePeer.ConnectToNerdbankServer(new WireService(), streamJsonRpcNaming);
		await peer.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"getCounter","params":[]}""");
		JsonElement marker = (await peer.ReceiveAsync(this.TimeoutToken)).GetProperty("result");
		int handle = marker.GetProperty("handle").GetInt32();

		await peer.SendAsync($$"""{"jsonrpc":"2.0","id":2,"method":"$/invokeProxy/{{handle}}/{{methodName}}","params":[4]}""");

		JsonElement response = await peer.ReceiveAsync(this.TimeoutToken);
		Assert.Equal(4, response.GetProperty("result").GetInt32());
	}

	[Test]
	[Arguments(false, "add")]
	[Arguments(true, "Add")]
	public async Task MarshaledProxyUsesConfiguredMethodName(bool streamJsonRpcNaming, string methodName)
	{
		using WirePeer peer = WirePeer.ConnectToNerdbankClient(streamJsonRpcNaming);
		Task<IWireCounter> request = peer.NerdbankClient.GetCounter(this.TimeoutToken);
		JsonElement call = await peer.ReceiveAsync(this.TimeoutToken);
		Assert.Equal("getCounter", call.GetProperty("method").GetString());
		await peer.SendAsync(WirePeer.Result(call, """{"__jsonrpc_marshaled":1,"handle":3}"""));

		IWireCounter counter = await request.WithCancellation(this.TimeoutToken);
		Task<int> add = counter.Add(2, this.TimeoutToken);

		JsonElement invocation = await peer.ReceiveAsync(this.TimeoutToken);
		Assert.Equal($"$/invokeProxy/3/{methodName}", invocation.GetProperty("method").GetString());
		await peer.SendAsync($$"""{"jsonrpc":"2.0","id":{{invocation.GetProperty("id").GetInt32()}},"result":2}""");
		Assert.Equal(2, await add.WithCancellation(this.TimeoutToken));
	}

	private sealed class Counter : IWireCounter, IWireCallScoped
	{
		private readonly TaskCompletionSource<bool> disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int value;

		internal Task Disposed => this.disposed.Task;

		public Task<int> Add(int amount, CancellationToken cancellationToken) => Task.FromResult(Interlocked.Add(ref this.value, amount));

		public void Dispose() => this.disposed.TrySetResult(true);
	}

	private sealed class WireService : IWireService
	{
		internal Counter Counter { get; } = new();

		public Task Report(IProgress<int> progress, CancellationToken cancellationToken)
		{
			progress.Report(1);
			return Task.CompletedTask;
		}

		public Task<IWireCounter> GetCounter(CancellationToken cancellationToken) => Task.FromResult<IWireCounter>(this.Counter);

		public Task<int> UseCallScoped(IWireCallScoped counter, CancellationToken cancellationToken) => counter.Add(1, cancellationToken);

		public Task<int> UseCounter(IWireCounter counter, CancellationToken cancellationToken) => counter.Add(1, cancellationToken);
	}

	private sealed class RecordingProgress : IProgress<int>
	{
		private readonly object sync = new();
		private readonly List<int> values = [];
		private TaskCompletionSource<bool> signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public void Report(int value)
		{
			lock (this.sync)
			{
				this.values.Add(value);
				this.signal.TrySetResult(true);
			}
		}

		internal async Task<int[]> WaitForAsync(int count, CancellationToken cancellationToken)
		{
			while (true)
			{
				Task signalTask;
				lock (this.sync)
				{
					if (this.values.Count >= count)
					{
						return [.. this.values];
					}

					if (this.signal.Task.IsCompleted)
					{
						this.signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
					}

					signalTask = this.signal.Task;
				}

				await signalTask.WithCancellation(cancellationToken);
			}
		}
	}

	/// <summary>A hand-written JSON-RPC peer that lets tests assert on and control raw wire bytes.</summary>
	private sealed class WirePeer : IDisposable
	{
		private readonly JsonRpc rpc;
		private readonly IDuplexPipe pipe;

		private WirePeer(JsonRpc rpc, IDuplexPipe pipe, IWireService? client)
		{
			this.rpc = rpc;
			this.pipe = pipe;
			this.NerdbankClient = client!;
		}

		internal IWireService NerdbankClient { get; }

		public void Dispose()
		{
			this.rpc.Dispose();
		}

		internal static WirePeer ConnectToNerdbankServer(IWireService target, bool streamJsonRpcNaming = false)
		{
			(IDuplexPipe peerPipe, IDuplexPipe rpcPipe) = FullDuplexStream.CreatePipePair();
			JsonRpc rpc = Create(rpcPipe, streamJsonRpcNaming, server: true);

			rpc.AddRpcTarget<IWireService>(target);
			rpc.Start();
			return new(rpc, peerPipe, null);
		}

		internal static WirePeer ConnectToNerdbankClient(bool streamJsonRpcNaming = false)
		{
			(IDuplexPipe peerPipe, IDuplexPipe rpcPipe) = FullDuplexStream.CreatePipePair();
			JsonRpc rpc = Create(rpcPipe, streamJsonRpcNaming, server: false);

			rpc.Start();
			return new(rpc, peerPipe, rpc.Attach<IWireService>());
		}

		/// <summary>Builds a JSON-RPC response to the given request with a raw JSON result.</summary>
		internal static string Result(JsonElement request, string resultJson)
			=> $$$"""{"jsonrpc":"2.0","id":{{{request.GetProperty("id").GetInt32()}}},"result":{{{resultJson}}}}""";

		internal async Task SendAsync(string json)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
			await this.pipe.Output.WriteAsync(bytes);
		}

		/// <summary>Reads the next newline-delimited JSON message sent by the Nerdbank.JsonRpc endpoint.</summary>
		internal async Task<JsonElement> ReceiveAsync(CancellationToken cancellationToken)
		{
			while (true)
			{
				ReadResult result = await this.pipe.Input.ReadAsync(cancellationToken);
				ReadOnlySequence<byte> buffer = result.Buffer;
				SequencePosition? newline = buffer.PositionOf((byte)'\n');
				if (newline is null)
				{
					this.pipe.Input.AdvanceTo(buffer.Start, buffer.End);
					if (result.IsCompleted)
					{
						throw new InvalidOperationException("The connection closed before a message arrived.");
					}

					continue;
				}

				byte[] line = BuffersExtensions.ToArray(buffer.Slice(0, newline.Value));
				this.pipe.Input.AdvanceTo(buffer.GetPosition(1, newline.Value));
				return JsonDocument.Parse(line).RootElement.Clone();
			}
		}

		private static JsonRpc Create(IDuplexPipe pipe, bool streamJsonRpcNaming, bool server)
		{
			JsonRpcPipeChannel channel = new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited);
			if (!streamJsonRpcNaming)
			{
				return new(channel);
			}

			return server
				? new(channel) { MarshaledTargetOptions = new() { MethodNameTransform = CommonMethodNameTransforms.Identity } }
				: new(channel) { MarshaledProxyOptions = new() { MethodNameTransform = CommonMethodNameTransforms.Identity } };
		}
	}
}
