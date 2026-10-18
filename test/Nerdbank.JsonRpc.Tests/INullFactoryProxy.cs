// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

[NullProxyFactory]
internal interface INullFactoryProxy
{
}

internal sealed class NullProxyFactoryAttribute : JsonRpcProxyFactoryAttribute
{
	public override object CreateProxy(IJsonRpcClient client, JsonRpcProxyOptions options) => null!;
}
