// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Simplifies proffering strongly typed factories that do not need the full activation context.</summary>
public static class BrokeredServiceContainerExtensions
{
	/// <summary>Proffers a parameterless asynchronous factory for an already registered service.</summary>
	/// <typeparam name="T">The primary service contract.</typeparam>
	/// <param name="container">The container.</param>
	/// <param name="descriptor">The server descriptor.</param>
	/// <param name="factory">The per-client factory.</param>
	/// <returns>The proffer lease.</returns>
	public static IDisposable Proffer<T>(this IBrokeredServiceContainer container, IJsonRpcDescriptor<T> descriptor, Func<ValueTask<T?>> factory)
		where T : class
	{
		if (container is null)
		{
			throw new ArgumentNullException(nameof(container));
		}

		if (factory is null)
		{
			throw new ArgumentNullException(nameof(factory));
		}

		return container.Proffer(descriptor, (_, _, _, _) => factory());
	}

	/// <summary>Proffers a cancellation-aware asynchronous factory for an already registered service.</summary>
	/// <typeparam name="T">The primary service contract.</typeparam>
	/// <param name="container">The container.</param>
	/// <param name="descriptor">The server descriptor.</param>
	/// <param name="factory">The per-client factory.</param>
	/// <returns>The proffer lease.</returns>
	public static IDisposable Proffer<T>(this IBrokeredServiceContainer container, IJsonRpcDescriptor<T> descriptor, Func<CancellationToken, ValueTask<T?>> factory)
		where T : class
	{
		if (container is null)
		{
			throw new ArgumentNullException(nameof(container));
		}

		if (factory is null)
		{
			throw new ArgumentNullException(nameof(factory));
		}

		return container.Proffer(descriptor, (_, _, _, cancellationToken) => factory(cancellationToken));
	}

	/// <summary>Proffers a synchronous factory for an already registered service.</summary>
	/// <typeparam name="T">The primary service contract.</typeparam>
	/// <param name="container">The container.</param>
	/// <param name="descriptor">The server descriptor.</param>
	/// <param name="factory">The per-client factory.</param>
	/// <returns>The proffer lease.</returns>
	public static IDisposable Proffer<T>(this IBrokeredServiceContainer container, IJsonRpcDescriptor<T> descriptor, Func<T?> factory)
		where T : class
	{
		if (container is null)
		{
			throw new ArgumentNullException(nameof(container));
		}

		if (factory is null)
		{
			throw new ArgumentNullException(nameof(factory));
		}

		return container.Proffer(descriptor, (_, _, _, _) => new(factory()));
	}
}
