// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using PolyType;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc.Tests;

public partial class MarshaledObjectManagerTests : TestBase
{
	[Test]
	public async Task MessagePackMarkerRejectsNilLifetime()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(new JsonRpcMessagePackChannel(clientPipe, NullLogger.Instance));
		using JsonRpc serverRpc = new(new JsonRpcMessagePackChannel(serverPipe, NullLogger.Instance));
		serverRpc.AddRpcTarget<IRemoteCounterService>(new RemoteCounterService());
		serverRpc.Start();
		clientRpc.Start();
		Sequence<byte> bytes = new();
		MessagePackWriter writer = new(bytes);
		writer.WriteArrayHeader(1);
		writer.WriteMapHeader(3);
		writer.Write("__jsonrpc_marshaled");
		writer.Write(1);
		writer.Write("handle");
		writer.Write(1L);
		writer.Write("lifetime");
		writer.WriteNil();
		writer.Flush();
		JsonRpcValue arguments = JsonRpcValue.FromMessagePack((RawMessagePack)bytes.AsReadOnlySequence.ToArray());

		await Assert.ThrowsAsync<JsonRpcException>(() => clientRpc.RequestAsync("isSameCounter", arguments, CancellationToken.None).AsTask());
	}

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
	public async Task ReleasingOneOfMultipleRemoteProxiesKeepsLocalObjectAlive()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(new JsonRpcMessagePackChannel(clientPipe, NullLogger.Instance));
		using JsonRpc serverRpc = new(new JsonRpcMessagePackChannel(serverPipe, NullLogger.Instance));
		DisposableTarget target = new();
		serverRpc.AddRpcTarget<IDisposableContract>(target);
		serverRpc.Start();
		clientRpc.Start();
		IDisposableContract client = clientRpc.Attach<IDisposableContract>();
		IDisposable firstProxy = await client.GetDisposableAsync(this.TimeoutToken);
		IDisposable secondProxy = await client.GetDisposableAsync(this.TimeoutToken);

		firstProxy.Dispose();

		await Assert.ThrowsAsync<TimeoutException>(() => target.ReturnedDisposable.Disposed.WithTimeout(ExpectedTimeout));
		Assert.False(target.ReturnedDisposable.IsDisposed);

		secondProxy.Dispose();

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
		JsonRpcValue namedReleaseArguments = JsonRpcValue.FromJson(Encoding.UTF8.GetBytes($$"""{"handle":{{handle}},"ownedBySender":false}"""));

		await clientRpc.NotifyAsync("$/releaseMarshaledObject", namedReleaseArguments, this.TimeoutToken);

		await target.ReturnedDisposable.Disposed.WithCancellation(this.TimeoutToken);
		Assert.True(target.ReturnedDisposable.IsDisposed);
	}

	[Test]
	public async Task ReleaseNotificationForSenderOwnedHandleDoesNotReleaseLocalObject()
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
		JsonRpcValue senderOwnedReleaseArguments = JsonRpcValue.FromJson(Encoding.UTF8.GetBytes($$"""{"handle":{{handle}},"ownedBySender":true}"""));

		await clientRpc.NotifyAsync("$/releaseMarshaledObject", senderOwnedReleaseArguments, this.TimeoutToken);

		await Assert.ThrowsAsync<TimeoutException>(() => target.ReturnedDisposable.Disposed.WithTimeout(ExpectedTimeout));
		Assert.False(target.ReturnedDisposable.IsDisposed);

		JsonRpcValue receiverOwnedReleaseArguments = JsonRpcValue.FromJson(Encoding.UTF8.GetBytes($$"""{"handle":{{handle}},"ownedBySender":false}"""));
		await clientRpc.NotifyAsync("$/releaseMarshaledObject", receiverOwnedReleaseArguments, this.TimeoutToken);

		await target.ReturnedDisposable.Disposed.WithCancellation(this.TimeoutToken);
		Assert.True(target.ReturnedDisposable.IsDisposed);
	}

	[Test]
	public void DisposingUnsentRawBatchDoesNotScanMarkerShapedPayload()
	{
		(IDuplexPipe localPipe, _) = FullDuplexStream.CreatePipePair();
		using JsonRpc rpc = new(new JsonRpcJsonChannel(localPipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited, NullLogger.Instance));
		TestDisposable disposable = new();
		using (JsonRpcArgumentsBuilder builder = rpc.CreateArguments(named: false, count: 1, this.TimeoutToken))
		{
			builder.Add(null, disposable, TypeShapeResolver.ResolveDynamicOrThrow<IDisposable, Witness>());
			JsonRpcValue ownedHandleArguments = builder.Build();

			using JsonRpcBatch batch = rpc.CreateBatch();
			JsonRpcValue markerShapedPayload = JsonRpcValue.FromJson("""[{"__jsonrpc_marshaled":1,"handle":1,"lifetime":"explicit"}]"""u8.ToArray());
			ValueTask notification = batch.NotifyAsync("fake", markerShapedPayload, this.TimeoutToken);
			Assert.True(notification.IsCompletedSuccessfully);

			batch.Dispose();

			Assert.False(disposable.IsDisposed);
			GC.KeepAlive(ownedHandleArguments);
		}
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

	[GenerateShapeFor<IDisposable>]
	private partial class Witness;
}
