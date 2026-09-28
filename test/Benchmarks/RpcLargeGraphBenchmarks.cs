// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using BenchmarkDotNet.Attributes;

namespace Benchmarks;

/// <summary>Compares a round trip carrying a large object graph between Nerdbank.JsonRpc and StreamJsonRpc.</summary>
[MemoryDiagnoser]
public class RpcLargeGraphBenchmarks : RpcRoundTripBenchmarksBase
{
	/// <summary>Invokes the large-graph method through Nerdbank.JsonRpc.</summary>
	[Benchmark(Baseline = true)]
	public Task<long> Nerdbank() => this.NerdbankClient.ProcessGraphAsync(this.LargeGraph, CancellationToken.None);

	/// <summary>Invokes the large-graph method through StreamJsonRpc.</summary>
	[Benchmark]
	public Task<long> StreamJsonRpc() => this.StreamJsonRpcClient.ProcessGraphAsync(this.LargeGraph, CancellationToken.None);
}
