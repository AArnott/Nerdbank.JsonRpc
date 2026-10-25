// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Owns a returned service object and the generation needed to use it.</summary>
/// <param name="target">The returned object.</param>
/// <param name="lease">The retained parent generation.</param>
/// <param name="disposeTarget">Whether this boundary owns the target rather than only its generation.</param>
internal sealed class RetainedServiceObject(object target, ServiceProxyLease lease, bool disposeTarget = true) : IAsyncDisposable
{
	/// <inheritdoc/>
	public async ValueTask DisposeAsync()
	{
		try
		{
			if (disposeTarget && !ReferenceEquals(target, lease.Target))
			{
				await ServiceLifetime.DisposeObjectAsync(target).ConfigureAwait(false);
			}
		}
		finally
		{
			lease.Dispose();
		}
	}
}
