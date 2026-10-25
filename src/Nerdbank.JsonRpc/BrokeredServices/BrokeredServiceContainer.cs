// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Immutable;
using System.Globalization;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>A programmatic, host-extensible container for per-client service activation.</summary>
public class BrokeredServiceContainer : IBrokeredServiceContainer, IDisposable
{
	private readonly object syncObject = new();
	private readonly Dictionary<ServiceMoniker, RegistrationEntry> registrations = [];
	private readonly Dictionary<ServiceMoniker, ResolvedOffering> offerings = [];
	private ILogger<BrokeredServiceContainer>? logger;
	private bool disposed;

	private event Action<ServiceMoniker?, ServiceAudience>? AvailabilityChanged;

	/// <summary>Gets the container logger.</summary>
	public ILogger<BrokeredServiceContainer> Logger
	{
		get => this.logger ??= this.LoggerFactory.CreateLogger<BrokeredServiceContainer>();
		init => this.logger = value ?? throw new ArgumentNullException(nameof(value));
	}

	/// <summary>Gets the factory for connection, local proxy and deferred activation loggers.</summary>
	public ILoggerFactory LoggerFactory { get; init; } = NullLoggerFactory.Instance;

	/// <summary>Gets the host's joinable task factory for shared service loading and RPC connections.</summary>
	public JoinableTaskFactory? JoinableTaskFactory { get; init; }

	/// <inheritdoc/>
	public virtual void Register(ServiceRegistration registration)
	{
		if (registration is null)
		{
			throw new ArgumentNullException(nameof(registration));
		}

		Action<ServiceMoniker?, ServiceAudience>? affected;
		lock (this.syncObject)
		{
			this.ThrowIfDisposed();
			if (this.registrations.ContainsKey(registration.Moniker))
			{
				throw new InvalidOperationException($"Service '{registration.Moniker}' is already registered.");
			}

			RegistrationEntry entry = new(this, registration);
			this.registrations.Add(registration.Moniker, entry);
			affected = this.AvailabilityChanged;
		}

		affected?.Invoke(registration.Moniker, registration.Audience);
	}

	/// <inheritdoc/>
	public virtual bool Unregister(ServiceMoniker moniker)
	{
		if (moniker is null)
		{
			throw new ArgumentNullException(nameof(moniker));
		}

		Action<ServiceMoniker?, ServiceAudience>? affected;
		RegistrationEntry registration;
		lock (this.syncObject)
		{
			this.ThrowIfDisposed();
			if (!this.registrations.TryGetValue(moniker, out registration!))
			{
				return false;
			}

			this.registrations.Remove(moniker);
			this.offerings.Remove(moniker);
			affected = this.AvailabilityChanged;
		}

		affected?.Invoke(moniker, registration.Metadata.Audience);
		return true;
	}

	/// <inheritdoc/>
	public virtual IDisposable Proffer<T>(IJsonRpcDescriptor<T> descriptor, BrokeredServiceFactory<T> factory)
		where T : class
		=> this.ProfferCore(ServiceOffering.Create(descriptor, factory), expectedRegistration: null);

	/// <inheritdoc/>
	public IServiceBroker GetServiceBroker(ServiceAudience audience = ServiceAudience.Local)
	{
		ServiceAudienceFilter.Validate(audience);
		lock (this.syncObject)
		{
			this.ThrowIfDisposed();
			return new BrokerView(this, audience);
		}
	}

	/// <summary>Gets a snapshot of currently registered identities for host diagnostics.</summary>
	/// <returns>The registered service identities.</returns>
	public IReadOnlyList<ServiceMoniker> GetRegisteredServices()
	{
		lock (this.syncObject)
		{
			this.ThrowIfDisposed();
			return this.registrations.Keys.ToArray();
		}
	}

	/// <inheritdoc/>
	public void Dispose()
	{
		Action<ServiceMoniker?, ServiceAudience>? affected;
		lock (this.syncObject)
		{
			if (this.disposed)
			{
				return;
			}

			this.disposed = true;
			this.registrations.Clear();
			this.offerings.Clear();
			affected = this.AvailabilityChanged;
			this.AvailabilityChanged = null;
		}

		affected?.Invoke(null, ServiceAudience.None);
	}

