// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Nerdbank.Streams;

[InheritsTests]
public class JsonRpcMessagePackSelfDelimitingChannelTests() : JsonRpcPipeChannelTestBase(CreateTransports())
{
	private static (JsonRpcPipeChannel Alice, JsonRpcPipeChannel Bob) CreateTransports()
	{
		(IDuplexPipe alice, IDuplexPipe bob) = FullDuplexStream.CreatePipePair();
		return (
			new JsonRpcMessagePackChannel(alice, LoggerFactory.CreateLogger<JsonRpcPipeChannel>(), JsonRpcMessagePackChannel.DefaultSerializer, JsonRpcMessagePackFraming.SelfDelimiting),
			new JsonRpcMessagePackChannel(bob, LoggerFactory.CreateLogger<JsonRpcPipeChannel>(), JsonRpcMessagePackChannel.DefaultSerializer, JsonRpcMessagePackFraming.SelfDelimiting));
	}
}
