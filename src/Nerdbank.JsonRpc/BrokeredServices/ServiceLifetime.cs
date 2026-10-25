// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Releases a service and its generated callback boundary together.</summary>
/// <param name="service">The service implementation.</param>
/// <param name="callback">The generated callback proxy.</param>
internal sealed class ServiceLifetime(object service, object callback) : IAsyncDisposable
{
	/// <inheritdoc/>
	public async ValueTask DisposeAsync()
	{
		try
		{
			await DisposeObjectAsync(service).ConfigureAwait(false);
		}
		finally
		{
			await DisposeObjectAsync(callback).ConfigureAwait(false);
		}
	}

	/// <summary>Releases an owned object using its asynchronous disposal contract when available.</summary>
	/// <param name="target">The owned object.</param>
	/// <returns>A task tracking cleanup.</returns>
	internal static async ValueTask DisposeObjectAsync(object target)
	{
		if (target is IAsyncDisposable asynchronous)
		{
			await asynchronous.DisposeAsync().ConfigureAwait(false);
		}
		else if (target is IDisposable disposable)
		{
			disposable.Dispose();
		}
	}
}
