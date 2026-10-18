// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>Identifies the primitive kind of a top-level JSON-RPC envelope extension property.</summary>
internal enum TopLevelPropertyKind
{
	/// <summary>A string value.</summary>
	String,

	/// <summary>A signed 64-bit integer value.</summary>
	Int64,
}
