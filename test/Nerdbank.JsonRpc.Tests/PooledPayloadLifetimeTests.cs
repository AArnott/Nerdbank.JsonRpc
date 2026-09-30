// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Nerdbank.Streams;

/// <summary>Verifies that recycling the buffers behind encoded payloads never corrupts or invalidates values that are still in use.</summary>
public partial class PooledPayloadLifetimeTests : TestBase
{
	[Test]
	[Arguments(JsonRpcEncoding.MessagePack)]
	[Arguments(JsonRpcEncoding.Json)]
	public async Task SequentialProxyCallsWithVaryingPayloadsRoundTripIntact(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		for (int i = 0; i < 200; i++)
		{
			string value = CreatePayload(i);
			Assert.Equal(value, await fixture.Client.EchoAsync(value, this.TimeoutToken));
		}
	}

	[Test]
	[Arguments(JsonRpcEncoding.MessagePack)]
	[Arguments(JsonRpcEncoding.Json)]
	public async Task ConcurrentProxyCallsWithVaryingPayloadsRoundTripIntact(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		string[] values = Enumerable.Range(0, 64).Select(CreatePayload).ToArray();
		string[] results = await Task.WhenAll(values.Select(v => fixture.Client.EchoAsync(v, this.TimeoutToken)));
		Assert.Equal(values, results);
	}

	[Test]
	[Arguments(JsonRpcEncoding.MessagePack)]
	[Arguments(JsonRpcEncoding.Json)]
	public async Task BuiltArgumentsCanBeSentRepeatedly(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		JsonRpcValue arguments;
		using (JsonRpcArgumentsBuilder builder = fixture.ClientRpc.CreateArguments(named: false, count: 1, this.TimeoutToken))
		{
			builder.Add(null, 21, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
			arguments = builder.Build();
		}

		for (int i = 0; i < 5; i++)
		{
			// Interleave other traffic so that any prematurely recycled buffer would be reused and overwritten.
			Assert.Equal(CreatePayload(i), await fixture.Client.EchoAsync(CreatePayload(i), this.TimeoutToken));
			Assert.Equal(42, await fixture.ClientRpc.RequestAsync("double", arguments, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken));
		}
	}

	[Test]
	public async Task SingleUseArgumentsAreReleasedWhenCanceledBeforeSending()
	{
		using Fixture fixture = new(JsonRpcEncoding.Json);
		using CancellationTokenSource cts = new();
		cts.Cancel();

		JsonRpcValue notificationArguments = BuildSingleUseArguments(fixture.ClientRpc);
		Assert.Throws<OperationCanceledException>(() => fixture.ClientRpc.NotifyAsync("notify", notificationArguments, cts.Token));
		Assert.Throws<ObjectDisposedException>(() => _ = notificationArguments.Bytes);

		JsonRpcValue requestArguments = BuildSingleUseArguments(fixture.ClientRpc);
		ValueTask<int> request = fixture.ClientRpc.RequestAsync("double", requestArguments, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, cts.Token);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.AsTask());
		Assert.Throws<ObjectDisposedException>(() => _ = requestArguments.Bytes);

		using JsonRpcBatch batch = fixture.ClientRpc.CreateBatch();
		JsonRpcValue batchArguments = BuildSingleUseArguments(fixture.ClientRpc);
		await batch.NotifyAsync("notify", batchArguments, cts.Token);
		Assert.Throws<ObjectDisposedException>(() => _ = batchArguments.Bytes);
	}

	[Test]
	[Arguments(JsonRpcEncoding.MessagePack)]
	[Arguments(JsonRpcEncoding.Json)]
	public async Task BatchedSingleUseArgumentsAreReleasedAfterSending(JsonRpcEncoding encoding)
	{
		(IDuplexPipe local, IDuplexPipe remote) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel sender = Fixture.CreateChannel(local, encoding);
		await using JsonRpcPipeChannel receiver = Fixture.CreateChannel(remote, encoding);
		using JsonRpc rpc = new(sender);
		rpc.Start();

		JsonRpcValue arguments;
		using (JsonRpcArgumentsBuilder builder = rpc.CreateArguments(named: false, count: 1, this.TimeoutToken))
		{
			builder.Add(null, 42, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
			arguments = builder.BuildForSingleUse();
		}

		using JsonRpcBatch batch = rpc.CreateBatch();
		await batch.NotifyAsync("method", arguments, this.TimeoutToken);
		await batch.SendAsync(this.TimeoutToken);
		JsonRpcMessageBatch received = Assert.IsType<JsonRpcMessageBatch>(await receiver.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal("method", Assert.IsType<JsonRpcRequest>(Assert.Single(received.Messages)).Method);
		Assert.Throws<ObjectDisposedException>(() => _ = arguments.Bytes);
	}

	private static JsonRpcValue BuildSingleUseArguments(JsonRpc rpc)
	{
		using JsonRpcArgumentsBuilder builder = rpc.CreateArguments(named: false, count: 1, CancellationToken.None);
		builder.Add(null, 42, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
		return builder.BuildForSingleUse();
	}

	private static string CreatePayload(int seed) => new((char)('a' + (seed % 26)), (seed * 37) % 3000);

	private sealed class EchoService : IEchoService
	{
		public Task<string> EchoAsync(string value, CancellationToken cancellationToken) => Task.FromResult(value);

		public Task<int> DoubleAsync(int value, CancellationToken cancellationToken) => Task.FromResult(value * 2);
	}

	private sealed class Fixture : IDisposable
	{
		internal Fixture(JsonRpcEncoding encoding)
		{
			(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
			this.ClientRpc = new(CreateChannel(clientPipe, encoding));
			this.ServerRpc = new(CreateChannel(serverPipe, encoding));
			this.ServerRpc.AddRpcTarget<IEchoService>(new EchoService());
			this.ServerRpc.Start();
			this.ClientRpc.Start();
			this.Client = this.ClientRpc.Attach<IEchoService>();
		}

		internal JsonRpc ClientRpc { get; }

		internal JsonRpc ServerRpc { get; }

		internal IEchoService Client { get; }

		public void Dispose()
		{
			this.ClientRpc.Dispose();
			this.ServerRpc.Dispose();
		}

		internal static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding)
			=> encoding switch
			{
				JsonRpcEncoding.MessagePack => new JsonRpcMessagePackChannel(pipe),
				JsonRpcEncoding.Json => new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited),
				_ => throw new ArgumentOutOfRangeException(nameof(encoding)),
			};
	}
}
