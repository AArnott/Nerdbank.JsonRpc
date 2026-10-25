// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Provides lifecycle and capability behavior shared by generated direct proxies.</summary>
public abstract class ServiceProxy : IClientProxy, IDisposable, IAsyncDisposable
{
	private readonly Type primaryInterface;
	private readonly IReadOnlyList<Type> enabledInterfaces;

	/// <summary>Initializes a new instance of the <see cref="ServiceProxy"/> class.</summary>
	/// <param name="context">The target and policy context.</param>
	/// <param name="primaryInterface">The always-enabled primary contract.</param>
	/// <param name="enabledInterfaces">The additional enabled contracts.</param>
	protected ServiceProxy(ServiceProxyContext context, Type primaryInterface, IReadOnlyList<Type> enabledInterfaces)
	{
		this.Context = context ?? throw new ArgumentNullException(nameof(context));
		this.primaryInterface = primaryInterface;
		this.enabledInterfaces = enabledInterfaces;
	}

	/// <summary>Gets a value indicating whether this proxy is disposed.</summary>
	public bool IsDisposed => this.Context.IsDisposed;

	/// <summary>Gets the context for recognizing objects returning across their original local boundary.</summary>
	internal ServiceProxyContext ProxyContext => this.Context;

	/// <summary>Gets the target context used by generated forwarding methods.</summary>
	protected ServiceProxyContext Context { get; }

	/// <inheritdoc/>
	public bool Is(Type type)
	{
		if (type is null)
		{
			throw new ArgumentNullException(nameof(type));
		}

		return type.IsInterface && type.IsInstanceOfType(this) && this.Context.IsInterfaceEnabled(type)
			&& (type.IsAssignableFrom(this.primaryInterface) || this.enabledInterfaces.Any(type.IsAssignableFrom));
	}

	/// <inheritdoc/>
	public virtual void Dispose() => this.Context.Dispose();

	/// <inheritdoc/>
	public virtual ValueTask DisposeAsync() => this.Context.DisposeAsync();
}