	/// <summary>Allows a host to complete pending catalog registration before a missing service is rejected.</summary>
	/// <param name="moniker">The requested service that is not currently registered.</param>
	/// <param name="cancellationToken">Cancels this request's wait for registrations.</param>
	/// <returns>A task that completes when registrations relevant to this request are available.</returns>
	/// <remarks>
	/// The default implementation does nothing. Overrides may await pending catalog sources and call <see cref="Register"/>.
	/// This method runs outside the container lock, at most once per lookup, and may run concurrently for multiple requests.
	/// The catalog is checked again after completion; neither completion nor missing results are cached.
	/// Already registered services, including those excluded by audience policy, do not invoke this method.
	/// Hosts are responsible for sharing catalog loading and isolating its lifetime from individual request cancellation.
	/// </remarks>
	protected virtual ValueTask OnMissingServiceRegistrationAsync(ServiceMoniker moniker, CancellationToken cancellationToken) => default;

	/// <summary>Discovers externally supplied offerings using host-specific discovery.</summary>
	/// <param name="moniker">The requested service.</param>
	/// <param name="cancellationToken">Cancels lookup.</param>
	/// <returns>The host implementation for an already registered service, or null.</returns>
	protected virtual ValueTask<ServiceOffering?> TryGetServiceOfferingAsync(ServiceMoniker moniker, CancellationToken cancellationToken)
		=> new((ServiceOffering?)null);

	/// <summary>Allows a host to customize immutable descriptor settings by role.</summary>
	/// <param name="descriptor">The contract descriptor.</param>
	/// <param name="clientRole">Whether the descriptor is for the client.</param>
	/// <returns>The descriptor to use.</returns>
	protected virtual IJsonRpcDescriptor ApplyDescriptorSettings(IJsonRpcDescriptor descriptor, bool clientRole) => descriptor;

	/// <summary>Supplies runtime connection infrastructure, including typed logging.</summary>
	/// <param name="moniker">The requested service.</param>
	/// <param name="clientRole">Whether the connection is for the client.</param>
	/// <returns>Per-connection infrastructure.</returns>
	protected virtual ServiceRpcContext CreateRpcContext(ServiceMoniker moniker, bool clientRole)
		=> new() { Logger = this.LoggerFactory.CreateLogger<JsonRpc>(), LoggerFactory = this.LoggerFactory, JoinableTaskFactory = this.JoinableTaskFactory };

	/// <summary>Allows the host to scope the dependency broker supplied to a factory.</summary>
	/// <param name="broker">The requesting broker.</param>
	/// <param name="options">The activation context.</param>
	/// <returns>The dependency broker.</returns>
	protected virtual IServiceBroker GetDependencyBroker(IServiceBroker broker, ServiceActivationOptions options) => broker;

	/// <summary>Observes a completed request, including unavailable and failed activations.</summary>
	/// <remarks>For callback-enabled RPC, channel acquisition precedes activation completion. Later activation failures are logged separately.</remarks>
	/// <param name="moniker">The requested service.</param>
	/// <param name="exception">The failure, or null.</param>
	/// <param name="available">Whether an implementation was returned.</param>
	protected virtual void OnRequestHandled(ServiceMoniker moniker, Exception? exception, bool available)
	{
	}

	/// <summary>Notifies interested brokers that a lazily discovered service may have changed.</summary>
	/// <param name="moniker">The affected service identity.</param>
	protected void NotifyAvailabilityChanged(ServiceMoniker moniker)
	{
		if (moniker is null)
		{
			throw new ArgumentNullException(nameof(moniker));
		}

		ServiceAudience audience;
		Action<ServiceMoniker?, ServiceAudience>? snapshot;
		lock (this.syncObject)
		{
			this.ThrowIfDisposed();
			snapshot = this.AvailabilityChanged;
			if (!this.registrations.TryGetValue(moniker, out RegistrationEntry? registered))
			{
				throw new InvalidOperationException($"Service '{moniker}' is not registered.");
			}

			audience = registered.Metadata.Audience;
		}

		snapshot?.Invoke(moniker, audience);
	}

