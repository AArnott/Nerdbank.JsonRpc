// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Nerdbank.JsonRpc;
using Nerdbank.Streams;

public class PipeTransportTests() : TransportTestBase(CreateTransports())
{
	private static (JsonRpcTransport Alice, JsonRpcTransport Bob) CreateTransports()
	{
		(IDuplexPipe alice, IDuplexPipe bob) = FullDuplexStream.CreatePipePair();
		return (new PipeTransport(alice), new PipeTransport(bob));
	}
}
