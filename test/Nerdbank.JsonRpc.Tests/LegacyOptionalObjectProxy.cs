// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

internal sealed class LegacyOptionalObjectProxy : IOptionalObject, ISubtractCapability
{
	internal LegacyOptionalObjectProxy(IJsonRpcClient client, JsonRpcProxyOptions options)
	{
		this.Client = client;
		this.Options = options;
	}

	internal IJsonRpcClient Client { get; }

	internal JsonRpcProxyOptions Options { get; }

	public Task<int> GetValueAsync(CancellationToken cancellationToken) => Task.FromResult(10);

	public Task<int> CalculateAsync(int value, CancellationToken cancellationToken) => Task.FromResult(10 - value);

	public void Dispose()
	{
	}
}
