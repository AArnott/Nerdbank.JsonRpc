# Host policy and extensibility

## Audience filtering

Set <xref:Nerdbank.JsonRpc.BrokeredServices.ServiceRegistration.Audience?displayProperty=nameWithType> through its constructor to control which clients may access a service before its implementation is loaded.
Every service must be registered before it can be proffered or discovered.
Registrations default to <xref:Nerdbank.JsonRpc.BrokeredServices.ServiceAudience.Local?displayProperty=nameWithType>.
Use <xref:Nerdbank.JsonRpc.BrokeredServices.ServiceAudience.Process?displayProperty=nameWithType> for services that must remain in-process, including contracts that are not RPC-compatible.
<xref:Nerdbank.JsonRpc.BrokeredServices.ServiceAudience.None?displayProperty=nameWithType> disables a service; `PublicSdk` alone does not grant access without a location flag.

<xref:Nerdbank.JsonRpc.BrokeredServices.IBrokeredServiceContainer.GetServiceBroker*?displayProperty=nameWithType> defaults to a `Local` filter.
Pass `ServiceAudience.Process` to also see process-only services.
Every flag in a view's filter is required on an exposed service: `Process` includes both process-only and machine-local services, whereas `Local` requires machine-local support.
Adding `PublicSdk` uniformly adds another requirement rather than widening access.
The retained numeric values are `None = 0`, `Process = 1`, `Local = 3`, and `PublicSdk = 0x10000000`.
A `None` view filter imposes no flag requirements, but still excludes services without a location audience.
This all-required-flags rule also extends to future audience flags.

