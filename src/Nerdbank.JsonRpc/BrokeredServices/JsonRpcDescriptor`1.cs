// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>A descriptor that captures the primary service shape and generated proxy support.</summary>
/// <typeparam name="T">The primary service interface.</typeparam>
public abstract record JsonRpcDescriptor<T> : JsonRpcDescriptor, IJsonRpcDescriptor<T>
	where T : class
{
	private readonly ITypeShape<T> shape;

	/// <summary>Initializes a new instance of the <see cref="JsonRpcDescriptor{T}"/> class.</summary>
	/// <param name="moniker">The service identity.</param>
	/// <param name="shape">The generated primary interface shape.</param>
	protected JsonRpcDescriptor(ServiceMoniker moniker, ITypeShape<T> shape)
		: base(moniker)
	{
		if (!typeof(T).IsInterface)
		{
			throw new ArgumentException("The service contract must be an interface.", nameof(shape));
		}

		this.shape = shape ?? throw new ArgumentNullException(nameof(shape));
	}

	/// <inheritdoc/>
	public virtual void AddRpcTarget(JsonRpc connection, T target, IReadOnlyList<ServiceInterface> additionalInterfaces)
	{
		Requires.NotNull(connection);
		Requires.NotNull(target);
		Requires.NotNull(additionalInterfaces);
		connection.AddRpcTarget(target, this.shape, this.TargetOptions);
		foreach (ServiceInterface additional in additionalInterfaces)
		{
			additional.AddRpcTarget(connection, target, this.TargetOptions);
		}
	}

	/// <inheritdoc/>
	public virtual T CreateProxy(JsonRpc connection, IReadOnlyList<ServiceInterface> additionalInterfaces, ServiceActivationOptions options = default)
	{
		Requires.NotNull(connection);
		Requires.NotNull(additionalInterfaces);
		if (options.ClientRpcTarget is { } callback)
		{
			(this.CallbackInterface ?? throw new InvalidOperationException("The descriptor has no callback contract.")).AddRpcTarget(connection, callback, this.TargetOptions);
		}

		return connection.Attach<T>(this.ProxyOptions with
		{
			EnabledInterfaces = additionalInterfaces.Select(static item => item.InterfaceType).ToArray(),
			Lifetime = connection,
		});
	}

	/// <inheritdoc/>
	public virtual T CreateLocalProxy(ServiceProxyContext context, IReadOnlyList<ServiceInterface> additionalInterfaces)
	{
		Requires.NotNull(context);
		Requires.NotNull(additionalInterfaces);
		return JsonRpcProxyFactoryAttribute.CreateLocal<T>(context, additionalInterfaces.Select(static item => item.InterfaceType).ToArray());
	}

	/// <inheritdoc/>
	public virtual T CreateLocalTarget(ServiceProxyContext context, IReadOnlyList<ServiceInterface> additionalInterfaces)
		=> this.CreateLocalProxy(context, additionalInterfaces);

	/// <inheritdoc/>
	protected sealed override void AddRpcTargetCore(JsonRpc connection, object target, IReadOnlyList<ServiceInterface> additionalInterfaces)
		=> this.AddRpcTarget(connection, (T)target, additionalInterfaces);

	/// <inheritdoc/>
	protected sealed override object CreateLocalTargetCore(ServiceProxyContext context, IReadOnlyList<ServiceInterface> additionalInterfaces)
		=> this.CreateLocalTarget(context, additionalInterfaces);
}
