// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using PolyType;

/// <summary>
/// Tests that events raised on RPC target objects are relayed to the remote party as JSON-RPC notifications,
/// per <see cref="JsonRpcTargetOptions.NotifyClientOfEvents"/> and <see cref="JsonRpcTargetOptions.EventNameTransform"/>.
/// </summary>
public partial class EventNotificationTests : TestBase
{
	internal delegate void PairEventHandler(int first, int second);

	internal delegate Task AsyncEventHandler(int value);

	internal delegate void LookalikeEventHandler(object? sender, int value);

	[Test]
	public async Task EndToEnd_RaisingEventInvokesRemotePartysMatchingMethods()
	{
		(IDuplexPipe serverPipe, IDuplexPipe clientPipe) = FullDuplexStream.CreatePipePair();

		// The server hosts the event source and raises events as ordinary CLR event invocations.
		EventfulTarget eventSource = new();
		JsonRpc serverRpc = new(new JsonRpcMessagePackChannel(serverPipe, NullLogger.Instance));
		serverRpc.AddRpcTarget(eventSource);
		serverRpc.Start();

		// The client hosts an ordinary RPC target whose method names/signatures match the notifications
		// the server will send, and it's registered the same way any other RPC target would be.
		EventReceiver receiver = new();
		JsonRpc clientRpc = new(new JsonRpcMessagePackChannel(clientPipe, NullLogger.Instance));
		clientRpc.AddRpcTarget(receiver);
		clientRpc.Start();

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

		eventSource.RaiseValueChanged(42);
		Assert.Equal(42, await receiver.ValueChangedInvocation.Task.WithCancellation(cts.Token));

		eventSource.RaisePairRaised(3, 4);
		(int First, int Second) pair = await receiver.PairRaisedInvocation.Task.WithCancellation(cts.Token);
		Assert.Equal((3, 4), pair);
	}

	[Test]
	public async Task ClassicEventPattern_ExcludesSenderAndForwardsArgs()
	{
		(Channel<JsonRpcMessage> remote, Channel<JsonRpcMessage> serverChannel) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc server = new(new MockJsonRpcPipeChannel(serverChannel));
		EventfulTarget target = new();
		server.AddRpcTarget(target, new JsonRpcTargetOptions());
		server.Start();

		target.RaiseValueChanged(42);

		JsonRpcRequest notification = Assert.IsType<JsonRpcRequest>(await remote.Reader.ReadAsync(this.TimeoutToken));
		Assert.Null(notification.Id);
		Assert.Equal("valueChanged", notification.Method);

		MessagePackReader reader = new(notification.Arguments.AsMessagePack());
		Assert.Equal(1, reader.ReadArrayHeader());
		Assert.Equal(42, reader.ReadInt32());
		Assert.True(reader.End);
	}