	private static async ValueTask DisposeTargetAsync(object target)
	{
		await ServiceLifetime.DisposeObjectAsync(target).ConfigureAwait(false);
	}

	private IDisposable ProfferCore(ServiceOffering implementation, RegistrationEntry? expectedRegistration)
	{
		IJsonRpcDescriptor descriptor = implementation.Descriptor;
		Action<ServiceMoniker?, ServiceAudience>? affected;
		ResolvedOffering offering;
		lock (this.syncObject)
		{
			this.ThrowIfDisposed();
			if (!this.registrations.TryGetValue(descriptor.Moniker, out RegistrationEntry? registration)
				|| (expectedRegistration is not null && !ReferenceEquals(registration, expectedRegistration)))
			{
				throw new InvalidOperationException($"Service '{descriptor.Moniker}' must have a current registration before proffering.");
			}

			if (this.offerings.ContainsKey(descriptor.Moniker))
			{
				throw new InvalidOperationException($"Service '{descriptor.Moniker}' is already proffered.");
			}

			offering = new(implementation, registration.Metadata);
			this.offerings.Add(descriptor.Moniker, offering);
			affected = this.AvailabilityChanged;
		}

		affected?.Invoke(descriptor.Moniker, offering.Registration.Audience);
		return new ProfferLease(this, offering);
	}

	private void ThrowIfDisposed()
	{
		if (this.disposed)
		{
			throw new ObjectDisposedException(nameof(BrokeredServiceContainer));
		}
	}

	private async Task<bool> LoadAsync(RegistrationEntry entry)
	{
		ServiceRegistration registration = entry.Metadata;
		try
		{
			if (registration.LoadAsync is { } loader)
			{
				await loader(registration.Moniker, new LoadingContainer(this, entry)).ConfigureAwait(false);
			}

			return true;
		}
		catch (Exception ex)
		{
			this.Logger.LogError(ex, "Service loading failed for {ServiceMoniker}.", registration.Moniker);
			throw;
		}
	}

