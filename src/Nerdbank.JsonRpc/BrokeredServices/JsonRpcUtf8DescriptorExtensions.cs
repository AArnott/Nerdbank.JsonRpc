// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Provides generated-shape resolution for UTF-8 JSON descriptors on legacy targets.</summary>
public static class JsonRpcUtf8DescriptorExtensions
{
	extension(JsonRpcUtf8Descriptor)
	{
		/// <summary>Captures a contract's statically resolved shape.</summary>
		/// <typeparam name="T">The primary service interface.</typeparam>
		/// <param name="moniker">The service identity.</param>
		/// <returns>The typed UTF-8 JSON descriptor.</returns>
#if NET
		[EditorBrowsable(EditorBrowsableState.Never)]
		[Obsolete("Use the static factory method. Check that the service interface has [GenerateShape], or supply its source-generated shape explicitly.", error: true)]
#endif
		public static JsonRpcUtf8Descriptor<T> Create<T>(ServiceMoniker moniker)
			where T : class => new(moniker, TypeShapeResolver.ResolveDynamicOrThrow<T>());

		/// <summary>Captures a contract using a source-generated witness.</summary>
		/// <typeparam name="T">The primary service interface.</typeparam>
		/// <typeparam name="TProvider">The source-generated shape provider.</typeparam>
		/// <param name="moniker">The service identity.</param>
		/// <returns>The typed UTF-8 JSON descriptor.</returns>
#if NET
		[EditorBrowsable(EditorBrowsableState.Never)]
		[Obsolete("Use the static factory method. Check that the witness has a source-generated shape for the service interface, or pass an explicit shape.", error: true)]
#endif
		public static JsonRpcUtf8Descriptor<T> Create<T, TProvider>(ServiceMoniker moniker)
			where T : class => new(moniker, TypeShapeResolver.ResolveDynamicOrThrow<T, TProvider>());
	}
}
