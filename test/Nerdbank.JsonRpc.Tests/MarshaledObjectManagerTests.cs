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
		_ = rpc.MarshalDisposable(disposable);

		rpc.Dispose();

		Assert.True(disposable.IsDisposed);
	}

	private sealed class TrackingDisposable : IDisposable
	{
		public bool IsDisposed { get; private set; }

		public void Dispose() => this.IsDisposed = true;
	}
}