	private async ValueTask<ResolvedOffering?> FindAsync(ServiceMoniker moniker, ServiceAudience audience, BrokeredServiceRequest request, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		RegistrationEntry registration;
		bool audienceMatches;
		bool registrationLookupRetried = false;
		while (true)
		{
			lock (this.syncObject)
			{
				this.ThrowIfDisposed();
				if (this.registrations.TryGetValue(moniker, out registration!))
				{
					audienceMatches = ServiceAudienceFilter.Matches(registration.Metadata.Audience, audience);
					if (audienceMatches && this.offerings.TryGetValue(moniker, out ResolvedOffering? offering))
					{
						return offering;
					}

					break;
				}

				if (registrationLookupRetried)
				{
					request.RejectionReason = "NoRegistration";
					return null;
				}
			}

			request.RejectionReason = "RegistrationDiscoveryFailed";
			await this.OnMissingServiceRegistrationAsync(moniker, cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			registrationLookupRetried = true;
		}

		if (!audienceMatches)
		{
			request.RejectionReason = "AudienceMismatch";
			this.Logger.LogInformation("Service {ServiceMoniker} audience {RegisteredAudience} does not include requested audience {RequestedAudience}.", moniker, registration.Metadata.Audience, audience);
			return null;
		}

		request.RejectionReason = "FactoryLoadingFailed";
		await registration.Loading.GetValueAsync(cancellationToken).ConfigureAwait(false);
		lock (this.syncObject)
		{
			this.ThrowIfDisposed();
			if (!this.registrations.TryGetValue(moniker, out RegistrationEntry? current) || !ReferenceEquals(current, registration))
			{
				request.RejectionReason = "RegistrationChanged";
				return null;
			}

			if (this.offerings.TryGetValue(moniker, out ResolvedOffering? offering))
			{
				return offering;
			}
		}

		request.RejectionReason = "OfferingDiscoveryFailed";
		ServiceOffering? discovered = await this.TryGetServiceOfferingAsync(moniker, cancellationToken).ConfigureAwait(false);
		lock (this.syncObject)
		{
			this.ThrowIfDisposed();
			if (!this.registrations.TryGetValue(moniker, out RegistrationEntry? current) || !ReferenceEquals(current, registration))
			{
				request.RejectionReason = "RegistrationChanged";
				return null;
			}

			if (this.offerings.TryGetValue(moniker, out ResolvedOffering? offering))
			{
				return offering;
			}

			if (discovered is null)
			{
				request.RejectionReason = "NoProfferingFactory";
				return null;
			}

			if (discovered.Descriptor.Moniker != moniker)
			{
				throw new InvalidOperationException("The discovered descriptor must match the requested moniker.");
			}

			return new(discovered, registration.Metadata);
		}
	}

	private async ValueTask<object?> ActivateAsync(ServiceMoniker moniker, ResolvedOffering offering, IServiceBroker broker, ServiceActivationOptions options, CancellationToken cancellationToken)
	{
		using BrokeredServiceRequest request = new(this.Logger, moniker, "Activate");
		CultureInfo previousCulture = CultureInfo.CurrentCulture;
		CultureInfo previousUICulture = CultureInfo.CurrentUICulture;
		try
		{
			if (options.ClientCulture is { } culture)
			{
				CultureInfo.CurrentCulture = culture;
			}

			if (options.ClientUICulture is { } uiCulture)
			{
				CultureInfo.CurrentUICulture = uiCulture;
			}

			request.RejectionReason = "FactoryException";
			object? target = await offering.Implementation.ActivateAsync(moniker, options, this.GetDependencyBroker(broker, options), cancellationToken).ConfigureAwait(false);
			request.RejectionReason = "FactoryReturnedNull";
			if (target is not null)
			{
				request.RejectionReason = "InvalidServiceContract";
				try
				{
					foreach (ServiceInterface additional in offering.AdditionalInterfaces)
					{
						if (!additional.InterfaceType.IsInstanceOfType(target))
						{
							throw new InvalidOperationException($"The activated service does not implement advertised contract '{additional.Id}'.");
						}
					}
				}
				catch
				{
					await DisposeTargetAsync(target).ConfigureAwait(false);
					throw;
				}
			}

			request.Complete(target is not null);
			return target;
		}
		catch (Exception ex)
		{
			request.Fail(ex, cancellationToken);
			throw;
		}
		finally
		{
			CultureInfo.CurrentCulture = previousCulture;
			CultureInfo.CurrentUICulture = previousUICulture;
		}
	}

	/// <summary>Describes a host-discovered implementation without registration policy.</summary>
	protected abstract class ServiceOffering
	{
		/// <summary>Initializes a new instance of the <see cref="ServiceOffering"/> class.</summary>
		/// <param name="descriptor">The server descriptor.</param>
		private ServiceOffering(IJsonRpcDescriptor descriptor)
		{
			this.Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
		}

		/// <summary>Gets the server descriptor.</summary>
		public IJsonRpcDescriptor Descriptor { get; }

		/// <summary>Captures a typed descriptor and its matching factory for host discovery.</summary>
		/// <typeparam name="T">The primary service contract.</typeparam>
		/// <param name="descriptor">The server descriptor.</param>
		/// <param name="factory">The per-client factory.</param>
		/// <returns>The discovered implementation.</returns>
		public static ServiceOffering Create<T>(IJsonRpcDescriptor<T> descriptor, BrokeredServiceFactory<T> factory)
			where T : class => new TypedOffering<T>(descriptor, factory ?? throw new ArgumentNullException(nameof(factory)));

		/// <summary>Activates an implementation after its contract type has been erased for catalog storage.</summary>
		/// <param name="moniker">The requested identity.</param>
		/// <param name="options">The client context.</param>
		/// <param name="broker">The dependency broker.</param>
		/// <param name="cancellationToken">Cancels activation.</param>
		/// <returns>The service implementation, or null.</returns>
		internal abstract ValueTask<object?> ActivateAsync(ServiceMoniker moniker, ServiceActivationOptions options, IServiceBroker broker, CancellationToken cancellationToken);

		private sealed class TypedOffering<T>(IJsonRpcDescriptor<T> descriptor, BrokeredServiceFactory<T> factory) : ServiceOffering(descriptor)
			where T : class
		{
			internal override async ValueTask<object?> ActivateAsync(ServiceMoniker moniker, ServiceActivationOptions options, IServiceBroker broker, CancellationToken cancellationToken)
				=> await factory(moniker, options, broker, cancellationToken).ConfigureAwait(false);
		}
	}

	private sealed class RegistrationEntry
	{
		/// <summary>Initializes a new instance of the <see cref="RegistrationEntry"/> class.</summary>
		/// <param name="container">The loader's owning container.</param>
		/// <param name="metadata">The immutable registration metadata.</param>
		internal RegistrationEntry(BrokeredServiceContainer container, ServiceRegistration metadata)
		{
			this.Metadata = metadata;
			this.Loading = new(() => container.LoadAsync(this), container.JoinableTaskFactory);
		}

		/// <summary>Gets the immutable exposure policy for this registration lifetime.</summary>
		internal ServiceRegistration Metadata { get; }

		/// <summary>Gets the shared loader for this registration lifetime.</summary>
		internal AsyncLazy<bool> Loading { get; }
	}

	private sealed record ResolvedOffering(ServiceOffering Implementation, ServiceRegistration Registration)
	{
		/// <summary>Gets the implementation's server descriptor.</summary>
		internal IJsonRpcDescriptor Descriptor => this.Implementation.Descriptor;

		/// <summary>Gets the supported contracts captured with this implementation.</summary>
		internal IReadOnlyList<ServiceInterface> AdditionalInterfaces { get; } = ResolveInterfaces(Implementation.Descriptor, Registration);

		private static IReadOnlyList<ServiceInterface> ResolveInterfaces(IJsonRpcDescriptor descriptor, ServiceRegistration registration)
		{
			Dictionary<string, ServiceInterface> known = descriptor.AdditionalInterfaces.ToDictionary(static contract => contract.Id, StringComparer.Ordinal);
			ServiceInterface[] supported = new ServiceInterface[registration.AdditionalInterfaceIds.Count];
			for (int i = 0; i < supported.Length; i++)
			{
				string id = registration.AdditionalInterfaceIds[i];
				if (!known.TryGetValue(id, out ServiceInterface? contract))
				{
					throw new InvalidOperationException($"Service '{registration.Moniker}' registers optional contract '{id}', but its server descriptor does not declare it.");
				}

				supported[i] = contract;
			}

			return Array.AsReadOnly(supported);
		}
	}

	private sealed class LoadingContainer(BrokeredServiceContainer container, RegistrationEntry registration) : IBrokeredServiceContainer
	{
		public void Register(ServiceRegistration metadata) => container.Register(metadata);

		public bool Unregister(ServiceMoniker moniker) => container.Unregister(moniker);

		public IDisposable Proffer<T>(IJsonRpcDescriptor<T> descriptor, BrokeredServiceFactory<T> factory)
			where T : class
		{
			if (descriptor is null)
			{
				throw new ArgumentNullException(nameof(descriptor));
			}

			return container.ProfferCore(ServiceOffering.Create(descriptor, factory), descriptor.Moniker == registration.Metadata.Moniker ? registration : null);
		}

		public IServiceBroker GetServiceBroker(ServiceAudience audience = ServiceAudience.Local) => container.GetServiceBroker(audience);
	}

	private sealed class ProfferLease(BrokeredServiceContainer container, ResolvedOffering offering) : IDisposable
	{
		private int disposed;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref this.disposed, 1) != 0)
			{
				return;
			}

