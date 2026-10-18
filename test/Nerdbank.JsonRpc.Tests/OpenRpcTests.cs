// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using System.IO.Pipelines;
using System.Text.Json.Nodes;
using Nerdbank.Streams;
using PolyType;

namespace Nerdbank.JsonRpc.Tests;

public class OpenRpcTests : TestBase
{
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	[Test]
	public void GetOpenRpcDocument_UsesResolvedMethodAndParameterMetadata(JsonRpcEncoding encoding)
	{
		(IDuplexPipe peerPipe, IDuplexPipe unusedPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc rpc = new(CreateChannel(peerPipe, encoding));
		using JsonRpc unusedPeer = new(CreateChannel(unusedPipe, encoding));
		rpc.AddRpcTarget(new OpenRpcTarget());

		JsonObject document = rpc.GetOpenRpcDocument();
		JsonObject method = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(document["methods"]!).Where(item => item?["name"]?.GetValue<string>() == "add")));
		JsonArray parameters = Assert.IsType<JsonArray>(method["params"]);
		Assert.True(Property(Element(parameters, 0), "required").GetValue<bool>());
		Assert.False(Property(Element(parameters, 1), "required").GetValue<bool>());
		Assert.Equal("integer", Property(Property(Property(method, "result"), "schema"), "type").GetValue<string>());
		Assert.Equal("http://json-schema.org/draft-07/schema#", Property(Property(Property(method, "result"), "schema"), "$schema").GetValue<string>());
		Assert.Equal("integer", Property(Property(Element(parameters, 0), "schema"), "type").GetValue<string>());
		Assert.Equal("http://json-schema.org/draft-07/schema#", Property(Property(Element(parameters, 0), "schema"), "$schema").GetValue<string>());
		Assert.Equal("http://json-schema.org/draft-07/schema#", Property(Property(Element(parameters, 1), "schema"), "$schema").GetValue<string>());
		JsonObject discovery = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(document["methods"]!).Where(item => item?["name"]?.GetValue<string>() == "rpc.discover")));
		Assert.Empty(Assert.IsType<JsonArray>(discovery["params"]));
		Assert.Equal("http://json-schema.org/draft-07/schema#", Property(Property(Property(discovery, "result"), "schema"), "$schema").GetValue<string>());
		Assert.Equal(JsonRpcErrorCode.InvalidParams, Property(Element(Property(method, "errors"), 0), "code").GetValue<int>());
	}

	[Test]
	public async Task RequestIdParameterSchemaMatchesJsonDecoderWireValues()
	{
		(IDuplexPipe serverPipe, IDuplexPipe clientPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc server = new(CreateChannel(serverPipe, JsonRpcEncoding.Json));
		await using JsonRpcPipeChannel clientChannel = CreateChannel(clientPipe, JsonRpcEncoding.Json);
		server.AddRpcTarget(new OpenRpcTarget());

		JsonObject document = server.GetOpenRpcDocument();
		JsonObject echoId = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(document["methods"]!).Where(item => item?["name"]?.GetValue<string>() == "echoId")));
		JsonArray acceptedTypes = Assert.IsType<JsonArray>(Property(Element(Property(echoId, "params"), 0), "schema")["type"]);
		Assert.Equal(new[] { "string", "integer", "null" }, acceptedTypes.Select(static type => type!.GetValue<string>()));

		server.Start();
		clientChannel.Start();
		foreach (string rawArgument in new[] { "\"string-id\"", "42", "null" })
		{
			JsonRpcRequest request = new()
			{
				Id = 1,
				Method = "echoId",
				Arguments = JsonRpcValue.FromJson(System.Text.Encoding.UTF8.GetBytes($"[{rawArgument}]")),
			};
			await clientChannel.Writer.WriteAsync(request, this.TimeoutToken);
			JsonRpcResult response = Assert.IsType<JsonRpcResult>(await clientChannel.Reader.ReadAsync(this.TimeoutToken));
			Assert.Equal(rawArgument.Trim('"'), JsonNode.Parse(response.Result.Bytes.Span)!.GetValue<string>());
		}
	}

	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	[Test]
	public async Task GetOpenRpcDocument_UsesConfiguredSerializerForParameterAndResult(JsonRpcEncoding encoding)
	{
		(IDuplexPipe serverPipe, IDuplexPipe clientPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc server = new(CreateChannel(serverPipe, encoding, snakeCase: true));
		await using JsonRpcPipeChannel clientChannel = CreateChannel(clientPipe, encoding, snakeCase: true);
		server.AddRpcTarget(new OpenRpcTarget());

		JsonObject method = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(server.GetOpenRpcDocument()["methods"]!).Where(item => item?["name"]?.GetValue<string>() == "echoPayload")));
		Assert.True(ContainsProperty(Property(Element(Property(method, "params"), 0), "schema"), "display_name"));
		Assert.True(ContainsProperty(Property(Property(method, "result"), "schema"), "display_name"));

		server.Start();
		clientChannel.Start();
		JsonRpcValue arguments;
		using (JsonRpcArgumentsBuilder argumentsBuilder = server.CreateArguments(named: false, count: 1))
		{
			argumentsBuilder.Add(null, new OpenRpcPayload { DisplayName = "configured" }, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.OpenRpcPayload);
			arguments = argumentsBuilder.Build();
		}

		await clientChannel.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = "echoPayload", Arguments = arguments }, this.TimeoutToken);
		JsonRpcResult response = Assert.IsType<JsonRpcResult>(await clientChannel.Reader.ReadAsync(this.TimeoutToken));
		JsonNode wireResult = encoding == JsonRpcEncoding.Json
			? JsonNode.Parse(response.Result.Bytes.Span)!
			: JsonNode.Parse(((MessagePackSerializerPlugin)clientChannel.Serializer).Serializer.ConvertToJson(response.Result.AsMessagePack()))!;
		Assert.Equal("configured", wireResult["display_name"]!.GetValue<string>());
	}

	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	[Test]
	public async Task RpcDiscover_ReturnsOpenRpcDocumentUsingConnectionEncoding(JsonRpcEncoding encoding)
	{
		(IDuplexPipe serverPipe, IDuplexPipe clientPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc server = new(CreateChannel(serverPipe, encoding));
		await using JsonRpcPipeChannel clientChannel = CreateChannel(clientPipe, encoding);
		server.AddRpcTarget(new OpenRpcTarget());
		server.Start();
		clientChannel.Start();

		await clientChannel.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = "rpc.discover" }, this.TimeoutToken);
		JsonRpcResult response = Assert.IsType<JsonRpcResult>(await clientChannel.Reader.ReadAsync(this.TimeoutToken));
		JsonNode document = encoding == JsonRpcEncoding.Json
			? JsonNode.Parse(response.Result.Bytes.Span)!
			: JsonNode.Parse(((MessagePackSerializerPlugin)clientChannel.Serializer).Serializer.ConvertToJson(response.Result.Bytes))!;
		Assert.Equal("1.4.1", document["openrpc"]!.GetValue<string>());
		JsonObject addMethod = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(document["methods"]!).Where(item => item?["name"]?.GetValue<string>() == "add")));
		Assert.Equal("http://json-schema.org/draft-07/schema#", Property(Property(Element(Property(addMethod, "params"), 0), "schema"), "$schema").GetValue<string>());
		Assert.Equal("http://json-schema.org/draft-07/schema#", Property(Property(Property(addMethod, "result"), "schema"), "$schema").GetValue<string>());
	}

	[Arguments(JsonRpcEncoding.Json, false, false)]
	[Arguments(JsonRpcEncoding.Json, false, true)]
	[Arguments(JsonRpcEncoding.Json, true, false)]
	[Arguments(JsonRpcEncoding.Json, true, true)]
	[Arguments(JsonRpcEncoding.MessagePack, false, false)]
	[Arguments(JsonRpcEncoding.MessagePack, false, true)]
	[Arguments(JsonRpcEncoding.MessagePack, true, false)]
	[Arguments(JsonRpcEncoding.MessagePack, true, true)]
	[Test]
	public async Task RpcDiscover_ValidatesParameters(JsonRpcEncoding encoding, bool named, bool extraArgument)
	{
		(IDuplexPipe serverPipe, IDuplexPipe clientPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc server = new(CreateChannel(serverPipe, encoding));
		await using JsonRpcPipeChannel clientChannel = CreateChannel(clientPipe, encoding);
		JsonRpcValue arguments;
		using (JsonRpcArgumentsBuilder builder = server.CreateArguments(named, extraArgument ? 1 : 0))
		{
			if (extraArgument)
			{
				builder.Add(named ? "dispatch" : null, 1, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.Int32);
			}

			arguments = builder.Build();
		}

		server.Start();
		clientChannel.Start();
		await clientChannel.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = "rpc.discover", Arguments = arguments }, this.TimeoutToken);
		JsonRpcMessage response = await clientChannel.Reader.ReadAsync(this.TimeoutToken);
		if (extraArgument)
		{
			Assert.Equal(JsonRpcErrorCode.InvalidParams, Assert.IsType<JsonRpcError>(response).Error.Code);
		}
		else
		{
			Assert.IsType<JsonRpcResult>(response);
		}
	}

	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	[Test]
	public async Task RpcDiscover_NotificationDoesNotSendResponse(JsonRpcEncoding encoding)
	{
		(IDuplexPipe serverPipe, IDuplexPipe clientPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc server = new(CreateChannel(serverPipe, encoding));
		await using JsonRpcPipeChannel clientChannel = CreateChannel(clientPipe, encoding);
		server.Start();
		clientChannel.Start();

		await clientChannel.Writer.WriteAsync(new JsonRpcRequest { Method = "rpc.discover" }, this.TimeoutToken);
		await clientChannel.Writer.WriteAsync(new JsonRpcRequest { Id = 2, Method = "rpc.discover" }, this.TimeoutToken);
		JsonRpcResult response = Assert.IsType<JsonRpcResult>(await clientChannel.Reader.ReadAsync(this.TimeoutToken));
		Assert.Equal(new RequestId(2), response.Id);
	}

	[Test]
	public async Task PublicRequestAndResponseTypes_AreOrdinaryApplicationValues()
	{
		(IDuplexPipe serverPipe, IDuplexPipe clientPipe) = FullDuplexStream.CreatePipePair();
		using JsonRpc server = new(CreateChannel(serverPipe, JsonRpcEncoding.MessagePack));
		await using JsonRpcPipeChannel clientChannel = CreateChannel(clientPipe, JsonRpcEncoding.MessagePack);
		server.AddRpcTarget(new OpenRpcTarget());
		JsonRpcValue arguments;
		using (JsonRpcArgumentsBuilder builder = server.CreateArguments(named: false, count: 1))
		{
			builder.Add(null, new JsonRpcRequest { Id = 99, Method = "applicationData" }, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.JsonRpcRequest);
			arguments = builder.Build();
		}

		server.Start();
		clientChannel.Start();
		await clientChannel.Writer.WriteAsync(new JsonRpcRequest { Id = 1, Method = "echoRequest", Arguments = arguments }, this.TimeoutToken);
		JsonRpcMessage message = await clientChannel.Reader.ReadAsync(this.TimeoutToken);
		Assert.False(message is JsonRpcError, (message as JsonRpcError)?.Error.Message);
		JsonRpcResult response = Assert.IsType<JsonRpcResult>(message);
		Assert.Equal(new RequestId(1), response.Id);
		JsonRpcResult applicationResult = clientChannel.Serializer.Deserialize(response.Result, PolyType.SourceGenerator.TypeShapeProvider_Nerdbank_JsonRpc_Tests.Default.JsonRpcResult, this.TimeoutToken);
		Assert.Equal(new RequestId(99), applicationResult.Id);
	}

	private static JsonNode Property(JsonNode? node, string name)
		=> Assert.IsType<JsonObject>(node)[name] ?? throw new InvalidOperationException($"Expected property '{name}'.");

	private static JsonNode Element(JsonNode? node, int index)
		=> Assert.IsType<JsonArray>(node)[index] ?? throw new InvalidOperationException($"Expected element at index {index}.");

	private static bool ContainsProperty(JsonNode? node, string propertyName)
		=> node switch
		{
			JsonObject jsonObject => jsonObject.ContainsKey(propertyName) || jsonObject.Any(property => ContainsProperty(property.Value, propertyName)),
			JsonArray jsonArray => jsonArray.Any(item => ContainsProperty(item, propertyName)),
			_ => false,
		};

	private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding, bool snakeCase = false)
		=> encoding == JsonRpcEncoding.Json
			? new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer { PropertyNamingPolicy = snakeCase ? Nerdbank.Json.JsonNamingPolicy.SnakeLowerCase : Nerdbank.Json.JsonNamingPolicy.CamelCase }, JsonRpcJsonFraming.NewlineDelimited)
			: new JsonRpcMessagePackChannel(pipe, serializer: new Nerdbank.MessagePack.MessagePackSerializer { PropertyNamingPolicy = snakeCase ? Nerdbank.MessagePack.MessagePackNamingPolicy.SnakeLowerCase : null });
}
