// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.Reflection;
using System.Text;
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

		await target.ReturnedDisposable.Disposed.WithCancellation(this.TimeoutToken);
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

		await target.ReturnedDisposable.Disposed.WithCancellation(this.TimeoutToken);
		Assert.True(target.ReturnedDisposable.IsDisposed);
	}

	[Test]
	public async Task NamedReleaseNotificationReleasesLocalObject()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(new JsonRpcJsonChannel(clientPipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited, NullLogger.Instance));
		using JsonRpc serverRpc = new(new JsonRpcJsonChannel(serverPipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited, NullLogger.Instance));
		DisposableTarget target = new();
		serverRpc.AddRpcTarget<IDisposableContract>(target);
		serverRpc.Start();
		clientRpc.Start();
		IDisposableContract client = clientRpc.Attach<IDisposableContract>();
		IDisposable remoteDisposable = await client.GetDisposableAsync(this.TimeoutToken);
		FieldInfo handleField = Assert.Single(remoteDisposable.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic), static field => field.FieldType == typeof(long));
		long handle = (long)handleField.GetValue(remoteDisposable)!;
		JsonRpcValue namedReleaseArguments = JsonRpcValue.FromJson(Encoding.UTF8.GetBytes($$"""{"handle":{{handle}},"ownedBySender":true}"""));

		await clientRpc.NotifyAsync("$/releaseMarshaledObject", namedReleaseArguments, this.TimeoutToken);

		await target.ReturnedDisposable.Disposed.WithCancellation(this.TimeoutToken);
		Assert.True(target.ReturnedDisposable.IsDisposed);
	}

	[Test]
	public async Task DisposingUnsentBatchReleasesDisposableArgument()
	{
		(IDuplexPipe localPipe, _) = FullDuplexStream.CreatePipePair();
		using JsonRpc rpc = new(new JsonRpcMessagePackChannel(localPipe, NullLogger.Instance));
		using JsonRpcBatch batch = rpc.CreateBatch();
		IDisposableContract client = batch.Attach<IDisposableContract>();
		TestDisposable disposable = new();
		Task pendingRequest = client.UseDisposableAsync(disposable, this.TimeoutToken);

		batch.Dispose();

		await disposable.Disposed.WithCancellation(this.TimeoutToken);
		Assert.True(disposable.IsDisposed);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingRequest.WithCancellation(this.TimeoutToken));
	}
}
