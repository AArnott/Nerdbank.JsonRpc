# Generated client proxies

The client proxy generator is included in the `Nerdbank.JsonRpc` NuGet package as a C# analyzer. It creates a typed implementation of an interface annotated with <xref:Nerdbank.JsonRpc.JsonRpcContractAttribute>. It uses PolyType-generated method shapes for arguments and results. [Getting Started](getting-started.md) shows the contract declaration and how to call <xref:Nerdbank.JsonRpc.JsonRpc.Attach*> on a connection to obtain the proxy; you do not need to construct the generated implementation yourself.

## Choosing request return types

For new RPC interfaces whose callers normally await each operation once, prefer <xref:System.Threading.Tasks.ValueTask`1> for requests with a result and <xref:System.Threading.Tasks.ValueTask> for requests without one. Generated proxies return these awaitables directly rather than converting them to Tasks. <xref:System.Threading.Tasks.Task`1> and <xref:System.Threading.Tasks.Task> are sensible choices when callers routinely share operations, await them repeatedly, or compose many operations, including in batching-heavy applications. Internal completion-source pooling benefits both return types; any additional savings from ValueTask depend on the request path and should be measured for your workload.

On the .NET 8 and later library targets, the response-processing and notification-submission awaitables are pooled when they suspend. Generated ValueTask-returning proxies expose the same awaitables as the connection APIs; no extra proxy-level async wrapper is needed. Task-returning proxies convert them once to preserve Task semantics, which can require an adapter allocation. Synchronously completed notifications do not need a pooled state machine. Pool reuse and the allocation benefit depend on the workload and consumption pattern.

Consume each returned ValueTask exactly once, normally by awaiting it directly. If you need to await the same operation multiple times, share it among consumers, or use <xref:System.Threading.Tasks.Task.WhenAll*?displayProperty=nameWithType>, convert it with <xref:System.Threading.Tasks.ValueTask`1.AsTask?displayProperty=nameWithType> once and reuse that Task. Alternatively, call <xref:System.Threading.Tasks.ValueTask`1.Preserve?displayProperty=nameWithType> once and reuse the returned ValueTask. Do not repeatedly await the original ValueTask or repeatedly convert it to a Task: it may be backed by a source that is recycled after consumption.

