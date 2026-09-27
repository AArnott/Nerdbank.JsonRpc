// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Nerdbank.Streams;
using PolyType;
using PolyType.Abstractions;

public partial class ProgressTests
{
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ProgressArgumentReportsInOrderBeforeRequestCompletes(JsonRpcEncoding encoding)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		serverRpc.AddRpcTarget<IProgressService>(new ProgressService());
		serverRpc.Start();
		clientRpc.Start();
		IProgressService client = clientRpc.Attach<IProgressService>();
		RecordingProgress progress = new();

		Assert.Equal(3, await client.ReportProgressAsync(progress, CancellationToken.None));
		Assert.Equal(new[] { 1, 2 }, progress.Values);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task NullProgressSuppressesUpdates(JsonRpcEncoding encoding)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		serverRpc.AddRpcTarget<IProgressService>(new ProgressService());
		serverRpc.Start();
		clientRpc.Start();
		IProgressService client = clientRpc.Attach<IProgressService>();

		Assert.Equal(3, await client.ReportProgressAsync(null, CancellationToken.None));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ProgressArgumentSupportsBatchedRequests(JsonRpcEncoding encoding)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		serverRpc.AddRpcTarget<IProgressService>(new ProgressService());
		serverRpc.Start();
		clientRpc.Start();
		using JsonRpcBatch batch = clientRpc.CreateBatch();
		IProgressService client = batch.Attach<IProgressService>();
		RecordingProgress progress = new();

		Task<int> result = client.ReportProgressAsync(progress, CancellationToken.None);
		await batch.SendAsync();
		Assert.Equal(3, await result);
		Assert.Equal(new[] { 1, 2 }, progress.Values);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ProgressProxyBecomesInertWhenRequestCompletes(JsonRpcEncoding encoding)
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		using JsonRpc serverRpc = new(CreateChannel(serverPipe, encoding));
		serverRpc.AddRpcTarget<IProgressService>(new ProgressService());
		serverRpc.Start();
		clientRpc.Start();
		IProgressService client = clientRpc.Attach<IProgressService>();
		RecordingProgress progress = new();

		await client.ReportProgressAsync(progress, CancellationToken.None);
		Assert.True(await client.ReportAfterCompletionAsync(CancellationToken.None));
		Assert.Equal(new[] { 1, 2 }, progress.Values);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ProgressCannotBeSentInNotification(JsonRpcEncoding encoding)
	{
		(IDuplexPipe clientPipe, _) = FullDuplexStream.CreatePipePair();
		using JsonRpc clientRpc = new(CreateChannel(clientPipe, encoding));
		ITypeShape<IProgress<int>> shape = TypeShapeResolver.ResolveDynamicOrThrow<IProgress<int>, Witness>();

		await Assert.ThrowsAsync<InvalidOperationException>(() => clientRpc.NotifyAsync("report", new RecordingProgress(), shape, CancellationToken.None).AsTask());
	}

	private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding)
		=> encoding == JsonRpcEncoding.Json
			? new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited, NullLogger.Instance)
			: new JsonRpcMessagePackChannel(pipe, NullLogger.Instance);

	[GenerateShapeFor<IProgress<int>>]
	private partial class Witness;
}
