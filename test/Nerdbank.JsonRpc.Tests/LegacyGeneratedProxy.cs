// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class LegacyGeneratedProxy : ILegacyGeneratedProxyContract
{
	internal LegacyGeneratedProxy(IJsonRpcClient client, JsonRpcProxyOptions options)
	{
		this.Client = client;
		this.Options = options;
	}

	internal IJsonRpcClient Client { get; }

	internal JsonRpcProxyOptions Options { get; }
}