	[Test]
	public async Task CustomDelegate_ForwardsAllParametersPositionally()
	{
		(Channel<JsonRpcMessage> remote, Channel<JsonRpcMessage> serverChannel) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc server = new(new MockJsonRpcPipeChannel(serverChannel));
		EventfulTarget target = new();
		server.AddRpcTarget(target, new JsonRpcTargetOptions());
		server.Start();

		target.RaisePairRaised(3, 4);

		JsonRpcRequest notification = Assert.IsType<JsonRpcRequest>(await remote.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal("pairRaised", notification.Method);

		MessagePackReader reader = new(notification.Arguments.AsMessagePack());
		Assert.Equal(2, reader.ReadArrayHeader());
		Assert.Equal(3, reader.ReadInt32());
		Assert.Equal(4, reader.ReadInt32());
		Assert.True(reader.End);
	}

	[Test]
	public async Task NotifyClientOfEvents_False_SuppressesNotifications()
	{
		(Channel<JsonRpcMessage> remote, Channel<JsonRpcMessage> serverChannel) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc server = new(new MockJsonRpcPipeChannel(serverChannel));
		EventfulTarget target = new();
		server.AddRpcTarget(target, new JsonRpcTargetOptions { NotifyClientOfEvents = false });
		server.Start();

		target.RaiseValueChanged(42);

		// Confirm no notification arrives within the expected window.
		Task<JsonRpcMessage> readTask = remote.Reader.ReadAsync(this.TimeoutToken).AsTask();
		await Task.Delay(ExpectedTimeout, this.TimeoutToken);
		Assert.False(readTask.IsCompleted);
	}

	[Test]
	public async Task ExplicitEventShapeName_IsAuthoritative()
	{
		(Channel<JsonRpcMessage> remote, Channel<JsonRpcMessage> serverChannel) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc server = new(new MockJsonRpcPipeChannel(serverChannel));
		EventfulTarget target = new();

		// Even with a transform that would otherwise mangle the name, the explicit name must be used verbatim.
		server.AddRpcTarget(target, new JsonRpcTargetOptions { EventNameTransform = name => name.ToUpperInvariant() });
		server.Start();

		target.RaiseRenamedEvent(7);

		JsonRpcRequest notification = Assert.IsType<JsonRpcRequest>(await remote.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal("renamed", notification.Method);
	}

	[Test]
	public async Task EventNameTransform_CustomTransform_AppliesProvidedFunction()
	{
		(Channel<JsonRpcMessage> remote, Channel<JsonRpcMessage> serverChannel) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc server = new(new MockJsonRpcPipeChannel(serverChannel));
		EventfulTarget target = new();
		server.AddRpcTarget(target, new JsonRpcTargetOptions { EventNameTransform = name => $"evt.{name}" });
		server.Start();

		target.RaiseValueChanged(1);

		JsonRpcRequest notification = Assert.IsType<JsonRpcRequest>(await remote.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal("evt.ValueChanged", notification.Method);
	}

	[Test]
	public void Dispose_UnsubscribesEventHandlers()
	{
		(_, Channel<JsonRpcMessage> serverChannel) = MockChannel<JsonRpcMessage>.CreatePair();
		JsonRpc server = new(new MockJsonRpcPipeChannel(serverChannel));
		EventfulTarget target = new();
		server.AddRpcTarget(target, new JsonRpcTargetOptions());
		server.Start();

		server.Dispose();

		// Raising the event after disposal must not throw, and (since the handler was unsubscribed) does not attempt to notify.
		target.RaiseValueChanged(1);
	}

	[Test]
	public void AsyncEventHandler_ThrowsNotSupported()
	{
		using JsonRpc server = new(new MockJsonRpcPipeChannel(MockChannel<JsonRpcMessage>.CreatePair().Item1));
		Assert.Throws<NotSupportedException>(() => server.AddRpcTarget(new AsyncEventTarget(), new JsonRpcTargetOptions()));
	}

	[Test]
	public void StaticEvents_AreIgnoredRatherThanThrowing()
	{
		using JsonRpc server = new(new MockJsonRpcPipeChannel(MockChannel<JsonRpcMessage>.CreatePair().Item1));

		// Should not throw despite the static event: there's no single target instance to bind handler removal to.
		server.AddRpcTarget(new StaticEventTarget(), new JsonRpcTargetOptions());
	}

	[Test]
	public async Task LookalikeDelegate_DoesNotExcludeSender()
	{
		(Channel<JsonRpcMessage> remote, Channel<JsonRpcMessage> serverChannel) = MockChannel<JsonRpcMessage>.CreatePair();
		using JsonRpc server = new(new MockJsonRpcPipeChannel(serverChannel));
		EventfulTarget target = new();
		server.AddRpcTarget(target, new JsonRpcTargetOptions());
		server.Start();

		target.RaiseLookalikeRaised(target, 5);

		// Unlike EventHandler<T>, a custom delegate with the same (object? sender, T e) shape is not special-cased:
		// both parameters are forwarded.
		JsonRpcRequest notification = Assert.IsType<JsonRpcRequest>(await remote.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal("lookalikeRaised", notification.Method);

		MessagePackReader reader = new(notification.Arguments.AsMessagePack());
		Assert.Equal(2, reader.ReadArrayHeader());
	}

	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class EventfulTarget
	{
		public event EventHandler<int>? ValueChanged;

		public event PairEventHandler? PairRaised;

		public event LookalikeEventHandler? LookalikeRaised;

		[EventShape(Name = "renamed")]
		public event EventHandler<int>? RenamedEvent;

		public void RaiseValueChanged(int value) => this.ValueChanged?.Invoke(this, value);

		public void RaisePairRaised(int first, int second) => this.PairRaised?.Invoke(first, second);

		public void RaiseLookalikeRaised(object? sender, int value) => this.LookalikeRaised?.Invoke(sender, value);

		public void RaiseRenamedEvent(int value) => this.RenamedEvent?.Invoke(this, value);
	}

	/// <summary>
	/// An ordinary RPC target representing the remote party's side of the conversation: it defines methods whose
	/// names and parameter shapes match <see cref="EventfulTarget"/>'s notifications, exactly as a consumer would
	/// write them, and is registered via <see cref="JsonRpc.AddRpcTarget{T}(T, ITypeShape{T}, JsonRpcTargetOptions)"/> (or its
	/// <c>IShapeable&lt;T&gt;</c>-based overload, on runtimes that support it) like any other RPC target.
	/// </summary>
	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class EventReceiver
	{
		public TaskCompletionSource<int> ValueChangedInvocation { get; } = new();

		public TaskCompletionSource<(int First, int Second)> PairRaisedInvocation { get; } = new();

		public void ValueChanged(int value) => this.ValueChangedInvocation.TrySetResult(value);

		public void PairRaised(int first, int second) => this.PairRaisedInvocation.TrySetResult((first, second));
	}

	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class AsyncEventTarget
	{
#pragma warning disable CS0067 // this event exists only to exercise unsupported-handler-shape detection during target registration; it is never raised.
		public event AsyncEventHandler? AsyncEvent;
#pragma warning restore CS0067
	}

	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class StaticEventTarget
	{
#pragma warning disable CS0067 // this event exists only to exercise static-event handling during target registration; it is never raised.
		public static event EventHandler<int>? StaticEvent;
#pragma warning restore CS0067
	}
}
