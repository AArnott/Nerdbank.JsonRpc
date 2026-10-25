# Lifetimes, caching, and resilience

## Proxy ownership

Each ordinary proxy acquisition owns its activation or remote connection.
Dispose the proxy when finished, preferring asynchronous disposal when available.
Dispose returned streams, subscriptions, and marshalable objects, and complete returned pipe ends, before awaiting proxy disposal.
The broker does not take ownership of caller-supplied callback targets.

The package includes [proxy ownership](../../analyzers/NBJSONRPC004.md) and [rental lifetime](../../analyzers/NBJSONRPC005.md) analyzers to help prevent resource leaks.
For example, a nullable proxy can be disposed in a local scope:

[!code-csharp[](../../../samples/cs/brokered-service-lifetimes.cs#brokered-dispose-with-cast)]

## Direct local invocation

The ordinary contract proxy also supports direct local calls; no additional broker-specific proxy or factory attribute is generated.
Its <xref:Nerdbank.JsonRpc.JsonRpcProxy> base class shares capability queries, connection access, and resource disposal.
RPC calls retain the normal serialization path; direct calls invoke the typed service without loopback transport, argument arrays, or serialization.
Ordinary local arguments and results retain reference identity rather than being cloned.
Nested application objects are shared, not recursively adapted or isolated.

Direct boundaries isolate observer, progress, and event-handler exceptions using <xref:Microsoft.Extensions.Logging.ILogger`1>.
Progress and call-scoped marshalable arguments expire when the invocation or returned enumeration ends.
Failed invocations revoke their borrowed callback boundaries.
Call-scoped marshalable objects and progress receivers cannot be returned as results.
Explicit-lifetime returned objects and streams retain their service generation until disposal; returned observers release their generation on termination or boundary disposal without owning the observer target.
Local generation leases keep those resources and in-flight invocations alive while their owner is retired.

Service failures become <xref:Nerdbank.JsonRpc.RemoteInvocationException>, while cancellation remains cancellation.
<xref:Nerdbank.JsonRpc.JsonRpcOptions.IncludeExceptionDetails?displayProperty=nameWithType> also controls local disclosure.
With details enabled, local failures retain the original inner exception and a bounded diagnostic snapshot.
They do not serialize exception payloads or reconstruct allowlisted exception types.

## Cached activations

<xref:Nerdbank.JsonRpc.BrokeredServices.ServiceBrokerClient> shares cached activations through independently disposable rentals.
Entries are keyed by descriptor instance and activation options; reuse the same descriptor instance to share an activation.
Keep the client for as long as the cache is useful and dispose each rental promptly.
Do not retain a rental's proxy after disposing the rental.
Availability changes evict affected cache entries while outstanding rentals retain their old activation.
Caller cancellation cancels that caller's wait, not another caller's shared activation.

[!code-csharp[](../../../samples/cs/brokered-services.cs#brokered-cache)]

## Resilient proxies

<xref:Nerdbank.JsonRpc.BrokeredServices.ResilientServiceProxy`1> exposes a stable proxy and reacquires backing services after availability changes or RPC disconnection.
Acquire it with <xref:Nerdbank.JsonRpc.BrokeredServices.ServiceBrokerExtensions.GetResilientProxyAsync*?displayProperty=nameWithType>; the descriptor supplies the service type through generic inference.
It migrates event subscriptions and restores observer subscriptions returned from supported subscription methods.
It does not replay arbitrary application calls.
Changes are reported as gained, lost, or replaced; replacement is a single transition rather than a lost/gained pair.
Lifecycle handlers run outside the refresh semaphore and may dispose the owner.
When no backing service is available, invocation throws <xref:Nerdbank.JsonRpc.BrokeredServices.BrokeredServiceUnavailableException>.

[!code-csharp[](../../../samples/cs/brokered-services.cs#brokered-resilience)]

## Aggregating brokers

<xref:Nerdbank.JsonRpc.BrokeredServices.ServiceBrokerAggregator> queries brokers in priority order.
Only an unavailable result causes fallthrough; faults and cancellation propagate.
The aggregate does not own its constituent brokers.
