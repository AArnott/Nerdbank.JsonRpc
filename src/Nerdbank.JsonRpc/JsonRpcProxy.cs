// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.JsonRpc.BrokeredServices;

namespace Nerdbank.JsonRpc;

/// <summary>Provides lifecycle and capability behavior shared by generated RPC and direct proxies.</summary>
public abstract class JsonRpcProxy : IClientProxy, IDisposable, IAsyncDisposable
{
	private readonly Type primaryInterface;
	private readonly IReadOnlyList<Type>? enabledInterfaces;
	private readonly bool ownsLifetime;
	private IDisposable? lifetime;

	/// <summary>Initializes a new instance of the <see cref="JsonRpcProxy"/> class for direct calls.</summary>
	/// <param name="context">The target and policy context.</param>
	/// <param name="primaryInterface">The always-enabled primary contract.</param>
	/// <param name="enabledInterfaces">The additional enabled contracts.</param>
	protected JsonRpcProxy(ServiceProxyContext context, Type primaryInterface, IReadOnlyList<Type> enabledInterfaces)
	{
		this.LocalContext = context ?? throw new ArgumentNullException(nameof(context));
		this.primaryInterface = primaryInterface ?? throw new ArgumentNullException(nameof(primaryInterface));
		this.enabledInterfaces = enabledInterfaces ?? throw new ArgumentNullException(nameof(enabledInterfaces));
		this.Client = null!;
		this.MethodNameTransform = CommonMethodNameTransforms.Default;
	}

	/// <summary>Initializes a new instance of the <see cref="JsonRpcProxy"/> class for RPC calls.</summary>
	/// <param name="client">The RPC invocation client.</param>
	/// <param name="options">The proxy options.</param>
	/// <param name="primaryInterface">The always-enabled primary contract.</param>
	protected JsonRpcProxy(IJsonRpcClient client, JsonRpcProxyOptions options, Type primaryInterface)
	{
		if (options is null)
		{
			throw new ArgumentNullException(nameof(options));
		}

		this.Client = client ?? throw new ArgumentNullException(nameof(client));
		this.primaryInterface = primaryInterface ?? throw new ArgumentNullException(nameof(primaryInterface));
		this.enabledInterfaces = options.EnabledInterfaces;
		this.lifetime = options.Lifetime;
		this.ownsLifetime = options.Lifetime is not null;
		this.UseNamedArguments = options.UseNamedArguments;
		this.MethodNameTransform = options.MethodNameTransform;
	}

	/// <inheritdoc/>
	JsonRpc? IClientProxy.JsonRpc => this.LocalContext is null ? this.Client.JsonRpc : null;

	/// <summary>Gets the context for recognizing objects returning across their original local boundary.</summary>
	internal ServiceProxyContext? ProxyContext => this.LocalContext;

	/// <summary>Gets the RPC invocation client when this proxy uses RPC.</summary>
	protected IJsonRpcClient Client { get; }

	/// <summary>Gets a value indicating whether RPC arguments are named.</summary>
	protected bool UseNamedArguments { get; }

	/// <summary>Gets the transform used to compute RPC method names.</summary>
	protected Func<string, string> MethodNameTransform { get; }

	/// <summary>Gets the direct-call context, or null when this proxy uses RPC.</summary>
	protected ServiceProxyContext? LocalContext { get; }

	/// <summary>Gets the target context used by generated forwarding methods.</summary>
	protected ServiceProxyContext Context => this.LocalContext!;

	/// <inheritdoc/>
	public bool Is(Type type)
	{
		if (type is null)
		{
			throw new ArgumentNullException(nameof(type));
		}

		if (!type.IsInterface || type == typeof(IClientProxy) || !type.IsInstanceOfType(this))
		{
			return false;
		}

		if (this.LocalContext is { } context && !context.IsInterfaceEnabled(type))
		{
			return false;
		}

		if (type.IsAssignableFrom(this.primaryInterface))
		{
			return true;
		}

		if (this.LocalContext is null && this.Client is IClientProxy remoteProxy && !remoteProxy.Is(type))
		{
			return false;
		}

		if (this.enabledInterfaces is null)
		{
			return true;
		}

		foreach (Type enabledInterface in this.enabledInterfaces)
		{
			if (type.IsAssignableFrom(enabledInterface))
			{
				return true;
			}
		}

		return false;
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		if (!this.TryDispose())
		{
			this.DisposeUnowned();
		}
	}

	/// <inheritdoc/>
	public async ValueTask DisposeAsync()
	{
		if (this.LocalContext is { } context)
		{
			await context.DisposeAsync().ConfigureAwait(false);
		}
		else if (this.ownsLifetime)
		{
			IDisposable? resource = Interlocked.Exchange(ref this.lifetime, null);
			if (resource is IAsyncDisposable asynchronous)
			{
				await asynchronous.DisposeAsync().ConfigureAwait(false);
			}
			else
			{
				resource?.Dispose();
			}
		}
		else
		{
			this.DisposeUnowned();
		}
	}

	/// <summary>Disposes the owned resource or direct-call context, if present.</summary>
	/// <returns>Whether disposal was handled without a remote call.</returns>
	protected bool TryDispose()
	{
		if (this.LocalContext is { } context)
		{
			context.Dispose();
			return true;
		}

		if (!this.ownsLifetime)
		{
			return false;
		}

		Interlocked.Exchange(ref this.lifetime, null)?.Dispose();
		return true;
	}

	/// <summary>Invokes the contract's remote disposal operation when no local resource is owned.</summary>
	protected virtual void DisposeUnowned()
	{
	}
}
