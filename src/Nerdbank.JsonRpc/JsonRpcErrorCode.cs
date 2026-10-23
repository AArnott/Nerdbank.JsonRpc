// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>Well-known JSON-RPC error codes.</summary>
public enum JsonRpcErrorCode : long
{
	/// <summary>The server could not parse the request.</summary>
	ParseError = -32700,

	/// <summary>The request is not a valid JSON-RPC request object.</summary>
	InvalidRequest = -32600,

	/// <summary>The requested method does not exist or is unavailable.</summary>
	MethodNotFound = -32601,

	/// <summary>The request parameters are invalid.</summary>
	InvalidParams = -32602,

	/// <summary>An internal JSON-RPC error occurred.</summary>
	InternalError = -32603,

	/// <summary>An exception occurred while invoking a JSON-RPC method.</summary>
	InvocationError = -32000,

	/// <summary>A marshaled object or async enumerator identified by a token no longer exists.</summary>
	NoMarshaledObjectFound = -32001,

	/// <summary>The request was canceled.</summary>
	RequestCancelled = -32800,
}
