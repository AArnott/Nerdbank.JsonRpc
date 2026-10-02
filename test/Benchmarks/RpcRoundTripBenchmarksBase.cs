// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Nerdbank.Streams;
using StreamRpc = StreamJsonRpc.JsonRpc;

namespace Benchmarks;

/// <summary>
/// Establishes matching Nerdbank.JsonRpc and StreamJsonRpc client/server pairs over in-memory duplex pipes,
/// using the same <see cref="IRpcBenchmarkContract"/> and the same wire encoding for both, so that derived
/// benchmark classes can compare complete request/response round trips.
/// </summary>
public abstract class RpcRoundTripBenchmarksBase
{
	private JsonRpcPipeChannel? nerdbankClientChannel;
	private JsonRpcPipeChannel? nerdbankServerChannel;
	private JsonRpc? nerdbankClientRpc;
	private JsonRpc? nerdbankServerRpc;

	private StreamRpc? streamClientRpc;
	private StreamRpc? streamServerRpc;

	/// <summary>Gets or sets the wire encoding to use for both libraries in this benchmark case.</summary>
	[Params(RpcEncoding.Json, RpcEncoding.MessagePack)]
	public RpcEncoding Encoding { get; set; }

	/// <summary>Gets the Nerdbank.JsonRpc client proxy, connected to a Nerdbank.JsonRpc server.</summary>
	protected IRpcBenchmarkContract NerdbankClient { get; private set; } = null!;

	/// <summary>Gets the StreamJsonRpc client proxy, connected to a StreamJsonRpc server.</summary>
	protected IRpcBenchmarkContract StreamJsonRpcClient { get; private set; } = null!;

	/// <summary>Gets the deterministic large object graph shared by every large-argument benchmark case.</summary>
	protected WorkspaceGraph LargeGraph { get; private set; } = null!;

	/// <summary>Constructs both client/server pairs for the selected <see cref="Encoding"/> and validates their behavior.</summary>
	[GlobalSetup]
	public async Task SetupAsync()
	{
		this.LargeGraph = WorkspaceGraphFactory.CreateLarge();

		(IDuplexPipe nerdbankClientPipe, IDuplexPipe nerdbankServerPipe) = FullDuplexStream.CreatePipePair();
		this.nerdbankClientChannel = CreateNerdbankChannel(nerdbankClientPipe, this.Encoding);
		this.nerdbankServerChannel = CreateNerdbankChannel(nerdbankServerPipe, this.Encoding);
		this.nerdbankClientRpc = new JsonRpc(this.nerdbankClientChannel);
		this.nerdbankServerRpc = new JsonRpc(this.nerdbankServerChannel);
		this.nerdbankServerRpc.AddRpcTarget<IRpcBenchmarkContract>(new RpcBenchmarkServer());
		this.nerdbankClientRpc.Start();
		this.nerdbankServerRpc.Start();
		this.NerdbankClient = this.nerdbankClientRpc.Attach<IRpcBenchmarkContract>();

		(IDuplexPipe streamClientPipe, IDuplexPipe streamServerPipe) = FullDuplexStream.CreatePipePair();
		this.streamServerRpc = new StreamRpc(CreateStreamJsonRpcHandler(streamServerPipe, this.Encoding));
		this.streamServerRpc.AddLocalRpcTarget<IRpcBenchmarkContract>(new RpcBenchmarkServer(), null);
		this.streamClientRpc = new StreamRpc(CreateStreamJsonRpcHandler(streamClientPipe, this.Encoding));
		this.StreamJsonRpcClient = this.streamClientRpc.Attach<IRpcBenchmarkContract>();
		this.streamServerRpc.StartListening();
		this.streamClientRpc.StartListening();

		await this.ValidateAsync();
	}

