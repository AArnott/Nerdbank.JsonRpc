# Generated client proxies

The client proxy generator is included in the `Nerdbank.JsonRpc` NuGet package as a C# analyzer. It creates a typed implementation of an interface annotated with <xref:Nerdbank.JsonRpc.GenerateJsonRpcProxyAttribute>. It uses PolyType-generated method shapes for arguments and results. [Getting Started](getting-started.md) shows the contract declaration and how to call <xref:Nerdbank.JsonRpc.JsonRpc.Attach*> on a connection to obtain the proxy; you do not need to construct the generated implementation yourself.

## Supported contracts

Generated proxy contracts must be `partial`, non-nested, non-generic interfaces. Their methods must be non-generic and may return `ValueTask<T>`, `Task<T>`, `ValueTask`, or `Task` for requests, `IAsyncEnumerable<T>` for streaming requests, or `void` for notifications. Properties and ordinary interface events are not remote method calls; see [Events as notifications](events.md) for supported event patterns.

Parameters may not be optional, `params`, `ref`, `out`, or `in`. Method parameters and return values must have PolyType shapes so the connection's serializer can encode and decode them. A method may include one `CancellationToken`, which must be its final parameter and is not serialized as an argument. For requests, cancellation after sending propagates to the remote target using `$/cancelRequest`. Notifications have no request ID, so their token controls only local argument serialization and enqueueing. See [Cancellation](protocol-behavior.md#cancellation).

The proxy resolves its type-shape provider once and caches the shapes it needs. <xref:Nerdbank.JsonRpc.JsonRpc.Attach*> and <xref:Nerdbank.JsonRpc.JsonRpcBatch.Attach*> accept an optional immutable <xref:Nerdbank.JsonRpc.JsonRpcProxyOptions> record for per-proxy argument encoding:

[!code-csharp[](../../samples/cs/client-proxies.cs#proxy-options)]

Arguments are positional arrays by default; set <xref:Nerdbank.JsonRpc.JsonRpcProxyOptions.UseNamedArguments> to send named object/map arguments when attaching a proxy. The same generated contract can be attached in either mode, including to a batch. For example, attach `ICalculator` with the named setting:

[!code-csharp[](../../samples/cs/client-proxies.cs#named-arguments)]

Generated proxies stream arguments directly into a counted, disposable <xref:Nerdbank.JsonRpc.JsonRpcArgumentsBuilder>, with one cancellation token for the entire argument set. They use the connection's selected [serializer](encodings.md).

To implement multiple RPC interfaces with one proxy, define an annotated composite interface and attach that composite type. `Attach<IBase>()` uses only metadata on `IBase`; it does not search for composite proxies that happen to implement that base interface. Proxies can also be [attached to a batch](batching.md).

By default, generated proxies send requests under a transformed wire name (trailing `Async` suffix removed, then camelCased); see [Method name transforms](method-naming.md) for how to customize or bypass this.
