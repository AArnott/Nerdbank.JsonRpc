// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Nerdbank.Streams;
using PolyType;

namespace Benchmarks;

// For more information on the VS BenchmarkDotNet Diagnosers see https://learn.microsoft.com/visualstudio/profiling/profiling-with-benchmark-dotnet
////[CPUUsageDiagnoser]
[MemoryDiagnoser]
public partial class Benchmarks
{
	private JsonRpc clientRpc = null!;
	private JsonRpc serverRpc = null!;

	[GlobalSetup]
	public void Setup()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();

		StreamingJsonRpcMessageChannel clientChannel = new(clientPipe, NullLogger.Instance);
		StreamingJsonRpcMessageChannel serverChannel = new(serverPipe, NullLogger.Instance);

		this.clientRpc = new(clientChannel);
		this.clientRpc.Start();

		this.serverRpc = new(serverChannel);
		this.serverRpc.AddRpcTarget(new Server());
		this.serverRpc.Start();
	}

	[Benchmark]
	public ValueTask<int> Add()
	{
		return this.clientRpc.RequestAsync<AddArgs, int, Witness>(nameof(Server.Add), new AddArgs(1, 3), CancellationToken.None);
	}

#pragma warning disable SA1300 // Element should begin with upper-case letter
	[GenerateShape]
	internal partial record struct AddArgs(int a, int b);
#pragma warning restore SA1300 // Element should begin with upper-case letter

	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	public partial class Server
	{
		public int Add(int a, int b) => a + b;
	}

	[GenerateShapeFor<int>]
	private partial class Witness;
}
