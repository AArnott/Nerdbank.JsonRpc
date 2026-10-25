// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

[NullProxyFactory]
[PolyType.GenerateShape]
internal partial interface INullFactoryProxy
{
}

internal sealed class NullProxyFactoryAttribute : JsonRpcProxyFactoryAttribute
{
	public override object CreateProxy(IJsonRpcClient client, JsonRpcProxyOptions options) => null!;

	public override object CreateProxy(Nerdbank.JsonRpc.BrokeredServices.ServiceProxyContext context, IReadOnlyList<Type> enabledInterfaces) => null!;
}
