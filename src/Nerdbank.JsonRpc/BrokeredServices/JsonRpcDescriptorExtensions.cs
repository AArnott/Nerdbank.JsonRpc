// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Provides generated-shape resolution on legacy targets.</summary>
public static class JsonRpcDescriptorExtensions
{
	extension(JsonRpcMessagePackDescriptor)
	{
		/// <summary>Captures a contract's statically resolved shape.</summary>
		/// <typeparam name="T">The primary service interface.</typeparam>
		/// <param name="moniker">The service identity.</param>
		/// <returns>The typed MessagePack descriptor.</returns>
#if NET
		[EditorBrowsable(EditorBrowsableState.Never)]
		[Obsolete("Use the static factory method. Check that the service interface has [GenerateShape], or supply its source-generated shape explicitly.", error: true)]
#endif
		public static JsonRpcMessagePackDescriptor<T> Create<T>(ServiceMoniker moniker)
			where T : class => new(moniker, TypeShapeResolver.ResolveDynamicOrThrow<T>());

		/// <summary>Captures a contract using a source-generated witness.</summary>
		/// <typeparam name="T">The primary service interface.</typeparam>
		/// <typeparam name="TProvider">The source-generated shape provider.</typeparam>
		/// <param name="moniker">The service identity.</param>
		/// <returns>The typed MessagePack descriptor.</returns>
#if NET
		[EditorBrowsable(EditorBrowsableState.Never)]
		[Obsolete("Use the static factory method. Check that the witness has a source-generated shape for the service interface, or pass an explicit shape.", error: true)]
#endif
		public static JsonRpcMessagePackDescriptor<T> Create<T, TProvider>(ServiceMoniker moniker)
			where T : class => new(moniker, TypeShapeResolver.ResolveDynamicOrThrow<T, TProvider>());
	}

	extension(ServiceInterface)
	{
		/// <summary>Captures an additional contract using its source-generated shape.</summary>
		/// <typeparam name="T">The additional interface.</typeparam>
		/// <param name="id">The stable capability identifier.</param>
		/// <returns>The typed contract metadata.</returns>
#if NET
		[EditorBrowsable(EditorBrowsableState.Never)]
		[Obsolete("Use the static factory method. Check that the additional interface has [GenerateShape], or supply its source-generated shape explicitly.", error: true)]
#endif
		public static ServiceInterface<T> Create<T>(string id)
			where T : class => ServiceInterface.Create(id, TypeShapeResolver.ResolveDynamicOrThrow<T>());

		/// <summary>Captures an additional contract using a source-generated witness.</summary>
		/// <typeparam name="T">The additional interface.</typeparam>
		/// <typeparam name="TProvider">The source-generated shape provider.</typeparam>
		/// <param name="id">The stable capability identifier.</param>
		/// <returns>The typed contract metadata.</returns>
#if NET
		[EditorBrowsable(EditorBrowsableState.Never)]
		[Obsolete("Use the static factory method. Check that the witness has a source-generated shape for the additional interface, or pass an explicit shape.", error: true)]
#endif
		public static ServiceInterface<T> Create<T, TProvider>(string id)
			where T : class => ServiceInterface.Create(id, TypeShapeResolver.ResolveDynamicOrThrow<T, TProvider>());
	}

	/// <summary>Captures an additional contract using its source-generated shape.</summary>
	/// <typeparam name="TAdditional">The additional interface.</typeparam>
	/// <param name="descriptor">The descriptor.</param>
	/// <param name="id">The stable capability identifier.</param>
	/// <returns>The contract registration metadata.</returns>
#if NET
	[EditorBrowsable(EditorBrowsableState.Never)]
	[Obsolete("Use the instance method. Check that the additional interface has [GenerateShape], or supply its source-generated shape explicitly.", error: true)]
#endif
	public static ServiceInterface<TAdditional> CreateInterface<TAdditional>(this JsonRpcDescriptor descriptor, string id)
		where TAdditional : class => (descriptor ?? throw new ArgumentNullException(nameof(descriptor))).CreateInterface(id, TypeShapeResolver.ResolveDynamicOrThrow<TAdditional>());

	/// <summary>Captures an additional contract using a generated witness shape.</summary>
	/// <typeparam name="TAdditional">The additional interface.</typeparam>
	/// <typeparam name="TProvider">The witness type.</typeparam>
	/// <param name="descriptor">The descriptor.</param>
	/// <param name="id">The stable capability identifier.</param>
	/// <returns>The captured contract.</returns>
#if NET
	[EditorBrowsable(EditorBrowsableState.Never)]
	[Obsolete("Use the instance method. Check that the witness has a source-generated shape for the additional interface, or pass an explicit shape.", error: true)]
#endif
	public static ServiceInterface<TAdditional> CreateInterface<TAdditional, TProvider>(this JsonRpcDescriptor descriptor, string id)
		where TAdditional : class => (descriptor ?? throw new ArgumentNullException(nameof(descriptor))).CreateInterface(id, TypeShapeResolver.ResolveDynamicOrThrow<TAdditional, TProvider>());
}
