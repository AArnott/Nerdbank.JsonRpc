// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Describes immutable settings shared by independent service connections.</summary>
public abstract record JsonRpcDescriptor : IJsonRpcDescriptor
{
	/// <summary>Initializes a new instance of the <see cref="JsonRpcDescriptor"/> class.</summary>
	/// <param name="moniker">The service identity.</param>
	protected JsonRpcDescriptor(ServiceMoniker moniker) => this.Moniker = moniker ?? throw new ArgumentNullException(nameof(moniker));

	/// <inheritdoc/>
	public ServiceMoniker Moniker
	{
		get;
		init => field = value ?? throw new ArgumentNullException(nameof(value));
	}

	/// <summary>Gets immutable connection policies.</summary>
	public JsonRpcOptions RpcOptions
	{
		get;
		init => field = value ?? throw new ArgumentNullException(nameof(value));
	} = JsonRpcOptions.Default;

	/// <summary>Gets client naming and argument conventions.</summary>
	public JsonRpcProxyOptions ProxyOptions
	{
		get;
		init => field = value ?? throw new ArgumentNullException(nameof(value));
	} = JsonRpcProxyOptions.Default;

	/// <summary>Gets server naming and event conventions.</summary>
	public JsonRpcTargetOptions TargetOptions
	{
		get;
		init => field = value ?? throw new ArgumentNullException(nameof(value));
	} = JsonRpcTargetOptions.Default;

	/// <summary>Gets options for a dedicated connection-owned multiplexer, or <see langword="null"/> to disable multiplexing.</summary>
	/// <value>The default is <see langword="null"/>.</value>
	/// <remarks>
	/// Set <see cref="MultiplexingStream.Options.ProtocolMajorVersion"/> to 3 for handshake-free connection setup.
	/// Version 3 or later is required, and both endpoints must agree on the protocol version and seeded channels.
	/// The options are captured as a frozen copy. Seeded channel 0 is reserved for RPC; applications may use any other configured seeded channels.
	/// If no seeded channels are configured, one is created for RPC. Configured channel indices are preserved.
	/// </remarks>
	public MultiplexingStream.Options? MultiplexingStreamOptions
	{
		get;
		init
		{
			if (value is { ProtocolMajorVersion: < 3 })
			{
				throw new ArgumentException("Multiplexing protocol version 3 or later is required.", nameof(value));
			}

			field = value?.GetFrozenCopy();
		}
	}

	/// <summary>Gets the captured client callback contract, if any.</summary>
	public ServiceInterface? CallbackInterface { get; init; }

	/// <summary>Gets known additional contracts, keyed by stable capability identifiers.</summary>
	/// <remarks>These are potential capabilities, not a declaration that every implementation supports them.</remarks>
	public IReadOnlyList<ServiceInterface> AdditionalInterfaces
	{
		get;
		init => field = ServiceInterface.CopyAndValidate(value, nameof(value));
	} = Array.Empty<ServiceInterface>();

#if NET
	/// <summary>Captures an additional source-generated contract.</summary>
	/// <typeparam name="TAdditional">The additional service interface.</typeparam>
	/// <param name="id">The stable capability identifier.</param>
	/// <returns>The contract registration metadata.</returns>
	public ServiceInterface<TAdditional> CreateInterface<TAdditional>(string id)
		where TAdditional : class, IShapeable<TAdditional> => ServiceInterface.Create(id, TAdditional.GetTypeShape());

	/// <summary>Captures an additional contract using a source-generated witness.</summary>
	/// <typeparam name="TAdditional">The additional interface.</typeparam>
	/// <typeparam name="TProvider">The source-generated shape provider.</typeparam>
	/// <param name="id">The stable capability identifier.</param>
	/// <returns>The contract registration metadata.</returns>
	public ServiceInterface<TAdditional> CreateInterface<TAdditional, TProvider>(string id)
		where TAdditional : class
		where TProvider : IShapeable<TAdditional> => ServiceInterface.Create(id, TProvider.GetTypeShape());
#endif

