// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.Tests;

public class MarshaledObjectManagerTests
{
	[Test]
	public async Task DisposeReleasesLocallyOwnedObjects()
	{
		MockJsonRpcPipeChannel channel = new(System.Threading.Channels.Channel.CreateUnbounded<JsonRpcMessage>());
		TrackingDisposable disposable = new();
		JsonRpc rpc = new(channel);
		_ = ((IJsonRpcClient)rpc).MarshalDisposable(disposable);

		rpc.Dispose();

		Assert.True(disposable.IsDisposed);
	}

	[Test]
	public async Task DisposingRemoteProxySendsReleaseNotification()
	{
		System.Threading.Channels.Channel<JsonRpcMessage> messages = System.Threading.Channels.Channel.CreateUnbounded<JsonRpcMessage>();
		MockJsonRpcPipeChannel channel = new(messages);
		using JsonRpc rpc = new(channel);
		JsonRpcValue marker = ((IJsonRpcClient)rpc).MarshalDisposable(new TrackingDisposable());

		rpc.UnmarshalDisposable(marker).Dispose();

		JsonRpcRequest release = Assert.IsType<JsonRpcRequest>(await messages.Reader.ReadAsync());
		Assert.Equal("$/releaseMarshaledObject", release.Method);
	}
	private sealed class TrackingDisposable : IDisposable
	{
		public bool IsDisposed { get; private set; }

		public void Dispose() => this.IsDisposed = true;
	}
}
