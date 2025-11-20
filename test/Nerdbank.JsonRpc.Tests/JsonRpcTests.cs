// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Nerdbank.Streams;
using PolyType;

public partial class JsonRpcTests : TestBase
{
	private readonly JsonRpc clientRpc;
	private readonly JsonRpc serverRpc;
	private readonly Server server;

	public JsonRpcTests()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();

		StreamingJsonRpcMessageChannel clientChannel = new(clientPipe, LoggerFactory.CreateLogger("client"));
		StreamingJsonRpcMessageChannel serverChannel = new(serverPipe, LoggerFactory.CreateLogger("server"));

		this.clientRpc = new(clientChannel);
		this.clientRpc.Start();

		this.serverRpc = new(serverChannel);
		this.serverRpc.AddRpcTarget(this.server = new Server());
		this.serverRpc.Start();
	}

	[Fact]
	public async Task SimpleAdd()
	{
		int sum = await this.clientRpc.RequestAsync<AddArgs, int, Witness>(nameof(Server.Add), new AddArgs(1, 3), this.TimeoutToken);
		Assert.Equal(4, sum);
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
