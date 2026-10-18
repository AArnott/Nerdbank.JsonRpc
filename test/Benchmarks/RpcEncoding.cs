// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Benchmarks;

/// <summary>Identifies the wire encoding used by a benchmark case.</summary>
public enum RpcEncoding
{
	/// <summary>UTF-8 JSON, newline-delimited.</summary>
	Json,

	/// <summary>Binary MessagePack.</summary>
	MessagePack,
}
