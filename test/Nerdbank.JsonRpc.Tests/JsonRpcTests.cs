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
	private readonly IServer client;

	public JsonRpcTests()
	{
		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();

		StreamingJsonRpcMessageChannel clientChannel = new(clientPipe, LoggerFactory.CreateLogger("client"));
		StreamingJsonRpcMessageChannel serverChannel = new(serverPipe, LoggerFactory.CreateLogger("server"));

		this.clientRpc = new(clientChannel);
		this.clientRpc.Start();
		this.client = new ServerProxy(this.clientRpc);

		this.serverRpc = new(serverChannel);
		this.serverRpc.AddRpcTarget<IServer>(this.server = new Server());
		this.serverRpc.Start();
	}

	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	public partial interface IServer
	{
		ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
	}

	[Fact]
	public async Task SimpleAdd()
	{
		int sum = await this.client.AddAsync(1, 3, this.TimeoutToken);
		Assert.Equal(4, sum);
	}

	public class Server : IServer
	{
		public ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken) => new(a + b);
	}

	internal partial class ServerProxy(JsonRpc jsonRpc) : IServer
	{
		public ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken)
			=> jsonRpc.RequestAsync<AddArgs, int, Witness>(nameof(IServer.AddAsync), new AddArgs { a = a, b = b }, cancellationToken);

#pragma warning disable SA1307 // Public field should start with an upper-case letter.
		[GenerateShape]
		internal partial struct AddArgs
		{
			public int a;
			public int b;
		}
#pragma warning restore SA1307 // Public field should start with an upper-case letter.
	}

	[GenerateShapeFor<int>]
	private partial class Witness;
}
