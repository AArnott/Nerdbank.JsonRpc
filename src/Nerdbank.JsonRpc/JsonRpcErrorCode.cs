// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

internal static class JsonRpcErrorCode
{
	internal const int ParseError = -32700;
	internal const int InvalidRequest = -32600;
	internal const int MethodNotFound = -32601;
	internal const int InvalidParams = -32602;
	internal const int InternalError = -32603;
}
