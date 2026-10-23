// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;

public class GeneratedProxyEventTests : TestBase
{
	[Test]
	[Arguments(JsonRpcEncoding.Json, false)]
	[Arguments(JsonRpcEncoding.Json, true)]
	[Arguments(JsonRpcEncoding.MessagePack, false)]
	[Arguments(JsonRpcEncoding.MessagePack, true)]
	public async Task EventsAreReadyBeforeStart(JsonRpcEncoding encoding, bool batch)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding)) { Logger = LoggerFactory.CreateLogger("client") };
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding)) { Logger = LoggerFactory.CreateLogger("server") };
		ProxyEventSource source = new();
		serverRpc.AddRpcTarget<IProxyEvents>(source);
		IProxyEvents proxy = batch ? clientRpc.CreateBatch().Attach<IProxyEvents>() : clientRpc.Attach<IProxyEvents>();
		TaskCompletionSource<(object? Sender, int Value)> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<object?> empty = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<(int First, string Second)> pair = new(TaskCreationOptions.RunContinuationsAsynchronously);
		proxy.Changed += (sender, value) => received.SetResult((sender, value));
		proxy.Empty += (sender, args) => empty.SetResult(sender);
		proxy.Pulse += () => pulse.SetResult(true);
		proxy.Pair += (first, second) => pair.SetResult((first, second));
		serverRpc.Start();
		source.Raise(42);
		clientRpc.Start();
		Assert.Equal((proxy, 42), await received.Task.WithCancellation(this.TimeoutToken));
		Assert.Same(proxy, await empty.Task.WithCancellation(this.TimeoutToken));
		Assert.True(await pulse.Task.WithCancellation(this.TimeoutToken));
		Assert.Equal((42, "value"), await pair.Task.WithCancellation(this.TimeoutToken));
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task HandlersCanBeAddedAndRemovedAfterStart(bool named)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(new JsonRpcJsonChannel(clientPipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited));
		using JsonRpc serverRpc = new(new JsonRpcJsonChannel(serverPipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited));
		IProxyEvents proxy = (IProxyEvents)clientRpc.Attach(typeof(IProxyEvents));
		clientRpc.Start();
		serverRpc.Start();
		int count = 0;
		EventHandler<int> handler = (sender, value) => count += value;
		await SendChangedAsync(1);
		Assert.Equal(0, count);
		proxy.Changed += handler;
		proxy.Changed += handler;
		await SendChangedAsync(2);
		Assert.Equal(4, count);
		proxy.Changed -= handler;
		await SendChangedAsync(3);
		Assert.Equal(7, count);
		proxy.Changed -= handler;
		await SendChangedAsync(4);
		Assert.Equal(7, count);

		ValueTask SendChangedAsync(int value)
		{
			using JsonRpcArgumentsBuilder builder = serverRpc.CreateArguments(named, 1, this.TimeoutToken);
			builder.Add("e", value, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
			return serverRpc.RequestAsync("changed", builder.Build(), this.TimeoutToken);
		}
	}

	[Test]
	public void DuplicateRegistrationIsAtomic()
	{
		(IDuplexPipe clientPipe, _) = FullDuplexStream.CreatePipePair();
		using JsonRpc rpc = new(new JsonRpcJsonChannel(clientPipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited));
		Assert.Throws<InvalidOperationException>(() => rpc.Attach<IProxyEvents>(new JsonRpcProxyOptions { EventNameTransform = _ => "same" }));
		Assert.NotNull(rpc.Attach<IProxyEvents>());
		Assert.Throws<InvalidOperationException>(() => rpc.Attach<IProxyEvents>());
	}

	[Test]
	[Arguments(null)]
	[Arguments("")]
	public void InvalidEventTransformIsRejected(string? wireName)
	{
		(IDuplexPipe pipe, _) = FullDuplexStream.CreatePipePair();
		using JsonRpc rpc = new(CreateChannel(pipe, JsonRpcEncoding.Json));
		Assert.Throws<InvalidOperationException>(() => rpc.Attach<IProxyEvents>(new JsonRpcProxyOptions { EventNameTransform = _ => wireName! }));
		Assert.NotNull(rpc.Attach<IProxyEvents>());
		Assert.Throws<ArgumentNullException>(() => new JsonRpcProxyOptions { EventNameTransform = null! });
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task EventArgumentAndHandlerFailuresUseNormalDispatchErrors(JsonRpcEncoding encoding)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding)) { Logger = LoggerFactory.CreateLogger("client") };
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding)) { Logger = LoggerFactory.CreateLogger("server") };
		IProxyEvents proxy = clientRpc.Attach<IProxyEvents>();
		EventHandler<int> handler = (sender, value) => throw new InvalidOperationException("handler failure");
		proxy.Changed += handler;
		clientRpc.Start();
		serverRpc.Start();
		RemoteInvocationException invalidArguments = await Assert.ThrowsAsync<RemoteInvocationException>(() => SendAsync(includeArgument: false).AsTask());
		Assert.Equal(JsonRpcErrorCode.InvalidParams, invalidArguments.ErrorDetails.Code);
		RemoteInvocationException failedHandler = await Assert.ThrowsAsync<RemoteInvocationException>(() => SendAsync(includeArgument: true).AsTask());
		Assert.Equal(JsonRpcErrorCode.InternalError, failedHandler.ErrorDetails.Code);
		proxy.Changed -= handler;
		await SendAsync(includeArgument: true);

		ValueTask SendAsync(bool includeArgument)
		{
			using JsonRpcArgumentsBuilder builder = serverRpc.CreateArguments(named: false, count: includeArgument ? 1 : 0, this.TimeoutToken);
			if (includeArgument)
			{
				builder.Add("e", 42, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
			}

			return serverRpc.RequestAsync("changed", builder.Build(), this.TimeoutToken);
		}
	}

	[Test]
	public async Task CustomDelegateWithSenderShapeKeepsBothParameters()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, JsonRpcEncoding.MessagePack));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, JsonRpcEncoding.MessagePack));
		ProxyEventSource source = new();
		serverRpc.AddRpcTarget<IProxyEvents>(source);
		IProxyEvents proxy = clientRpc.Attach<IProxyEvents>();
		TaskCompletionSource<(object? Sender, int Value)> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
		proxy.Lookalike += (sender, value) => received.SetResult((sender, value));
		clientRpc.Start();
		serverRpc.Start();
		source.RaiseLookalike(42);
		Assert.Equal((null, 42), await received.Task.WithCancellation(this.TimeoutToken));
	}

	[Test]
	public async Task CachedMetadataDoesNotShareHandlersNamesOrSerializers()
	{
		await Task.WhenAll(RunAsync(JsonRpcEncoding.Json, "first."), RunAsync(JsonRpcEncoding.MessagePack, "second."));

		async Task RunAsync(JsonRpcEncoding encoding, string prefix)
		{
			(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
			using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
			using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
			IProxyEvents proxy = clientRpc.Attach<IProxyEvents>(new JsonRpcProxyOptions { EventNameTransform = name => prefix + name });
			int received = 0;
			proxy.Changed += (sender, value) =>
			{
				Assert.Same(proxy, sender);
				received += value;
			};
			clientRpc.Start();
			serverRpc.Start();
			JsonRpcValue arguments;
			using (JsonRpcArgumentsBuilder builder = serverRpc.CreateArguments(named: true, count: 1, this.TimeoutToken))
			{
				builder.Add("e", 42, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
				arguments = builder.Build();
			}

			await serverRpc.RequestAsync(prefix + "Changed", arguments, this.TimeoutToken);
			Assert.Equal(42, received);
		}
	}

	private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding)
		=> encoding == JsonRpcEncoding.Json ? new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited) : new JsonRpcMessagePackChannel(pipe);

	private sealed class ProxyEventSource : IProxyEvents
	{
		public event EventHandler<int>? Changed;

		public event EventHandler? Empty;

		public event Action? Pulse;

		public event Action<object?, int>? Lookalike;

		public event Action<int, string>? Pair;

		internal void RaiseLookalike(int value) => this.Lookalike?.Invoke(null, value);

		internal void Raise(int value)
		{
			this.Changed?.Invoke(this, value);
			this.Empty?.Invoke(this, EventArgs.Empty);
			this.Pulse?.Invoke();
			this.Pair?.Invoke(value, "value");
		}
	}
}
