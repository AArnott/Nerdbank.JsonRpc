// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using BenchmarkDotNet.Attributes;
using Nerdbank.Streams;

namespace Benchmarks;

/// <summary>Measures request timeout overhead separately from the benchmark runner's per-invocation Task adapter.</summary>
[MemoryDiagnoser]
public class RpcRequestTimeoutBenchmarks
{
	private JsonRpcPipeChannel clientChannel = null!;
	private JsonRpcPipeChannel serverChannel = null!;
	private JsonRpc clientRpc = null!;
	private JsonRpc serverRpc = null!;
	private IRpcBenchmarkContract proxy = null!;
	private CancellationTokenSource callerCancellationSource = null!;
	private CancellationToken cancellationToken;
	private JsonRpcValue emptyArguments;

	[Params(RpcEncoding.Json, RpcEncoding.MessagePack)]
	public RpcEncoding Encoding { get; set; }

	[Params(false, true)]
	public bool TimeoutEnabled { get; set; }

	[Params(false, true)]
	public bool Cancellable { get; set; }

	[GlobalSetup]
	public async Task SetupAsync()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		this.clientChannel = RpcRoundTripBenchmarksBase.CreateNerdbankChannel(clientPipe, this.Encoding);
		this.serverChannel = RpcRoundTripBenchmarksBase.CreateNerdbankChannel(serverPipe, this.Encoding);
		this.clientRpc = new JsonRpc(this.clientChannel, new JsonRpcOptions
		{
			OutboundRequestTimeout = this.TimeoutEnabled ? TimeSpan.FromMinutes(1) : Timeout.InfiniteTimeSpan,
		});
		this.serverRpc = new JsonRpc(this.serverChannel);
		this.serverRpc.AddRpcTarget<IRpcBenchmarkContract>(new RpcBenchmarkServer());
		this.clientRpc.Start();
		this.serverRpc.Start();
		this.proxy = this.clientRpc.Attach<IRpcBenchmarkContract>();
		this.callerCancellationSource = new();
		this.cancellationToken = this.Cancellable ? this.callerCancellationSource.Token : CancellationToken.None;
		using (JsonRpcArgumentsBuilder builder = this.clientRpc.CreateArguments(named: false, count: 0))
		{
			this.emptyArguments = builder.Build();
		}

		if (await this.Direct() != 32 || await this.Proxy() != 32 || await this.Batch() != 32)
		{
			throw new InvalidOperationException("The request did not produce the expected result.");
		}
	}

	[GlobalCleanup]
	public async Task CleanupAsync()
	{
		this.clientRpc.Dispose();
		this.serverRpc.Dispose();
		await this.clientChannel.DisposeAsync();
		await this.serverChannel.DisposeAsync();
		this.callerCancellationSource.Dispose();
	}

	[Benchmark(OperationsPerInvoke = 32)]
	public async Task<int> Direct()
	{
		int sum = 0;
		for (int i = 0; i < 32; i++)
		{
			sum += await this.clientRpc.RequestAsync("pingValueTask", this.emptyArguments, PolyType.SourceGenerator.TypeShapeProvider_Benchmarks.Default.Int32, this.cancellationToken).ConfigureAwait(false);
		}

		return sum;
	}

	[Benchmark(OperationsPerInvoke = 32)]
	public async Task<int> Proxy()
	{
		int sum = 0;
		for (int i = 0; i < 32; i++)
		{
			sum += await this.proxy.PingValueTaskAsync(this.cancellationToken).ConfigureAwait(false);
		}

		return sum;
	}

	[Benchmark(OperationsPerInvoke = 32)]
	public async Task<int> Batch()
	{
		int sum = 0;
		for (int i = 0; i < 8; i++)
		{
			using JsonRpcBatch batch = this.clientRpc.CreateBatch();
			ValueTask<int> first = batch.RequestAsync("pingValueTask", this.emptyArguments, PolyType.SourceGenerator.TypeShapeProvider_Benchmarks.Default.Int32, this.cancellationToken);
			ValueTask<int> second = batch.RequestAsync("pingValueTask", this.emptyArguments, PolyType.SourceGenerator.TypeShapeProvider_Benchmarks.Default.Int32, this.cancellationToken);
			ValueTask<int> third = batch.RequestAsync("pingValueTask", this.emptyArguments, PolyType.SourceGenerator.TypeShapeProvider_Benchmarks.Default.Int32, this.cancellationToken);
			ValueTask<int> fourth = batch.RequestAsync("pingValueTask", this.emptyArguments, PolyType.SourceGenerator.TypeShapeProvider_Benchmarks.Default.Int32, this.cancellationToken);
			await batch.SendAsync(this.cancellationToken).ConfigureAwait(false);
			sum += await first.ConfigureAwait(false);
			sum += await second.ConfigureAwait(false);
			sum += await third.ConfigureAwait(false);
			sum += await fourth.ConfigureAwait(false);
		}

		return sum;
	}
}
