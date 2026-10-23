// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.TestContracts;

internal sealed class LegacyBaseFactoryAttribute : JsonRpcProxyFactoryAttribute
{
	public override object CreateProxy(IJsonRpcClient client, JsonRpcProxyOptions options) => new LegacyBaseProxy();
}