	/// <summary>Captures an additional contract with an explicit shape.</summary>
	/// <typeparam name="TAdditional">The additional service interface.</typeparam>
	/// <param name="id">The stable capability identifier.</param>
	/// <param name="interfaceShape">The source-generated shape.</param>
	/// <returns>The contract registration metadata.</returns>
	public ServiceInterface<TAdditional> CreateInterface<TAdditional>(string id, ITypeShape<TAdditional> interfaceShape)
		where TAdditional : class => ServiceInterface.Create(id, interfaceShape);

	/// <inheritdoc/>
	public virtual JsonRpc CreateConnection(IDuplexPipe pipe, ServiceRpcContext? context = null)
	{
		if (pipe is null)
		{
			throw new ArgumentNullException(nameof(pipe));
		}

		context ??= new();
		MultiplexingStream? multiplexer = null;
		JsonRpcPipeChannel? channel = null;
		try
		{
			if (this.MultiplexingStreamOptions is { } multiplexingOptions)
			{
				MultiplexingStream.Options options = new(multiplexingOptions);
				if (options.SeededChannels is [])
				{
					options.SeededChannels.Add(new());
				}

				multiplexer = MultiplexingStream.Create(pipe.AsStream(), options);
				pipe = multiplexer.AcceptChannel(0);
			}

			channel = this.CreateChannel(pipe);
			return new(channel, this.RpcOptions)
			{
				Logger = context.Logger,
				MultiplexingStream = multiplexer,
				OwnsMultiplexingStream = multiplexer is not null,
				JoinableTaskFactory = context.JoinableTaskFactory,
				SynchronizationContext = context.SynchronizationContext,
			};
		}
		catch
		{
			CleanupFailedConnectionAsync(channel, multiplexer, context.Logger).Forget();
			throw;
		}
	}

	void IJsonRpcDescriptor.AddRpcTargetCore(JsonRpc connection, object target, IReadOnlyList<ServiceInterface> additionalInterfaces)
		=> this.AddRpcTargetCore(connection, target, additionalInterfaces);

	object IJsonRpcDescriptor.CreateLocalTarget(ServiceProxyContext context, IReadOnlyList<ServiceInterface> additionalInterfaces)
		=> this.CreateLocalTargetCore(context, additionalInterfaces);

	/// <inheritdoc/>
	public virtual object? CreateCallbackProxy(JsonRpc connection)
		=> this.CallbackInterface?.CreateProxy(connection, this.ProxyOptions with { EnabledInterfaces = [], Lifetime = null });

	/// <inheritdoc/>
	public virtual object CreateLocalCallback(object target, ILogger<ServiceProxyContext>? logger = null)
	{
		ServiceInterface callback = this.CallbackInterface ?? throw new InvalidOperationException("The descriptor has no callback contract.");
		ServiceProxyContext context = ServiceProxyContext.Borrow(target, logger, this.RpcOptions);
		try
		{
			return callback.CreateLocalProxy(context);
		}
		catch
		{
			context.Dispose();
			throw;
		}
	}

	/// <summary>Creates the encoding-specific channel for a connection.</summary>
	/// <param name="pipe">The connected transport.</param>
	/// <returns>The channel that owns the transport.</returns>
	protected abstract JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe);

	/// <summary>Registers a service after its type has been erased for catalog storage.</summary>
	/// <param name="connection">The unstarted connection.</param>
	/// <param name="target">The service implementation.</param>
	/// <param name="additionalInterfaces">The supported optional contracts.</param>
	protected abstract void AddRpcTargetCore(JsonRpc connection, object target, IReadOnlyList<ServiceInterface> additionalInterfaces);

	/// <summary>Constructs a forwarding target after its contract type has been erased.</summary>
	/// <param name="context">The activation context.</param>
	/// <param name="additionalInterfaces">The supported optional contracts.</param>
	/// <returns>The forwarding target.</returns>
	protected abstract object CreateLocalTargetCore(ServiceProxyContext context, IReadOnlyList<ServiceInterface> additionalInterfaces);

	private static async Task CleanupFailedConnectionAsync(JsonRpcPipeChannel? channel, MultiplexingStream? multiplexer, ILogger logger)
	{
		try
		{
			if (channel is not null)
			{
				await channel.DisposeAsync().ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed service connection channel cleanup failed.");
		}

		try
		{
			if (multiplexer is not null)
			{
				await multiplexer.DisposeAsync().ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed service connection multiplexer cleanup failed.");
		}
	}
}
