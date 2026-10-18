// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using BenchmarkDotNet.Attributes;

namespace Benchmarks;

/// <summary>Compares a directly consumed ValueTask RPC round trip between Nerdbank.JsonRpc and StreamJsonRpc.</summary>
[MemoryDiagnoser]
public class RpcValueTaskPingBenchmarks : RpcRoundTripBenchmarksBase
{
	/// <summary>Invokes the no-argument method through Nerdbank.JsonRpc.</summary>
	[Benchmark(Baseline = true)]
	public ValueTask<int> Nerdbank() => this.NerdbankClient.PingValueTaskAsync(CancellationToken.None);

	/// <summary>Invokes the no-argument method through StreamJsonRpc.</summary>
	[Benchmark]
	public ValueTask<int> StreamJsonRpc() => this.StreamJsonRpcClient.PingValueTaskAsync(CancellationToken.None);
}