			Action<ServiceMoniker?, ServiceAudience>? affected;
			ServiceAudience audience;
			lock (container.syncObject)
			{
				if (!container.offerings.TryGetValue(offering.Descriptor.Moniker, out ResolvedOffering? current)
					|| !ReferenceEquals(current, offering))
				{
					return;
				}

				audience = offering.Registration.Audience;
				container.offerings.Remove(offering.Descriptor.Moniker);
				affected = container.AvailabilityChanged;
			}

			affected?.Invoke(offering.Descriptor.Moniker, audience);
		}
	}

	private sealed class BrokerView(BrokeredServiceContainer container, ServiceAudience audience) : IServiceBroker, IDisposable
	{
		private readonly HashSet<ServiceMoniker> queried = [];
		private EventHandler<BrokeredServicesChangedEventArgs>? availabilityChanged;
		private bool disposed;

		public event EventHandler<BrokeredServicesChangedEventArgs>? AvailabilityChanged
		{
			add
			{
				lock (container.syncObject)
				{
					if (this.disposed || container.disposed)
					{
						return;
					}

					if (value is not null && this.availabilityChanged is null)
					{
						container.AvailabilityChanged += this.OnAvailabilityChanged;
					}

					this.availabilityChanged += value;
				}
			}

			remove
			{
				lock (container.syncObject)
				{
					this.availabilityChanged -= value;
					if (this.availabilityChanged is null)
					{
						container.AvailabilityChanged -= this.OnAvailabilityChanged;
					}
				}
			}
		}

		public async ValueTask<IReadOnlyList<string>?> GetCapabilitiesAsync(ServiceMoniker moniker, CancellationToken cancellationToken)
		{
			using BrokeredServiceRequest request = new(container.Logger, moniker, "GetCapabilities");
			try
			{
				this.Track(moniker);
				ResolvedOffering? offering = await container.FindAsync(moniker, audience, request, cancellationToken).ConfigureAwait(false);
				request.Complete(offering is not null);
				return offering?.AdditionalInterfaces.Select(static item => item.Id).ToArray();
			}
			catch (Exception ex)
			{
				request.Fail(ex, cancellationToken);
				throw;
			}
		}

		public async ValueTask<T?> GetProxyAsync<T>(IJsonRpcDescriptor<T> descriptor, ServiceActivationOptions options, CancellationToken cancellationToken)
			where T : class
		{
			if (descriptor is null)
			{
				throw new ArgumentNullException(nameof(descriptor));
			}

			using BrokeredServiceRequest request = new(container.Logger, descriptor.Moniker, "GetProxy");
			object? target = null;
			object? callback = null;
			ServiceProxyContext? context = null;
			try
			{
				this.Track(descriptor.Moniker);
				ResolvedOffering? offering = await container.FindAsync(descriptor.Moniker, audience, request, cancellationToken).ConfigureAwait(false);
				if (offering is null)
				{
					container.OnRequestHandled(descriptor.Moniker, null, false);
					request.Complete(false);
					return null;
				}

				request.RejectionReason = "CallbackConfigurationFailed";
				if (options.ClientRpcTarget is { } callbackTarget)
				{
					callback = offering.Descriptor.CreateLocalCallback(callbackTarget, container.LoggerFactory.CreateLogger<ServiceProxyContext>());
				}

				request.RejectionReason = "ActivationFailed";
				target = await container.ActivateAsync(descriptor.Moniker, offering, this, options with { ClientRpcTarget = callback }, cancellationToken).ConfigureAwait(false);
				if (target is null)
				{
					request.RejectionReason = "FactoryReturnedNull";
					container.OnRequestHandled(descriptor.Moniker, null, false);
					request.Complete(false);
					return null;
				}

				request.RejectionReason = "InvalidServiceContract";
				if (target is not T)
				{
					throw new InvalidOperationException($"The activated service does not implement requested contract '{typeof(T)}'.");
				}

				request.RejectionReason = "ProxyConfigurationFailed";
				IJsonRpcDescriptor<T> configured = container.ApplyDescriptorSettings(descriptor, clientRole: true) as IJsonRpcDescriptor<T>
					?? throw new InvalidOperationException("The host descriptor must preserve the typed proxy contract.");
				ILogger<ServiceProxyContext> logger = container.LoggerFactory.CreateLogger<ServiceProxyContext>();
				context = callback is null ? ServiceProxyContext.Own(target, logger: logger, rpcOptions: configured.RpcOptions)
					: ServiceProxyContext.OwnActivation(target, callback, logger, configured.RpcOptions);
				target = null;
				callback = null;
				ServiceInterface[] enabled = configured.AdditionalInterfaces.Where(known => offering.AdditionalInterfaces.Any(
					actual => actual.Id == known.Id && actual.InterfaceType == known.InterfaceType)).ToArray();
				T proxy = configured.CreateLocalProxy(context, enabled);
				options.ConfigureProxy?.Invoke(proxy);
				container.OnRequestHandled(descriptor.Moniker, null, true);
				request.Complete(true);
				context = null;
				return proxy;
			}
			catch (Exception ex)
			{
				request.Fail(ex, cancellationToken);
				if (context is not null)
				{
					await context.DisposeAsync().ConfigureAwait(false);
				}

				if (target is not null)
				{
					await DisposeTargetAsync(target).ConfigureAwait(false);
				}

				container.OnRequestHandled(descriptor.Moniker, ex, false);
				throw;
			}
			finally
			{
				if (callback is not null)
				{
					await ServiceLifetime.DisposeObjectAsync(callback).ConfigureAwait(false);
				}
			}
		}

		public async ValueTask<IDuplexPipe?> GetPipeAsync(ServiceMoniker moniker, ServiceActivationOptions options, CancellationToken cancellationToken)
			=> (await this.GetConnectionAsync(moniker, options, cancellationToken).ConfigureAwait(false))?.Pipe;

		public async ValueTask<BrokeredServiceConnection?> GetConnectionAsync(ServiceMoniker moniker, ServiceActivationOptions options, CancellationToken cancellationToken)
		{
			using BrokeredServiceRequest request = new(container.Logger, moniker, "GetConnection");
			try
			{
				this.Track(moniker);
				BrokeredServiceConnection? connection = await this.GetConnectionCoreAsync(moniker, options, request, cancellationToken).ConfigureAwait(false);
				container.OnRequestHandled(moniker, null, connection is not null);
				request.Complete(connection is not null);
				return connection;
			}
			catch (Exception ex)
			{
				request.Fail(ex, cancellationToken);
				container.OnRequestHandled(moniker, ex, false);
				throw;
			}
		}

		public void Dispose()
		{
			lock (container.syncObject)
			{
				this.disposed = true;
				container.AvailabilityChanged -= this.OnAvailabilityChanged;
				this.availabilityChanged = null;
				this.queried.Clear();
			}
		}

		private void OnAvailabilityChanged(ServiceMoniker? moniker, ServiceAudience changedAudience)
		{
			EventHandler<BrokeredServicesChangedEventArgs>? handlers;
			ImmutableHashSet<ServiceMoniker> impacted;
			lock (container.syncObject)
			{
				if (this.disposed)
				{
					return;
				}

				if (moniker is not null && (!ServiceAudienceFilter.Matches(changedAudience, audience) || !this.queried.Remove(moniker)))
				{
					return;
				}

				impacted = moniker is null ? ImmutableHashSet<ServiceMoniker>.Empty : ImmutableHashSet.Create(moniker);
				if (moniker is null)
				{
					this.queried.Clear();
				}

				handlers = this.availabilityChanged;
			}

			this.Raise(new(impacted, otherServicesImpacted: moniker is null), handlers);
		}

		private async ValueTask<BrokeredServiceConnection?> GetConnectionCoreAsync(ServiceMoniker moniker, ServiceActivationOptions options, BrokeredServiceRequest request, CancellationToken cancellationToken)
		{
			ResolvedOffering? offering = await container.FindAsync(moniker, audience | ServiceAudience.Local, request, cancellationToken).ConfigureAwait(false);
			if (offering is null)
			{
				return null;
			}

			request.RejectionReason = "ConnectionConfigurationFailed";
			(IDuplexPipe client, IDuplexPipe server) = FullDuplexStream.CreatePipePair();
			JsonRpc? rpc = null;
			object? target = null;
			try
			{
				IJsonRpcDescriptor descriptor = container.ApplyDescriptorSettings(offering.Descriptor, clientRole: false);
				ServiceRpcContext rpcContext = container.CreateRpcContext(moniker, clientRole: false);
				rpc = descriptor.CreateConnection(server, rpcContext);
				object? callback = descriptor.CreateCallbackProxy(rpc);
				DeferredServiceProxyContext? deferred = null;
				if (callback is null)
				{
					request.RejectionReason = "ActivationFailed";
					target = await container.ActivateAsync(moniker, offering, this, options, cancellationToken).ConfigureAwait(false);
					if (target is null)
					{
						request.RejectionReason = "FactoryReturnedNull";
						rpc.Dispose();
						await client.Input.CompleteAsync().ConfigureAwait(false);
						await client.Output.CompleteAsync().ConfigureAwait(false);
						return null;
					}
				}
				else
				{
					deferred = new(moniker, token => container.ActivateAsync(moniker, offering, this, options with { ClientRpcTarget = callback }, token))
					{
						Logger = container.LoggerFactory.CreateLogger<ServiceProxyContext>(),
						RpcOptions = descriptor.RpcOptions,
					};
					target = deferred;
				}

				request.RejectionReason = "ConnectionConfigurationFailed";
				object serverTarget = deferred is null ? target : descriptor.CreateLocalTarget(deferred, offering.AdditionalInterfaces);
				descriptor.AddRpcTargetCore(rpc, serverTarget, offering.AdditionalInterfaces);
				rpc.Start();
				this.ObserveConnectionAsync(rpc, target).Forget();
				if (deferred is not null)
				{
					this.ObserveActivationAsync(deferred, rpc).Forget();
				}

				return new(client, offering.AdditionalInterfaces.Select(static item => item.Id).ToArray());
			}
			catch
			{
				rpc?.Dispose();
				if (target is not null)
				{
					await DisposeTargetAsync(target).ConfigureAwait(false);
				}

				await client.Input.CompleteAsync().ConfigureAwait(false);
				await client.Output.CompleteAsync().ConfigureAwait(false);
				await server.Input.CompleteAsync().ConfigureAwait(false);
				await server.Output.CompleteAsync().ConfigureAwait(false);
				throw;
			}
		}

		private void Track(ServiceMoniker moniker)
		{
			if (moniker is null)
			{
				throw new ArgumentNullException(nameof(moniker));
			}

			lock (container.syncObject)
			{
				container.ThrowIfDisposed();
				if (this.disposed)
				{
					throw new ObjectDisposedException(nameof(IServiceBroker));
				}

				this.queried.Add(moniker);
			}
		}

		private void Raise(BrokeredServicesChangedEventArgs args, EventHandler<BrokeredServicesChangedEventArgs>? handlers)
		{
			if (handlers is not null)
			{
				foreach (EventHandler<BrokeredServicesChangedEventArgs> handler in handlers.GetInvocationList())
				{
					try
					{
						handler(this, args);
					}
					catch (Exception ex)
					{
						container.Logger.LogError(ex, "A service availability handler failed.");
					}
				}
			}
		}

		private async Task ObserveActivationAsync(DeferredServiceProxyContext context, JsonRpc rpc)
		{
			try
			{
				await context.InitializeAsync().ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				container.Logger.LogError(ex, "Service activation failed after callback dispatch started.");
				rpc.Dispose();
			}
		}

		private async Task ObserveConnectionAsync(JsonRpc rpc, object target)
		{
			try
			{
#pragma warning disable VSTHRD003 // Connection completion is driven by asynchronous transport dispatch, not caller context.
				await rpc.Completion.ConfigureAwait(false);
#pragma warning restore VSTHRD003
			}
			catch (Exception ex)
			{
				container.Logger.LogError(ex, "A service connection failed.");
			}
			finally
			{
				rpc.Dispose();
				try
				{
					await DisposeTargetAsync(target).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					container.Logger.LogError(ex, "Service connection cleanup failed.");
				}
			}
		}
	}
}
