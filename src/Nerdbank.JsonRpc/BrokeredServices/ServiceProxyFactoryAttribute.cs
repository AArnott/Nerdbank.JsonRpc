// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using System.Reflection;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Constructs a source-generated direct proxy for one interface group.</summary>
/// <remarks>This is infrastructure for generated code. No constructors are invoked through reflection.</remarks>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = true)]
[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class ServiceProxyFactoryAttribute : Attribute
{
	/// <summary>Initializes a new instance of the <see cref="ServiceProxyFactoryAttribute"/> class.</summary>
	/// <param name="additionalInterfaces">The additional interfaces implemented by the generated group.</param>
	protected ServiceProxyFactoryAttribute(params Type[] additionalInterfaces) => this.AdditionalInterfaces = Array.AsReadOnly(additionalInterfaces);

	/// <summary>Gets the additional generated interfaces.</summary>
	public IReadOnlyList<Type> AdditionalInterfaces { get; }

	/// <summary>Creates a generated forwarding proxy.</summary>
	/// <param name="context">The target provider and boundary policy.</param>
	/// <param name="enabledInterfaces">The interfaces enabled for this activation.</param>
	/// <returns>The generated proxy.</returns>
	public abstract object CreateProxy(ServiceProxyContext context, IReadOnlyList<Type> enabledInterfaces);

	/// <summary>Selects a generated group covering every enabled contract.</summary>
	/// <typeparam name="T">The primary service interface.</typeparam>
	/// <param name="context">The target context.</param>
	/// <param name="enabledInterfaces">The supported additional interfaces.</param>
	/// <returns>The generated proxy.</returns>
	internal static T Create<T>(ServiceProxyContext context, IReadOnlyList<Type> enabledInterfaces)
		where T : class
	{
		ServiceProxyFactoryAttribute? factory = Cache<T>.Factories.FirstOrDefault(candidate =>
			enabledInterfaces.All(type => type.IsAssignableFrom(typeof(T)) || candidate.AdditionalInterfaces.Any(type.IsAssignableFrom)));
		return factory is null
			? throw new NotSupportedException($"No generated service proxy group covers '{typeof(T)}'. Add JsonRpcContract and a covering JsonRpcProxyInterfaceGroup.")
			: (T)factory.CreateProxy(context, enabledInterfaces);
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
		return Create<T>(context, enabled);
	}

	private static class Cache<T>
	{
		internal static readonly ServiceProxyFactoryAttribute[] Factories = typeof(T).GetCustomAttributes<ServiceProxyFactoryAttribute>(inherit: false)
			.OrderBy(static factory => factory.AdditionalInterfaces.Count).ToArray();
	}
}
