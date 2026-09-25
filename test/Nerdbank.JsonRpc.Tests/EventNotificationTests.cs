// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Channels;
using PolyType;

/// <summary>
/// Tests that events raised on RPC target objects are relayed to the remote party as JSON-RPC notifications,
/// per <see cref="JsonRpcTargetOptions.NotifyClientOfEvents"/> and <see cref="JsonRpcTargetOptions.EventNameTransform"/>.
/// </summary>
public partial class EventNotificationTests : TestBase
{
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

	internal delegate void PairEventHandler(int first, int second);

	internal delegate Task AsyncEventHandler(int value);

	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class EventfulTarget
	{
		public event EventHandler<int>? ValueChanged;

		public event PairEventHandler? PairRaised;

		[EventShape(Name = "renamed")]
		public event EventHandler<int>? RenamedEvent;

		public void RaiseValueChanged(int value) => this.ValueChanged?.Invoke(this, value);

		public void RaisePairRaised(int first, int second) => this.PairRaised?.Invoke(first, second);

		public void RaiseRenamedEvent(int value) => this.RenamedEvent?.Invoke(this, value);
	}

	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class AsyncEventTarget
	{
		public event AsyncEventHandler? AsyncEvent;
	}

	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class StaticEventTarget
	{
		public static event EventHandler<int>? StaticEvent;
	}
}
