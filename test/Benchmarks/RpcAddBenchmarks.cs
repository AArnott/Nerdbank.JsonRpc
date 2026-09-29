// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using BenchmarkDotNet.Attributes;

namespace Benchmarks;

/// <summary>Compares a round trip with a few small arguments between Nerdbank.JsonRpc and StreamJsonRpc.</summary>
[MemoryDiagnoser]
public class RpcAddBenchmarks : RpcRoundTripBenchmarksBase
{
	/// <summary>Invokes the small-argument method through Nerdbank.JsonRpc.</summary>
	[Benchmark(Baseline = true)]
	public Task<int> Nerdbank() => this.NerdbankClient.AddAsync(19, 23, "benchmark", CancellationToken.None);

	/// <summary>Invokes the small-argument method through StreamJsonRpc.</summary>
	[Benchmark]
	public Task<int> StreamJsonRpc() => this.StreamJsonRpcClient.AddAsync(19, 23, "benchmark", CancellationToken.None);
}
