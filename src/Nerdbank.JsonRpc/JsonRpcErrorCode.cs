// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

public static class JsonRpcErrorCode
{
	public const int ParseError = -32700;
	public const int InvalidRequest = -32600;
	public const int MethodNotFound = -32601;
	public const int InvalidParams = -32602;
	public const int InternalError = -32603;

	/// <summary>A marshaled object or async enumerator identified by a token no longer exists.</summary>
	public const int NoMarshaledObjectFound = -32001;

	public const int RequestCancelled = -32800;
}
