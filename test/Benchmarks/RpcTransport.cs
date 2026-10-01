// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Benchmarks;

/// <summary>Selects the transport for a same-process RPC round trip.</summary>
public enum RpcTransport
{
	/// <summary>A connected pair of in-memory pipelines.</summary>
	InMemory,

	/// <summary>An operating-system named pipe, with both endpoints in this process.</summary>
	NamedPipe,
}