	/// <summary>Disposes both client/server pairs and their transports.</summary>
	[GlobalCleanup]
	public async Task CleanupAsync()
	{
		this.streamClientRpc?.Dispose();
		this.streamServerRpc?.Dispose();
		this.nerdbankClientRpc?.Dispose();
		this.nerdbankServerRpc?.Dispose();

		if (this.nerdbankClientChannel is not null)
		{
			await this.nerdbankClientChannel.DisposeAsync();
		}

		if (this.nerdbankServerChannel is not null)
		{
			await this.nerdbankServerChannel.DisposeAsync();
		}
	}

	/// <summary>Creates a channel with the benchmark's framing and serializer for the chosen encoding.</summary>
	/// <remarks>The default MessagePack framing (a big-endian length header) matches StreamJsonRpc's LengthHeaderMessageHandler.</remarks>
	/// <param name="pipe">The connected transport.</param>
	/// <param name="encoding">The wire encoding.</param>
	/// <returns>A channel ready for an RPC instance.</returns>
	internal static JsonRpcPipeChannel CreateNerdbankChannel(IDuplexPipe pipe, RpcEncoding encoding) => encoding switch
	{
		RpcEncoding.Json => new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited),
		RpcEncoding.MessagePack => new JsonRpcMessagePackChannel(pipe),
		_ => throw new ArgumentOutOfRangeException(nameof(encoding)),
	};

	// Use the same MessagePack serializer implementation and shape provider for both RPC stacks.
	private static StreamJsonRpc.IJsonRpcMessageHandler CreateStreamJsonRpcHandler(IDuplexPipe pipe, RpcEncoding encoding) => encoding switch
	{
		RpcEncoding.Json => new StreamJsonRpc.NewLineDelimitedMessageHandler(pipe, new StreamJsonRpc.JsonMessageFormatter()),
		RpcEncoding.MessagePack => new StreamJsonRpc.LengthHeaderMessageHandler(pipe, new StreamJsonRpc.NerdbankMessagePackFormatter { TypeShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_Benchmarks.Default }),
		_ => throw new ArgumentOutOfRangeException(nameof(encoding)),
	};

	/// <summary>Exercises every scenario once through both stacks and confirms they produce identical, expected results.</summary>
	private async Task ValidateAsync()
	{
		long expectedChecksum = WorkspaceGraphFactory.Checksum(this.LargeGraph);
		long nerdbankChecksum = await this.NerdbankClient.ProcessGraphAsync(this.LargeGraph, CancellationToken.None);
		long streamChecksum = await this.StreamJsonRpcClient.ProcessGraphAsync(this.LargeGraph, CancellationToken.None);
		if (nerdbankChecksum != expectedChecksum || streamChecksum != expectedChecksum)
		{
			throw new InvalidOperationException("The large graph round trip did not produce the expected checksum.");
		}

		int nerdbankSum = await this.NerdbankClient.AddAsync(19, 23, "benchmark", CancellationToken.None);
		int streamSum = await this.StreamJsonRpcClient.AddAsync(19, 23, "benchmark", CancellationToken.None);
		if (nerdbankSum != 19 + 23 + "benchmark".Length || nerdbankSum != streamSum)
		{
			throw new InvalidOperationException("The small-argument round trip produced an unexpected result.");
		}

		int nerdbankPing = await this.NerdbankClient.PingAsync(CancellationToken.None);
		int streamPing = await this.StreamJsonRpcClient.PingAsync(CancellationToken.None);
		if (nerdbankPing != 1 || nerdbankPing != streamPing)
		{
			throw new InvalidOperationException("The no-argument round trip produced an unexpected result.");
		}

		int nerdbankValueTaskPing = await this.NerdbankClient.PingValueTaskAsync(CancellationToken.None);
		int streamValueTaskPing = await this.StreamJsonRpcClient.PingValueTaskAsync(CancellationToken.None);
		if (nerdbankValueTaskPing != 1 || nerdbankValueTaskPing != streamValueTaskPing)
		{
			throw new InvalidOperationException("The ValueTask round trip produced an unexpected result.");
		}
	}
}
