// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using System.Reflection;
using Nerdbank.JsonRpc.BrokeredServices;

namespace Nerdbank.JsonRpc;

/// <summary>
/// A base class for source-generated attributes that create the client proxy for an RPC contract interface.
/// </summary>
/// <remarks>
/// The source generator emits a derived attribute for each generated proxy and applies it to the RPC contract interface.
/// This lets the proxy be created without constructor reflection, and works even when the proxy type is not accessible to the caller.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = true)]
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class JsonRpcProxyFactoryAttribute : Attribute
{
	/// <summary>Initializes a new instance of the <see cref="JsonRpcProxyFactoryAttribute"/> class for a base-only proxy.</summary>
	protected JsonRpcProxyFactoryAttribute()
		: this([])
	{
	}

	/// <summary>Initializes a new instance of the <see cref="JsonRpcProxyFactoryAttribute"/> class for an interface group.</summary>
	/// <param name="additionalInterfaces">The interfaces implemented in addition to the primary contract.</param>
	protected JsonRpcProxyFactoryAttribute(params Type[] additionalInterfaces)
	{
		this.AdditionalInterfaces = Array.AsReadOnly(additionalInterfaces);
	}

	/// <summary>Gets the interfaces implemented in addition to the primary contract.</summary>
	public IReadOnlyList<Type> AdditionalInterfaces { get; }

	/// <summary>
	/// Creates a new instance of the generated proxy.
	/// </summary>
	/// <param name="client">The client the proxy sends requests through.</param>
	/// <param name="options">Options controlling the proxy's behavior.</param>
	/// <returns>The new proxy, which implements the interface this attribute is applied to.</returns>
	public abstract object CreateProxy(IJsonRpcClient client, JsonRpcProxyOptions options);

	/// <summary>Creates the same generated proxy for direct calls.</summary>
	/// <param name="context">The target provider and boundary policy.</param>
	/// <param name="enabledInterfaces">The additional interfaces enabled for this activation.</param>
	/// <returns>The generated proxy.</returns>
	public abstract object CreateProxy(ServiceProxyContext context, IReadOnlyList<Type> enabledInterfaces);

	/// <summary>Selects a generated group covering every enabled direct-call contract.</summary>
	/// <typeparam name="T">The primary contract.</typeparam>
	/// <param name="context">The target context.</param>
	/// <param name="enabledInterfaces">The supported additional interfaces.</param>
	/// <returns>The generated proxy.</returns>
	internal static T CreateLocal<T>(ServiceProxyContext context, IReadOnlyList<Type> enabledInterfaces)
		where T : class
	{
		JsonRpcProxyFactoryAttribute? factory = Cache<T>.Factories.FirstOrDefault(candidate =>
			enabledInterfaces.All(type => type.IsAssignableFrom(typeof(T)) || candidate.AdditionalInterfaces.Any(type.IsAssignableFrom)));
		if (factory is null)
		{
			throw new NotSupportedException($"No generated proxy group covers '{typeof(T)}'. Add JsonRpcContract and a covering JsonRpcProxyInterfaceGroup.");
		}

		object? proxy = factory.CreateProxy(context, enabledInterfaces);
		return proxy is T typedProxy
			? typedProxy
			: throw new InvalidOperationException($"The generated proxy factory returned {(proxy is null ? "null" : $"an instance of '{proxy.GetType().FullName}'")} which does not implement requested interface '{typeof(T).FullName}'.");
	}

	/// <summary>Selects capabilities from a returned object's generated contract universe.</summary>
	/// <typeparam name="T">The primary contract.</typeparam>
	/// <param name="context">The retained target context.</param>
	/// <param name="target">The returned service object.</param>
	/// <returns>The generated proxy with only the target's capabilities enabled.</returns>
	internal static T CreateForTarget<T>(ServiceProxyContext context, T target)
		where T : class
	{
		Type[] enabled = Cache<T>.Factories.SelectMany(static factory => factory.AdditionalInterfaces).Distinct()
			.Where(type => target is IClientProxy proxy ? proxy.Is(type) : type.IsInstanceOfType(target)).ToArray();
		return CreateLocal<T>(context, enabled);
	}

	private static class Cache<T>
	{
		internal static readonly JsonRpcProxyFactoryAttribute[] Factories = typeof(T).GetCustomAttributes<JsonRpcProxyFactoryAttribute>(inherit: false)
			.OrderBy(static factory => factory.AdditionalInterfaces.Count).ToArray();
	}
}
