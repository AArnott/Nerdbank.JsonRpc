# On-demand registration and proffering

A host can publish a service catalog at startup without loading every implementation.
Register a <xref:Nerdbank.JsonRpc.BrokeredServices.ServiceRegistration> with a loader callback; the container invokes the callback when it needs the offering for a matching client request.
The loader proffers a factory, and the factory creates the requested client-owned instance.
These are separate lifetimes: **loading is shared**, while **activation is per acquisition**.

## Register catalog metadata

Catalog entries declare a moniker, an audience policy, the supported optional-interface IDs, and an optional loader.
The catalog need not reference contract or implementation types, even for services with optional interfaces.
See [serialized host metadata](optional-interfaces.md#register-from-serialized-host-metadata) for a manifest-driven registration example.
An application's loader can locate and load an implementation module before calling its proffering entry point.

[!code-csharp[](../../../samples/cs/brokered-services.cs#brokered-lazy-registration)]

The implementation entry point then supplies its descriptor and factory:

[!code-csharp[](../../../samples/cs/brokered-services.cs#brokered-lazy-proffer)]

This small example leaves the offering available until the container is disposed.
Retain the returned proffer lease if the host needs to withdraw the implementation earlier.
Withdrawing an offering does not destroy activations already issued to clients.

## A host that owns its proffer lease

The following host registers catalog metadata in its constructor but does not proffer a factory yet.
The first matching request invokes the loader, which saves the proffer lease.
The implementation types are in the same sample assembly for simplicity; a real module loader can defer loading their assembly.

[!code-csharp[](../../../samples/cs/brokered-service-hosting.cs#brokered-lazy-host)]

A client request triggers loading and activation without requiring a separate initialization call:

[!code-csharp[](../../../samples/cs/brokered-service-hosting.cs#brokered-lazy-client)]

Subsequent acquisitions reuse the loaded factory, not the service instance.
Each factory invocation constructs a new `Greeter`.
Dispose the host only after its clients have finished.

## Wait for registrations that arrive later

Lazy registration discovery is separate from lazy implementation loading.
Some catalog sources may be ready at startup while others require asynchronous discovery.
Override <xref:Nerdbank.JsonRpc.BrokeredServices.BrokeredServiceContainer.OnMissingServiceRegistrationAsync*?displayProperty=nameWithType> to wait for relevant pending registrations before an unregistered service is rejected.
The override may call <xref:Nerdbank.JsonRpc.BrokeredServices.BrokeredServiceContainer.Register*?displayProperty=nameWithType> itself, or await other host work that registers services.
After it completes, the container checks the catalog again and follows normal audience filtering, implementation loading, and activation.

[!code-csharp[](../../../samples/cs/brokered-service-hosting.cs#brokered-deferred-catalog)]

This example shares one deferred catalog load across requests using [AsyncLazy&lt;T&gt;](https://learn.microsoft.com/dotnet/api/microsoft.visualstudio.threading.asynclazy-1).
Only a request for an unregistered moniker starts or waits for that load; requests for already registered services proceed independently.
The callback can register metadata with per-service loaders, so completing the catalog need not load any implementations.
When using joinable tasks, supply the host's joinable task factory to this constructor, which passes it to the shared lazy initializer as well.

For several independent catalog sources, the override receives the requested moniker so the host can select only relevant sources rather than awaiting every source.
The hook runs outside the container lock and may be called concurrently; the host owns synchronization and sharing of its discovery work.
Cancellation should stop that request's wait without canceling discovery needed by other requests.
Exceptions propagate to the requesting caller rather than being converted into a missing service.

Each lookup invokes the hook at most once, and then returns unavailable if the registration is still absent.
Neither misses nor hook completion are cached by the container, allowing later catalog batches and live registration changes.
An existing registration never invokes this hook, even when its audience excludes the caller or no factory is available.
The synchronous <xref:Nerdbank.JsonRpc.BrokeredServices.BrokeredServiceContainer.GetRegisteredServices*?displayProperty=nameWithType> diagnostic API remains a snapshot and does not wait for pending registrations.
Proxy, pipe, connection, and capability requests all use the same registration-wait behavior.

## Concurrency, cancellation, and errors

Concurrent requests share one loader invocation per registration.
Canceling a request stops that caller's wait, not shared loading.
Loader failures are logged and propagated to waiting and subsequent callers.
The loader is not rerun after completion or failure, including after a proffer is withdrawn; the host may explicitly proffer a replacement.
Unregistering and re-registering creates a new loading lifetime.
Use the container passed to the loader when proffering: it is scoped to the registration being loaded, and rejects a late proffer if that registration was removed or replaced.
Requests still awaiting loading or discovery do not acquire an implementation from a removed registration.

[Audience filtering](hosting.md#audience-filtering) runs before loading, so an excluded client does not cause implementation code to load.
Capability queries may load an implementation to discover its supported contracts, but do not activate a service instance.
Hosts that use joinable tasks can set the container's <xref:Nerdbank.JsonRpc.BrokeredServices.BrokeredServiceContainer.JoinableTaskFactory> to preserve threading dependencies during shared loading.
