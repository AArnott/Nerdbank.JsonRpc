// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

[JsonRpcProxyImplementation(typeof(LegacyJsonRpcConstructorProxy))]
internal interface ILegacyJsonRpcConstructorProxy
{
}

internal sealed class LegacyJsonRpcConstructorProxy : ILegacyJsonRpcConstructorProxy
{
	internal LegacyJsonRpcConstructorProxy(JsonRpc jsonRpc)
	{
		this.JsonRpc = jsonRpc;
	}

	internal JsonRpc JsonRpc { get; }
}
