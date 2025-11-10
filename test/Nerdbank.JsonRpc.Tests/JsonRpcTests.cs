// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading.Channels;
using Nerdbank.JsonRpc;
using Nerdbank.MessagePack;
using Nerdbank.Streams;
using PolyType;
using Xunit;

public partial class JsonRpcTests : TestBase
{
	private readonly ChannelReader<JsonRpcMessage> reader;
	private readonly ChannelWriter<JsonRpcMessage> writer;
	private readonly JsonRpc jsonRpc;

	public JsonRpcTests()
	{
		this.jsonRpc = new();
		this.reader = this.jsonRpc.OutboundMessages;

		this.jsonRpc.AddRpcTarget(new MockServer());

		Channel<JsonRpcMessage> inboundChannel = Channel.CreateUnbounded<JsonRpcMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
		this.writer = inboundChannel.Writer;
		this.jsonRpc.Start(inboundChannel.Reader);
	}

	[Fact]
	public async Task SimpleRequestResponse()
	{
		await this.writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = nameof(MockServer.GetMagicNumber) }, this.TimeoutToken);
		JsonRpcResult result = Assert.IsType<JsonRpcResult>(await this.reader.ReadAsync(this.TimeoutToken));
		int resultValue = this.jsonRpc.Serializer.Deserialize(result.Result, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken);
		Assert.Equal(42, resultValue);
	}

	[Fact]
	public async Task Add_PositionalArgs()
	{
		Sequence<byte> seq = new();
		MessagePackWriter msgpackWriter = new(seq);
		msgpackWriter.WriteArrayHeader(2);
		msgpackWriter.Write(3);
		msgpackWriter.Write(2);
		msgpackWriter.Flush();

		await this.writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = nameof(MockServer.Add), Arguments = (RawMessagePack)seq.AsReadOnlySequence }, this.TimeoutToken);
		JsonRpcResult result = Assert.IsType<JsonRpcResult>(await this.reader.ReadAsync(this.TimeoutToken));
		int resultValue = this.jsonRpc.Serializer.Deserialize(result.Result, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken);
		Assert.Equal(5, resultValue);
	}

	[Fact]
	public async Task Add_NamedArgs()
	{
		Sequence<byte> seq = new();
		MessagePackWriter msgpackWriter = new(seq);
		msgpackWriter.WriteMapHeader(2);
		msgpackWriter.Write("a");
		msgpackWriter.Write(3);
		msgpackWriter.Write("b");
		msgpackWriter.Write(2);
		msgpackWriter.Flush();

		await this.writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = nameof(MockServer.Add), Arguments = (RawMessagePack)seq.AsReadOnlySequence }, this.TimeoutToken);
		JsonRpcResult result = Assert.IsType<JsonRpcResult>(await this.reader.ReadAsync(this.TimeoutToken));
		int resultValue = this.jsonRpc.Serializer.Deserialize(result.Result, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32, this.TimeoutToken);
		Assert.Equal(5, resultValue);
	}

	[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
	internal partial class MockServer
	{
		public int GetMagicNumber() => 42;

		public int Add(int a, int b) => a + b;
	}
}
