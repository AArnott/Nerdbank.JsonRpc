# RPC-marshalable interfaces

A value declared as an interface marked with <xref:Nerdbank.JsonRpc.RpcMarshalableAttribute> is sent by reference instead of by value. The receiving endpoint gets a generated proxy; calls on that proxy are sent to the endpoint that owns the object. For wire-protocol and lifetime details, see StreamJsonRpc's [RPC-marshalable objects documentation](https://microsoft.github.io/vs-streamjsonrpc/exotic_types/rpc_marshalable_objects.html); this page describes the subset currently supported by Nerdbank.JsonRpc.

Marshalable interfaces declare methods only and need a generated PolyType shape that includes public instance methods. Explicit-lifetime interfaces (the default) must inherit <xref:System.IDisposable>; the owner disposes the target when the receiver disposes the proxy or when the connection closes. Methods must return `Task`, `Task<T>`, `ValueTask`, or `ValueTask<T>`; `void` methods are not supported except for `IDisposable.Dispose()`.

```csharp
[RpcMarshalable]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ICounter : IDisposable
{
    Task<int> IncrementAsync(CancellationToken cancellationToken);
}
```

The interface itself does not also need <xref:Nerdbank.JsonRpc.GenerateJsonRpcProxyAttribute>; the JSON-RPC source generator creates its marshaled proxy from <xref:Nerdbank.JsonRpc.RpcMarshalableAttribute>. A typical use is to return the interface from a normal RPC contract, call methods on the returned proxy, and dispose it when finished.

Set <xref:Nerdbank.JsonRpc.RpcMarshalableAttribute.CallScopedLifetime> to `true` for a call-scoped interface. Such an interface need not inherit `IDisposable`; its proxy is valid only while the receiving RPC method is running, and the target remains under the sender's lifetime control. Call-scoped interfaces are supported only in request arguments, not return values. No marshalable interface may be sent in a notification because there is no response to confirm acceptance. When a request returns a JSON-RPC error, its marshaled arguments are released; successful explicit-lifetime proxies remain valid until disposed or the connection closes. Optional interfaces are not yet supported.
