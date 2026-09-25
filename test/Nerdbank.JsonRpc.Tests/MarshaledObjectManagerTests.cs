// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc.Tests;

public class MarshaledObjectManagerTests : TestBase
{
	[Test]
	public async Task DisposingRemoteProxySendsReleaseNotification()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(new JsonRpcMessagePackChannel(clientPipe, NullLogger.Instance));
		using JsonRpc serverRpc = new(new JsonRpcMessagePackChannel(serverPipe, NullLogger.Instance));
		DisposableTarget target = new();
		serverRpc.AddRpcTarget<IDisposableContract>(target);
		serverRpc.Start();
		clientRpc.Start();
		IDisposableContract client = clientRpc.Attach<IDisposableContract>();
		IDisposable remoteDisposable = await client.GetDisposableAsync(this.TimeoutToken);

		remoteDisposable.Dispose();

		await target.ReturnedDisposable.Disposed.Task.WithCancellation(this.TimeoutToken);
		Assert.True(target.ReturnedDisposable.IsDisposed);
	}

	[Test]
	public async Task DisposeReleasesLocallyOwnedObjects()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(new JsonRpcMessagePackChannel(clientPipe, NullLogger.Instance));
		using JsonRpc serverRpc = new(new JsonRpcMessagePackChannel(serverPipe, NullLogger.Instance));
		DisposableTarget target = new();
		serverRpc.AddRpcTarget<IDisposableContract>(target);
		serverRpc.Start();
		clientRpc.Start();
		IDisposableContract client = clientRpc.Attach<IDisposableContract>();
		_ = await client.GetDisposableAsync(this.TimeoutToken);

		serverRpc.Dispose();

		await target.ReturnedDisposable.Disposed.Task.WithCancellation(this.TimeoutToken);
		Assert.True(target.ReturnedDisposable.IsDisposed);
	}
}
