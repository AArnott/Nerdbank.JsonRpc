// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using BenchmarkDotNet.Attributes;

namespace Benchmarks;

/// <summary>Compares a no-argument RPC round trip between Nerdbank.JsonRpc and StreamJsonRpc.</summary>
[MemoryDiagnoser]
public class RpcPingBenchmarks : RpcRoundTripBenchmarksBase
{
	/// <summary>Invokes the no-argument method through Nerdbank.JsonRpc.</summary>
	[Benchmark(Baseline = true)]
	public Task<int> Nerdbank() => this.NerdbankClient.PingAsync(CancellationToken.None);

	/// <summary>Invokes the no-argument method through StreamJsonRpc.</summary>
	[Benchmark]
	public Task<int> StreamJsonRpc() => this.StreamJsonRpcClient.PingAsync(CancellationToken.None);
}
