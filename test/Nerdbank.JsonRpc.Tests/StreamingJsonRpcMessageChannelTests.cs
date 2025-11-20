// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Nerdbank.JsonRpc;
using Nerdbank.Streams;

public class StreamingJsonRpcMessageChannelTests() : JsonRpcPipeChannelTestBase(CreateTransports())
{
	private static (JsonRpcPipeChannel Alice, JsonRpcPipeChannel Bob) CreateTransports()
	{
		(IDuplexPipe alice, IDuplexPipe bob) = FullDuplexStream.CreatePipePair();
		ILogger<JsonRpcPipeChannel> aliceLogger = LoggerFactory.CreateLogger<JsonRpcPipeChannel>();
		ILogger<JsonRpcPipeChannel> bobLogger = LoggerFactory.CreateLogger<JsonRpcPipeChannel>();
		return (new StreamingJsonRpcMessageChannel(alice, aliceLogger), new StreamingJsonRpcMessageChannel(bob, bobLogger));
	}
}
