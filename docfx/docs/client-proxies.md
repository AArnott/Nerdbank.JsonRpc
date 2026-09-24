# Generated client proxies

The experimental client proxy generator creates a typed implementation of an interface annotated with <xref:Nerdbank.JsonRpc.GenerateJsonRpcProxyAttribute>. It uses PolyType-generated method shapes for arguments and results. [Getting Started](getting-started.md) shows the contract declaration and how to call <xref:Nerdbank.JsonRpc.JsonRpc.Attach*> on a connection to obtain the proxy; you do not need to construct the generated implementation yourself.

The proxy resolves its type-shape provider once and caches the shapes it needs. <xref:Nerdbank.JsonRpc.JsonRpc.Attach*> and <xref:Nerdbank.JsonRpc.JsonRpcBatch.Attach*> accept an optional immutable <xref:Nerdbank.JsonRpc.JsonRpcProxyOptions> record for per-proxy argument encoding:

[!code-csharp[](../../samples/cs/client-proxies.cs#proxy-options)]

Generated methods support `ValueTask<T>`, `Task<T>`, `ValueTask`, `Task`, and `void` notifications. Arguments are positional arrays by default; set <xref:Nerdbank.JsonRpc.JsonRpcProxyOptions.UseNamedArguments> to send named object/map arguments when attaching a proxy. The same generated contract can be attached in either mode, including to a batch. For example, attach `ICalculator` with the named setting:

[!code-csharp[](../../samples/cs/client-proxies.cs#named-arguments)]

Generated proxies stream arguments directly into a counted, disposable <xref:Nerdbank.JsonRpc.JsonRpcArgumentsBuilder>, with one cancellation token for the entire argument set. They use the connection's selected [serializer](encodings.md).

To implement multiple RPC interfaces with one proxy, define an annotated composite interface and attach that composite type. `Attach<IBase>()` uses only metadata on `IBase`; it does not search for composite proxies that happen to implement that base interface. Proxies can also be [attached to a batch](batching.md).
