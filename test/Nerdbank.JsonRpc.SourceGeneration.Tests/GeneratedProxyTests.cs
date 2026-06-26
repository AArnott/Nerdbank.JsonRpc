// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Nerdbank.JsonRpc;
using Nerdbank.Streams;
using Xunit;

using ShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_SourceGeneration_Tests;

public class GeneratedProxyTests
{
	[Fact]
	public async Task GeneratedProxy_SimpleAdd()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();

		StreamingJsonRpcMessageChannel clientChannel = new(clientPipe, NullLogger.Instance);
		StreamingJsonRpcMessageChannel serverChannel = new(serverPipe, NullLogger.Instance);

		JsonRpc clientRpc = new(clientChannel);
		clientRpc.Start();
		CalculatorProxy client = new(clientRpc, ShapeProvider.Default);

		JsonRpc serverRpc = new(serverChannel);
		serverRpc.AddRpcTarget<ICalculator>(new Calculator());
		serverRpc.Start();

		using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
		int sum = await client.AddAsync(1, 3, cts.Token);
		Assert.Equal(4, sum);
	}
}
