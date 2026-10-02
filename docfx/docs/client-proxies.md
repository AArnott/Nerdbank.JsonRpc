# Generated client proxies

The client proxy generator is included in the `Nerdbank.JsonRpc` NuGet package as a C# analyzer. It creates a typed implementation of an interface annotated with <xref:Nerdbank.JsonRpc.GenerateJsonRpcProxyAttribute>. It uses PolyType-generated method shapes for arguments and results. [Getting Started](getting-started.md) shows the contract declaration and how to call <xref:Nerdbank.JsonRpc.JsonRpc.Attach*> on a connection to obtain the proxy; you do not need to construct the generated implementation yourself.

## Choosing request return types

For new RPC interfaces whose callers normally await each operation once, prefer <xref:System.Threading.Tasks.ValueTask`1> for requests with a result and <xref:System.Threading.Tasks.ValueTask> for requests without one. Generated proxies return these awaitables directly rather than converting them to Tasks. <xref:System.Threading.Tasks.Task`1> and <xref:System.Threading.Tasks.Task> are sensible choices when callers routinely share operations, await them repeatedly, or compose many operations, including in batching-heavy applications. Internal completion-source pooling benefits both return types; any additional savings from ValueTask depend on the request path and should be measured for your workload.

Consume each returned ValueTask exactly once, normally by awaiting it directly. If you need to await the same operation multiple times, share it among consumers, or use <xref:System.Threading.Tasks.Task.WhenAll*?displayProperty=nameWithType>, convert it with <xref:System.Threading.Tasks.ValueTask`1.AsTask?displayProperty=nameWithType> once and reuse that Task. Alternatively, call <xref:System.Threading.Tasks.ValueTask`1.Preserve?displayProperty=nameWithType> once and reuse the returned ValueTask. Do not repeatedly await the original ValueTask or repeatedly convert it to a Task: it may be backed by a source that is recycled after consumption.

For [batched requests](batching.md#consuming-batched-request-results), queue all calls, send the batch, and only then await the saved results. Awaiting directly means consuming an operation once, not necessarily awaiting it immediately. Converting each ValueTask to a Task once is appropriate when using Task.WhenAll to observe all batch operations, including when one fails.

Discarding an awaitable does not cancel its request. Observe failures and dispose any returned resources as required by their contracts. Do not rely on whether a particular ValueTask is currently Task-backed or pooled: an unconsumed pooled awaitable may lose the opportunity for reuse, while repeated consumption is unsupported and can interfere with a recycled source.

This guidance applies to the ValueTasks returned by the connection and batch request APIs as well as generated proxies. It does not change the wire protocol, and client and server interfaces can use different supported asynchronous return types.

## Supported contracts

Generated proxy contracts must be `partial`, non-nested, non-generic interfaces. Their methods must be non-generic and may return `ValueTask<T>`, `Task<T>`, `ValueTask`, or `Task` for requests, `IAsyncEnumerable<T>` for streaming requests, or `void` for notifications. Generated proxies do not implement properties or events, so omit abstract properties and events from the contract (or provide default interface implementations). To receive remote event notifications, register methods that match their names and arguments; see [Events as notifications](events.md#receiving-the-notifications).

Parameters may not be optional, `params`, `ref`, `out`, or `in`. Method parameters and return values must have PolyType shapes so the connection's serializer can encode and decode them. A method may include one `CancellationToken`, which must be its final parameter and is not serialized as an argument. For requests, cancellation after sending propagates to the remote target using `$/cancelRequest`. Notifications have no request ID, so their token controls only local argument serialization and enqueueing. See [Cancellation](protocol-behavior.md#cancellation).

The proxy resolves its type-shape provider once and caches the shapes it needs. <xref:Nerdbank.JsonRpc.JsonRpc.Attach*> and <xref:Nerdbank.JsonRpc.JsonRpcBatch.Attach*> accept an optional immutable <xref:Nerdbank.JsonRpc.JsonRpcProxyOptions> record for per-proxy argument encoding:

[!code-csharp[](../../samples/cs/client-proxies.cs#proxy-options)]

Arguments are positional arrays by default; set <xref:Nerdbank.JsonRpc.JsonRpcProxyOptions.UseNamedArguments> to send named object/map arguments when attaching a proxy. The same generated contract can be attached in either mode, including to a batch. For example, attach `ICalculator` with the named setting:

[!code-csharp[](../../samples/cs/client-proxies.cs#named-arguments)]

Generated proxies stream arguments directly into a counted, disposable <xref:Nerdbank.JsonRpc.JsonRpcArgumentsBuilder>, with one cancellation token for the entire argument set. They use the connection's selected [serializer](encodings.md).

To implement multiple RPC interfaces with one proxy, define an annotated composite interface and attach that composite type. `Attach<IBase>()` uses only metadata on `IBase`; it does not search for composite proxies that happen to implement that base interface. Proxies can also be [attached to a batch](batching.md).

By default, generated proxies send requests under a transformed wire name (trailing `Async` suffix removed, then camelCased); see [Method name transforms](method-naming.md) for how to customize or bypass this.
