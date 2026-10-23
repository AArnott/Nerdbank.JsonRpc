// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.TestContracts;

internal sealed class LegacyOptionalFactoryAttribute : JsonRpcOptionalProxyFactoryAttribute
{
	public LegacyOptionalFactoryAttribute()
		: base(1)
	{
	}

	public override object CreateProxy(IJsonRpcClient client, JsonRpcProxyOptions options) => new LegacyOptionalProxy();
}