For [batched requests](batching.md#consuming-batched-request-results), queue all calls, send the batch, and only then await the saved results. Awaiting directly means consuming an operation once, not necessarily awaiting it immediately. Converting each ValueTask to a Task once is appropriate when using Task.WhenAll to observe all batch operations, including when one fails.

Discarding an awaitable does not cancel its request. Response processing and encoded-payload cleanup still run without application consumption, but a pooled public awaitable that is never consumed cannot return its state machine to the pool; it can be collected when the operation and all references to it have ended. Observe failures and dispose any returned resources as required by their contracts. Do not rely on whether a particular ValueTask is currently Task-backed or pooled: an unconsumed pooled awaitable may lose the opportunity for reuse, while repeated consumption is unsupported and can interfere with a recycled source.

This guidance applies to the ValueTasks returned by the connection and batch request APIs as well as generated proxies. It does not change the wire protocol, and client and server interfaces can use different supported asynchronous return types.

## Supported contracts

<xref:Nerdbank.JsonRpc.JsonRpcContractAttribute> identifies the RPC contract and enables [contract analysis](../analyzers/index.md), including detection of colliding wire names.
It replaces the former `GenerateJsonRpcProxyAttribute`.
Set <xref:Nerdbank.JsonRpc.JsonRpcContractAttribute.GenerateProxy?displayProperty=nameWithType> to `false` to keep these checks without generating a client proxy:

[!code-csharp[](../../samples/cs/contract-validation.cs#target-only)]

Generated proxy contracts must be `partial`, non-nested, non-generic interfaces. Their methods must be non-generic and may return `ValueTask<T>`, `Task<T>`, `ValueTask`, or `Task` for requests, `IAsyncEnumerable<T>` for streaming requests, or `void` for notifications. Generated proxies also implement instance events with synchronous, void-returning delegate types and by-value, non-ref-like parameters, including inherited events.
Attach the proxy and subscribe before starting the connection to avoid losing early notifications; see [Events as notifications](events.md#receiving-the-notifications).
Generated proxies do not implement properties, so omit abstract properties from the contract (or provide default interface implementations).

Parameters may not be `params`, `ref`, `out`, or `in`. Method parameters and return values must have PolyType shapes so the connection's serializer can encode and decode them. A method may include one `CancellationToken`, which must be its final parameter and is not serialized as an argument. For requests, cancellation after sending propagates to the remote target using `$/cancelRequest`. Notifications have no request ID, so their token controls only local argument serialization and enqueueing. See [Cancellation](protocol-behavior.md#cancellation).

Optional parameters are supported, including a final cancellation token with `= default`.
When calling through the interface, the C# compiler supplies omitted arguments using the interface's defaults, and the proxy serializes those values like explicitly supplied arguments.
When an incoming request omits optional arguments, the server uses the defaults from the contract type registered with <xref:Nerdbank.JsonRpc.JsonRpc.AddRpcTarget*>.
Register the interface as the target contract to use its defaults, even if the implementation declares no defaults.

The proxy resolves its type-shape provider once and caches the shapes it needs.
Event argument binding and dispatch metadata is cached in static fields on the generated proxy class and shared across instances.
Each instance retains its own subscriptions and transformed event names; connections and serializers are not captured by the shared metadata. <xref:Nerdbank.JsonRpc.JsonRpc.Attach*> and <xref:Nerdbank.JsonRpc.JsonRpcBatch.Attach*> accept an optional immutable <xref:Nerdbank.JsonRpc.JsonRpcProxyOptions> record for per-proxy argument encoding:

[!code-csharp[](../../samples/cs/client-proxies.cs#proxy-options)]

Arguments are positional arrays by default; set <xref:Nerdbank.JsonRpc.JsonRpcProxyOptions.UseNamedArguments> to send named object/map arguments when attaching a proxy. The same generated contract can be attached in either mode, including to a batch. For example, attach `ICalculator` with the named setting:

[!code-csharp[](../../samples/cs/client-proxies.cs#named-arguments)]

Generated proxies stream arguments directly into a counted, disposable <xref:Nerdbank.JsonRpc.JsonRpcArgumentsBuilder>, with one cancellation token for the entire argument set. They use the connection's selected [serializer](encodings.md).

To implement multiple RPC interfaces with one proxy, define an annotated composite interface and attach that composite type, or declare interface groups as described below. `Attach<IBase>()` uses only metadata on `IBase`; it does not search for composite proxies that happen to implement that base interface. Proxies can also be [attached to a batch](batching.md).

By default, generated proxies send requests under a transformed wire name (trailing `Async` suffix removed, then camelCased); see [Method name transforms](method-naming.md) for how to customize or bypass this.

## Testing remote interface availability

Every newly generated proxy implements <xref:Nerdbank.JsonRpc.IClientProxy>, for root services, batches, and RPC-marshalable objects alike.
Use <xref:Nerdbank.JsonRpc.IClientProxy.Is(System.Type)> or <xref:Nerdbank.JsonRpc.JsonRpcExtensions.As``1(Nerdbank.JsonRpc.IClientProxy)> to query enabled contract interfaces rather than relying on a CLR cast when the generated proxy may expose more interfaces than the remote object supports.
The generator also emits `Is(Type)` and `As<T>()` extension methods directly on the contract interfaces and their optional interfaces, so callers need not cast to <xref:Nerdbank.JsonRpc.IClientProxy>.
These extensions use proxy capability metadata when present and ordinary CLR type checking for local implementations; null receivers return false or null.
The extension classes are internal to the contract assembly by default, matching StreamJsonRpc and avoiding conflicting extensions from referenced assemblies.
For a shared contract assembly whose consumers need these extensions, set the MSBuild property `PublicRpcMarshalableInterfaceExtensions` to `true` when compiling that assembly; accessors for public interfaces defined in that assembly then become public.
Accessors for interfaces imported from referenced assemblies remain internal to avoid ambiguous extension calls in downstream consumers.
Despite the property's name, it applies to both root and marshalable contract interfaces.
The queries are local: they do not send RPC requests, negotiate capabilities, or guard direct method calls.

For a root service, the code that creates the proxy can supply <xref:Nerdbank.JsonRpc.JsonRpcProxyOptions.EnabledInterfaces?displayProperty=nameWithType> using capabilities learned out of band.
Generate a composite contract or interface groups containing the potentially available interfaces, then list the interfaces enabled for that remote object when attaching it:

[!code-csharp[](../../samples/cs/client-proxies.cs#enabled-interfaces)]

The primary contract passed to `Attach<T>` and its inherited interfaces are always enabled; they do not need to appear in the list.
A null list (the default) enables all contract interfaces implemented by the selected proxy; an empty list enables only the primary contract and its inherited interfaces.
Use additional interface groups rather than inheritance for capabilities that may be unavailable on the remote service.
Each listed interface also enables its inherited interfaces, but listing separate base interfaces does not enable a composite derived interface.
An interface the proxy does not implement is never reported as enabled, even if listed.
The options copy the list when assigned, so later changes to the caller's collection do not affect the proxy.
The same options work with generic and non-generic attachment, including <xref:Nerdbank.JsonRpc.JsonRpcBatch.Attach*>.

[RPC-marshalable objects](rpc-marshalable-interfaces.md#optional-interfaces) use the peer's advertisement for capability queries independently of the generated proxy's CLR interfaces.
An explicit enabled-interface list in <xref:Nerdbank.JsonRpc.JsonRpcOptions.MarshaledProxy?displayProperty=nameWithType> can restrict those advertised optional capabilities further, but cannot disable the primary contract or its inherited interfaces.
Proxies compiled by an older generator, and application-written proxies, must implement the new interface themselves to support these queries.

## Discovering root-service capabilities

An application can define an `IDiscovery` RPC contract to learn the other peer's capabilities before attaching its main service proxy.
This is an application-level protocol, not a built-in discovery request: both peers agree that discovery and the baseline calculator service are always available.
Use stable, versioned capability identifiers rather than assembly-qualified CLR type names so the protocol can evolve independently of implementation details.

[!code-csharp[](../../samples/cs/discovery.cs#discovery-contract)]

This example uses the `IGroupedCalculator` and `IResettableCalculator` contracts from [Choosing generated interface groups](#choosing-generated-interface-groups).
The `calculator.reset.v1` identifier means that the peer offers the `IResettableCalculator` contract.
The serving peer registers the baseline service and discovery target, registering and advertising the optional interface only when its service implements it:

[!code-csharp[](../../samples/cs/discovery.cs#discovery-server)]

The discovery target returns the capability list captured during registration:

[!code-csharp[](../../samples/cs/discovery.cs#discovery-target)]

The calling peer first attaches a discovery proxy and starts its connection.
It maps recognized capability identifiers to local interface types, and attaches a second proxy on the same connection with <xref:Nerdbank.JsonRpc.JsonRpcProxyOptions.EnabledInterfaces?displayProperty=nameWithType>.
Unknown capability identifiers are ignored, allowing newer peers to advertise features older clients do not understand.

[!code-csharp[](../../samples/cs/discovery.cs#discovery-client)]

Both methods accept a newly constructed, unstarted connection whose lifetime remains owned by the caller.
The discovery call must complete before the configured service proxy can be created; cancellation and RPC failures propagate to the caller rather than being treated as an empty capability list.
The enabled interfaces are a snapshot for that proxy, so this protocol assumes the advertised capabilities remain valid for the connection's lifetime.
Discovery does not provide authorization or prevent direct calls to unadvertised methods.
Either peer can use this pattern to discover the other when it exposes the discovery contract.
For services with events, account for notifications that may arrive before the second proxy is attached; see [Events as notifications](events.md#receiving-the-notifications).

## Choosing generated interface groups

<xref:Nerdbank.JsonRpc.JsonRpcProxyInterfaceGroupAttribute> defines the additional interfaces to include alongside the annotated contract in one generated proxy class.
It follows StreamJsonRpc's source-generation policy:

- Without group attributes, generate a base-only proxy and, for a marshalable contract with optional interfaces, one proxy implementing all optional interfaces.
- With group attributes, generate only the distinct groups explicitly declared.
An empty group explicitly requests a base-only proxy; no full proxy is implicitly added.

[!code-csharp[](../../samples/cs/client-proxies.cs#proxy-interface-groups)]

For marshalable contracts, each additional interface in a group must be declared with <xref:Nerdbank.JsonRpc.RpcMarshalableOptionalInterfaceAttribute>.
For ordinary root contracts, groups can add other supported RPC interfaces without marshalable annotations.
Each additional interface requires generated PolyType method shapes for its argument and result types, as with a composite contract.
Grouped methods and events use their owning contract's shape provider, including when that contract is defined in a referenced assembly.
When an additional contract uses witness-generated shapes instead of implementing PolyType's shape-provider interface itself, its members use the primary contract's provider; ensure that provider includes the required shapes.

When attaching a root proxy, the enabled-interface list selects the smallest generated group covering the known enabled interfaces, including inherited capabilities; with a null list, the smallest generated group is selected.
When receiving a marshalable object, the advertised optional interfaces select the smallest covering group instead.
A marshalable group containing a derived optional interface also includes its declared optional base interfaces and their wire identifiers.
An exact match is preferred, but a superset is accepted, so `is` and direct casts can yield false positives; use the generated capability-query extensions.
If no group covers the required interfaces, proxy creation fails with an error identifying the missing group configuration.
This bounds the default output to two proxy classes rather than generating every combination.
