// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.IO.Pipes;
using BenchmarkDotNet.Attributes;
using Nerdbank.Streams;

namespace Benchmarks;

/// <summary>
/// Compares end-to-end RPC costs over in-memory pipes and OS named pipes with both peers in the same process.
/// Named pipes exercise kernel IPC but not cross-process scheduling or address-space copying.
/// </summary>
[MemoryDiagnoser]
public class RpcTransportBenchmarks
{
	private JsonRpcPipeChannel? clientChannel;
	private JsonRpcPipeChannel? serverChannel;
	private JsonRpc? clientRpc;
	private JsonRpc? serverRpc;
	private NamedPipeServerStream? serverStream;
	private NamedPipeClientStream? clientStream;
	private IRpcBenchmarkContract client = null!;
	private WorkspaceGraph largeGraph = null!;

	/// <summary>Gets or sets the transport for both endpoints.</summary>
	[Params(RpcTransport.InMemory, RpcTransport.NamedPipe)]
	public RpcTransport Transport { get; set; }

	/// <summary>Gets or sets the encoding and framing for both endpoints.</summary>
	[Params(RpcEncoding.Json, RpcEncoding.MessagePack)]
	public RpcEncoding Encoding { get; set; }

	/// <summary>Connects and validates both endpoints outside the measured operation.</summary>
	[GlobalSetup]
	public async Task SetupAsync()
	{
		this.largeGraph = WorkspaceGraphFactory.CreateLarge();
		try
		{
			(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = this.Transport switch
			{
				RpcTransport.InMemory => FullDuplexStream.CreatePipePair(),
				RpcTransport.NamedPipe => await this.CreateNamedPipePairAsync(),
				_ => throw new ArgumentOutOfRangeException(nameof(this.Transport)),
			};

			this.clientChannel = RpcRoundTripBenchmarksBase.CreateNerdbankChannel(clientPipe, this.Encoding);
			this.serverChannel = RpcRoundTripBenchmarksBase.CreateNerdbankChannel(serverPipe, this.Encoding);
			this.clientRpc = new JsonRpc(this.clientChannel);
			this.serverRpc = new JsonRpc(this.serverChannel);
			this.serverRpc.AddRpcTarget<IRpcBenchmarkContract>(new RpcBenchmarkServer());
			this.clientRpc.Start();
			this.serverRpc.Start();
			this.client = this.clientRpc.Attach<IRpcBenchmarkContract>();

			if (await this.Ping() != 1 || await this.Add() != 19 + 23 + "benchmark".Length || await this.ProcessGraph() != WorkspaceGraphFactory.Checksum(this.largeGraph))
			{
				throw new InvalidOperationException("The transport did not produce the expected RPC results.");
			}
		}
		catch
		{
			await this.CleanupAsync();
			throw;
		}
	}

	/// <summary>Releases the connections and OS handles after measurement.</summary>
	[GlobalCleanup]
	public async Task CleanupAsync()
	{
		this.clientRpc?.Dispose();
		this.serverRpc?.Dispose();
		if (this.clientChannel is not null)
		{
			await this.clientChannel.DisposeAsync();
		}

		if (this.serverChannel is not null)
		{
			await this.serverChannel.DisposeAsync();
		}

		this.clientStream?.Dispose();
		this.serverStream?.Dispose();
	}

	/// <summary>Measures a no-argument request and response.</summary>
	/// <returns>The server's fixed result.</returns>
	[Benchmark]
	public Task<int> Ping() => this.client.PingAsync(CancellationToken.None);

	/// <summary>Measures a request with a few small arguments and its response.</summary>
	/// <returns>The sum computed by the server.</returns>
	[Benchmark]
	public Task<int> Add() => this.client.AddAsync(19, 23, "benchmark", CancellationToken.None);

	/// <summary>Measures a large-graph request and its response.</summary>
	/// <returns>The server's checksum of the graph.</returns>
	[Benchmark]
	public Task<long> ProcessGraph() => this.client.ProcessGraphAsync(this.largeGraph, CancellationToken.None);

	private async Task<(IDuplexPipe Client, IDuplexPipe Server)> CreateNamedPipePairAsync()
	{
		string name = $"Nerdbank.JsonRpc.Benchmarks.{Guid.NewGuid():N}";
		this.serverStream = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
		this.clientStream = new NamedPipeClientStream(".", name, PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
		Task accept = this.serverStream.WaitForConnectionAsync(timeout.Token);
		await this.clientStream.ConnectAsync(timeout.Token);
		await accept;
		return (this.clientStream.UsePipe(), this.serverStream.UsePipe());
	}
}
