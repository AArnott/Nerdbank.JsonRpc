// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.IO.Pipelines;
using System.IO.Pipes;
using BenchmarkDotNet.Attributes;
using Nerdbank.Streams;

namespace Benchmarks;

/// <summary>
/// Compares end-to-end RPC costs over in-memory pipes and OS named pipes with both peers in the same process.
/// Named pipes still copy payload bytes through kernel buffers; this same-process comparison excludes cross-process scheduling and address-space effects.
/// Shared-memory endpoints map the same memory independently and rendezvous through <see cref="SharedMemoryDuplexPipe.ListenAsync"/>.
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
	private SharedMemoryDuplexPipe? sharedMemoryClient;
	private SharedMemoryDuplexPipe? sharedMemoryServer;
	private IRpcBenchmarkContract client = null!;
	private WorkspaceGraph largeGraph = null!;

	/// <summary>Gets or sets the transport for both endpoints.</summary>
	[Params(RpcTransport.InMemory, RpcTransport.NamedPipe, RpcTransport.SharedMemoryIpc)]
	public RpcTransport Transport { get; set; }

	/// <summary>Gets or sets the encoding and framing for both endpoints.</summary>
	[Params(RpcEncoding.Json, RpcEncoding.MessagePack)]
	public RpcEncoding Encoding { get; set; }

	/// <summary>Connects and validates both endpoints outside the measured operation.</summary>
	[GlobalSetup]
	public async Task SetupAsync()
	{
		this.largeGraph = WorkspaceGraphFactory.CreateLarge();
		if (this.Transport == RpcTransport.SharedMemoryIpc)
		{
			await ValidateZeroCopyWraparoundAsync();
		}

		try
		{
			(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = this.Transport switch
			{
				RpcTransport.InMemory => FullDuplexStream.CreatePipePair(),
				RpcTransport.NamedPipe => await this.CreateNamedPipePairAsync(),
				RpcTransport.SharedMemoryIpc => await this.CreateSharedMemoryPairAsync(),
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
		this.sharedMemoryClient?.Dispose();
		this.sharedMemoryServer?.Dispose();
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

	private static async Task ValidateZeroCopyWraparoundAsync()
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
		(SharedMemoryDuplexPipe client, SharedMemoryDuplexPipe server) = await CreateSharedMemoryEndpointsAsync(64, timeout.Token);
		using SharedMemoryDuplexPipe clientScope = client;
		using SharedMemoryDuplexPipe serverScope = server;
		byte[] expected = new byte[200];
		for (int i = 0; i < expected.Length; i++)
		{
			expected[i] = (byte)((i * 31) & 0xff);
		}

		// Several transfers force the ring to wrap and to stage an oversized message.
		for (int round = 0; round < 4; round++)
		{
			Task<FlushResult> send = client.Output.WriteAsync(expected, timeout.Token).AsTask();
			byte[] actual = new byte[expected.Length];
			int received = 0;
			while (received < actual.Length)
			{
				ReadResult read = await server.Input.ReadAsync(timeout.Token);
				if (read.Buffer.IsEmpty && read.IsCompleted)
				{
					server.Input.AdvanceTo(read.Buffer.End);
					throw new InvalidOperationException("The shared-memory pipe completed before the transfer finished.");
				}

				int count = checked((int)Math.Min(read.Buffer.Length, actual.Length - received));
				read.Buffer.Slice(0, count).CopyTo(actual.AsSpan(received));
				server.Input.AdvanceTo(read.Buffer.GetPosition(count));
				received += count;
			}

			await send;
			if (!expected.AsSpan().SequenceEqual(actual))
			{
				throw new InvalidOperationException("The zero-copy ring corrupted a wrapped transfer.");
			}
		}
	}

	/// <summary>Creates connected shared-memory endpoints over the production IPC signaling path.</summary>
	private static async Task<(SharedMemoryDuplexPipe Client, SharedMemoryDuplexPipe Server)> CreateSharedMemoryEndpointsAsync(int capacity, CancellationToken cancellationToken)
	{
		SharedMemoryPipeOptions options = new() { Capacity = capacity };
		string channel = Guid.NewGuid().ToString("N");
		using CancellationTokenSource setupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		Task<SharedMemoryDuplexPipe> listen = SharedMemoryDuplexPipe.ListenAsync(channel, options, setupCancellation.Token);
		SharedMemoryDuplexPipe client;
		try
		{
			client = await SharedMemoryDuplexPipe.ConnectAsync(channel, options, setupCancellation.Token);
		}
		catch (Exception connectionException)
		{
			await setupCancellation.CancelAsync();
			try
			{
				(await listen).Dispose();
			}
			catch (OperationCanceledException) when (setupCancellation.IsCancellationRequested)
			{
			}
			catch (Exception listenerException)
			{
				throw new AggregateException("Shared-memory connection setup and listener cleanup both failed.", connectionException, listenerException);
			}

			throw;
		}

		try
		{
			return (client, await listen);
		}
		catch
		{
			client.Dispose();
			throw;
		}
	}

	private async Task<(IDuplexPipe Client, IDuplexPipe Server)> CreateSharedMemoryPairAsync()
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
		(this.sharedMemoryClient, this.sharedMemoryServer) = await CreateSharedMemoryEndpointsAsync(1024 * 1024, timeout.Token);
		return (this.sharedMemoryClient, this.sharedMemoryServer);
	}

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
