// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Nerdbank.Streams;

namespace Benchmarks;

/// <summary>Measures the cost of attaching a generated client proxy to a connection or batch.</summary>
[MemoryDiagnoser]
public class AttachBenchmarks
{
	private JsonRpcPipeChannel? channel;
	private JsonRpc? rpc;
	private JsonRpcBatch? batch;

	/// <summary>Creates a connection to attach proxies to.</summary>
	[GlobalSetup]
	public void Setup()
	{
		(IDuplexPipe pipe, _) = FullDuplexStream.CreatePipePair();
		this.channel = new JsonRpcMessagePackChannel(pipe, NullLogger.Instance);
		this.rpc = new JsonRpc(this.channel);
		this.batch = this.rpc.CreateBatch();
	}

	/// <summary>Disposes the connection.</summary>
	[GlobalCleanup]
	public async Task CleanupAsync()
	{
		this.rpc?.Dispose();
		if (this.channel is not null)
		{
			await this.channel.DisposeAsync();
		}
	}

	/// <summary>Attaches a proxy with the generic API.</summary>
	/// <returns>The proxy.</returns>
	[Benchmark(Baseline = true)]
	public IRpcBenchmarkContract AttachGeneric() => this.rpc!.Attach<IRpcBenchmarkContract>();

	/// <summary>Attaches a proxy with the <see cref="Type"/>-based API.</summary>
	/// <returns>The proxy.</returns>
	[Benchmark]
	public object AttachByType() => this.rpc!.Attach(typeof(IRpcBenchmarkContract));

	/// <summary>Attaches a proxy to a batch with the generic API.</summary>
	/// <returns>The proxy.</returns>
	[Benchmark]
	public IRpcBenchmarkContract AttachToBatch() => this.batch!.Attach<IRpcBenchmarkContract>();
}
