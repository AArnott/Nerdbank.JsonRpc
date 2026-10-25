// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType.Abstractions;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Captures generated contracts for UTF-8 JSON service connections.</summary>
public static class JsonRpcUtf8Descriptor
{
	/// <summary>Captures a contract's source-generated shape.</summary>
	/// <typeparam name="T">The primary service interface.</typeparam>
	/// <param name="moniker">The service identity.</param>
	/// <param name="shape">The generated contract shape.</param>
	/// <returns>The typed UTF-8 JSON descriptor.</returns>
	public static JsonRpcUtf8Descriptor<T> Create<T>(ServiceMoniker moniker, ITypeShape<T> shape)
		where T : class => new(moniker, shape);

#if NET
	/// <summary>Captures a contract's statically resolved shape.</summary>
	/// <typeparam name="T">The primary service interface.</typeparam>
	/// <param name="moniker">The service identity.</param>
	/// <returns>The typed UTF-8 JSON descriptor.</returns>
	public static JsonRpcUtf8Descriptor<T> Create<T>(ServiceMoniker moniker)
		where T : class, IShapeable<T> => new(moniker, T.GetTypeShape());

	/// <summary>Captures a contract using a source-generated witness.</summary>
	/// <typeparam name="T">The primary service interface.</typeparam>
	/// <typeparam name="TProvider">The source-generated shape provider.</typeparam>
	/// <param name="moniker">The service identity.</param>
	/// <returns>The typed UTF-8 JSON descriptor.</returns>
	public static JsonRpcUtf8Descriptor<T> Create<T, TProvider>(ServiceMoniker moniker)
		where T : class
		where TProvider : IShapeable<T> => new(moniker, TProvider.GetTypeShape());
#endif
}