[!code-csharp[](../../../samples/cs/brokered-services.cs#brokered-audience)]

Filtering applies to proxies, capability queries, channels, and availability notifications.
Pipe and connection acquisition additionally require `Local`, even through a `Process` or unfiltered view, so process-only services cannot accidentally be exposed over RPC.
Hosts should pass a `Local`-filtered view to IPC or multiplexing relays.
Audience filtering is a host exposure policy, not authentication or protection against callers that already possess an unrestricted container.

## Catalog and offering lifetimes

Disposing a proffer lease withdraws availability without removing catalog metadata or destroying services already issued to clients.
Register and unregister services on a live container using <xref:Nerdbank.JsonRpc.BrokeredServices.IBrokeredServiceContainer.Register*?displayProperty=nameWithType> and <xref:Nerdbank.JsonRpc.BrokeredServices.IBrokeredServiceContainer.Unregister*?displayProperty=nameWithType>.
These operations are thread-safe and update existing broker views through filtered availability notifications.
Unregistering also withdraws the current proffer, but leaves existing client activations alive.
Re-register to change the audience or supported optional interfaces; the new registration has its own loading lifetime.
Disposing an old proffer lease cannot withdraw a replacement registration's proffer.

[!code-csharp[](../../../samples/cs/brokered-service-hosting.cs#brokered-live-registration)]

See [on-demand proffering](on-demand.md) for catalog loading and replacement behavior.
Broker views are not retained by the container unless they have subscribers to <xref:Nerdbank.JsonRpc.BrokeredServices.IServiceBroker.AvailabilityChanged?displayProperty=nameWithType>.
Remove event handlers when finished; removing the final handler releases the container's reference to the view.
Adding availability handlers after the view or container is disposed is ignored without retaining the handler.

## Customization and diagnostics

Subclass <xref:Nerdbank.JsonRpc.BrokeredServices.BrokeredServiceContainer> to [wait for pending catalog registrations](on-demand.md#wait-for-registrations-that-arrive-later) through <xref:Nerdbank.JsonRpc.BrokeredServices.BrokeredServiceContainer.OnMissingServiceRegistrationAsync*>, supply external offering discovery through <xref:Nerdbank.JsonRpc.BrokeredServices.BrokeredServiceContainer.TryGetServiceOfferingAsync*>, descriptor policy through <xref:Nerdbank.JsonRpc.BrokeredServices.BrokeredServiceContainer.ApplyDescriptorSettings*>, connection infrastructure through <xref:Nerdbank.JsonRpc.BrokeredServices.BrokeredServiceContainer.CreateRpcContext*>, and dependency routing through <xref:Nerdbank.JsonRpc.BrokeredServices.BrokeredServiceContainer.GetDependencyBroker*>.
Use <xref:Nerdbank.JsonRpc.BrokeredServices.BrokeredServiceContainer.OnRequestHandled*> for request diagnostics and <xref:Nerdbank.JsonRpc.BrokeredServices.BrokeredServiceContainer.GetRegisteredServices*> for a snapshot of catalog identities, whether or not their implementations have been proffered.
Call <xref:Nerdbank.JsonRpc.BrokeredServices.BrokeredServiceContainer.NotifyAvailabilityChanged*> when host-managed lazy registrations change.
Discovery supplies only a descriptor and factory through the protected <xref:Nerdbank.JsonRpc.BrokeredServices.BrokeredServiceContainer.ServiceOffering.Create*?displayProperty=nameWithType> factory, which checks their contract types at compile time; exposure metadata always comes from the current registration.
Supply typed loggers or a logger factory for runtime diagnostics.

Discovery, audience policy, and dependency-injection integration remain host responsibilities.
The library does not provide authorization, protected operations, client credentials, or trusted/untrusted server modes.
The broker protocol is not compatible with existing ServiceHub peers.

## Structured request logs and distributed traces

The container, remote client broker, and IPC or multiplexing relays log each acquisition or capability query at information level, including successful and unavailable results.
Factory activation is a separate logged operation, so discovery and connection establishment can be distinguished from implementation behavior.
These diagnostics are built in; overriding the request hooks is not required.
Supply the application's logging infrastructure on both endpoints:

[!code-csharp[](../../../samples/cs/brokered-service-hosting.cs#brokered-diagnostics)]

Pass the connection context to <xref:Nerdbank.JsonRpc.BrokeredServices.RemoteServiceBroker.ConnectAsync*?displayProperty=nameWithType>.
Its logger factory creates the remote broker logger, while its typed logger records JSON-RPC connection diagnostics.
When adapting an already connected control contract, set <xref:Nerdbank.JsonRpc.BrokeredServices.RemoteServiceBroker.Logger?displayProperty=nameWithType> on the returned broker.
Configure <xref:Nerdbank.JsonRpc.BrokeredServices.IpcRelayServiceBroker.Logger?displayProperty=nameWithType> or <xref:Nerdbank.JsonRpc.BrokeredServices.MultiplexingRelayServiceBroker.Logger?displayProperty=nameWithType> on the host as well.
Enable information-level logging for the `Nerdbank.JsonRpc.BrokeredServices` categories and retain structured properties and exceptions in your logging provider.

### Understand acquisition outcomes

Request events have IDs `1000` (`ServiceRequestStarted`), `1001` (`ServiceRequestCompleted`), and `1002` (`ServiceRequestFailed`).
Their structured fields include `ServiceMoniker`, `Operation`, `TraceId`, and `SpanId`; start events also include `ParentSpanId`, and terminal events include `Outcome` and `ElapsedMilliseconds`.
The logging scope carries the service identity, operation, and trace identifiers into nested logs.
Failures include the exception object, with its stack trace and inner exceptions; request cancellation is logged at information level rather than as an error.
Activation arguments and callback objects are not included in these request diagnostics.

| Outcome | Meaning |
| --- | --- |
| `Succeeded` | The requested proxy, connection, capability metadata, or activation was obtained. |
| `NoRegistration` | No catalog entry exists, including after waiting for pending registrations. |
| `AudienceMismatch` | The registration does not admit the requested audience, including the same-machine requirement for connections. |
| `NoProfferingFactory` | The registration exists, but loading and host discovery supplied no factory. |
| `RegistrationChanged` | The registration was removed or replaced while loading or discovery was pending. |
| `FactoryReturnedNull` | The activation factory declined the request by returning null. |
| `FactoryException` | The factory or its dependency-broker configuration threw; see the activation event's exception. |
| `InvalidServiceContract` | The activated instance does not implement a required contract. |
| `Canceled` | The operation stopped with cancellation while its request token was canceled. |

Other failures identify their stage, such as `RegistrationDiscoveryFailed`, `FactoryLoadingFailed`, `OfferingDiscoveryFailed`, `CallbackConfigurationFailed`, `ProxyConfigurationFailed`, or `ConnectionConfigurationFailed`.
An acquisition may report `ActivationFailed` while its child activation event gives the precise factory failure.
A remote client reports `RemoteUnavailable` when the host returns no connection or metadata, and a relay reports `BrokerUnavailable` when its underlying broker does so.
Follow the shared trace to the container's logs for the exact rejection reason; it is not sent to the client as additional protocol metadata.
Relays also log allocated endpoint request IDs, abandoned-request cancellation, and unclaimed-endpoint expiration.

For callback-enabled services, connection acquisition can succeed before activation completes.
The later activation has its own correlated result, including null returns and exceptions; connection success alone does not prove that the factory succeeded.

### Join client and host logs

Broker operations participate in <xref:Nerdbank.JsonRpc.JsonRpc.ActivitySource?displayProperty=nameWithType>, named `Nerdbank.JsonRpc`.
Subscribe with an activity listener or configure an OpenTelemetry tracer provider to collect that source in **each process**, and send both processes' logs and traces to the same observability backend.
Activities include service name, version, operation, and outcome tags, and exceptional failures are marked as errors.
Use a backend that correlates W3C `TraceId` and `SpanId` fields and displays parent-child spans, such as an OpenTelemetry-compatible trace viewer.
The library does not produce WCF XML trace files for `SvcTraceViewer.exe`.

The remote broker's control calls carry W3C `traceparent` and `tracestate` in the existing JSON-RPC envelope for both JSON and MessagePack.
A host restores that context before invoking its relay and container, and nested dependency acquisitions inherit it.
Thus, the client acquisition, its control RPC, the host relay, container lookup, and activation share a trace ID while retaining distinct span IDs.
Each request gets correlation context even without a tracing exporter, and incoming W3C context is preserved even when the host does not sample activities.
You can therefore join structured logs from independently running processes by `TraceId` without requiring every process to export spans.
Enable scope collection in providers that require it to correlate auxiliary lifecycle and cleanup logs as well.

An application's ambient W3C activity becomes the parent of acquisitions made within it.
Later calls on an acquired service follow the activity current at invocation time, not the already-completed acquisition span.
