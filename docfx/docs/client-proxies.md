# Generated client proxies

The experimental client proxy generator creates a typed implementation of an interface annotated with <xref:Nerdbank.JsonRpc.GenerateJsonRpcProxyAttribute>. It uses PolyType-generated method shapes for arguments and results.

[!code-csharp[](../../samples/cs/GettingStarted.cs#generated-client-proxy)]

Call <xref:Nerdbank.JsonRpc.JsonRpc.Attach*> on a connection to obtain the proxy; you do not need to construct the generated implementation yourself:

[!code-csharp[](../../samples/cs/GettingStarted.cs#attach-proxy)]

The proxy resolves its type-shape provider once and caches the shapes it needs. <xref:Nerdbank.JsonRpc.JsonRpc.Attach*> accepts an optional immutable <xref:Nerdbank.JsonRpc.JsonRpcProxyOptions> record reserved for future settings:

[!code-csharp[](../../samples/cs/GettingStarted.cs#proxy-options)]

Generated methods support `ValueTask<T>`, `Task<T>`, `ValueTask`, `Task`, and `void` notifications. Arguments are positional arrays by default. To send named object/map arguments, set <xref:Nerdbank.JsonRpc.GenerateJsonRpcProxyAttribute.UseNamedArguments> on the contract:

[!code-csharp[](../../samples/cs/GettingStarted.cs#named-arguments)]

Generated proxies stream arguments directly into a counted, disposable <xref:Nerdbank.JsonRpc.JsonRpcArgumentsBuilder>, with one cancellation token for the entire argument set. They use the connection's selected [serializer](encodings.md).

To implement multiple RPC interfaces with one proxy, define an annotated composite interface and attach that composite type. `Attach<IBase>()` uses only metadata on `IBase`; it does not search for composite proxies that happen to implement that base interface. Proxies can also be [attached to a batch](batching.md).
