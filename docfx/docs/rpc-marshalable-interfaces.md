# RPC-marshalable interfaces

A value declared as an interface marked with <xref:Nerdbank.JsonRpc.RpcMarshalableAttribute> is sent by reference instead of by value. The receiving endpoint gets a generated proxy; calls on that proxy are sent to the endpoint that owns the object. For wire-protocol and lifetime details, see StreamJsonRpc's [RPC-marshalable objects documentation](https://microsoft.github.io/vs-streamjsonrpc/exotic_types/rpc_marshalable_objects.html); this page describes the subset currently supported by Nerdbank.JsonRpc.

Marshalable interfaces must inherit <xref:System.IDisposable>, declare methods only, and have a generated PolyType shape that includes public instance methods. The owner disposes the target when the receiver disposes the proxy or when the connection closes. Methods must return `Task`, `Task<T>`, `ValueTask`, or `ValueTask<T>`; `void` methods are not supported except for `IDisposable.Dispose()`.

```csharp
[RpcMarshalable]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ICounter : IDisposable
{
    Task<int> IncrementAsync(CancellationToken cancellationToken);
}
```

The interface itself does not also need <xref:Nerdbank.JsonRpc.GenerateJsonRpcProxyAttribute>; the JSON-RPC source generator creates its marshaled proxy from <xref:Nerdbank.JsonRpc.RpcMarshalableAttribute>. A typical use is to return the interface from a normal RPC contract, call methods on the returned proxy, and dispose it when finished.

This stage supports explicit lifetimes only. Call-scoped lifetimes and optional interfaces are not yet supported. A marshalable interface must not be used in a notification because a notification has no response that can confirm the receiver accepted its lifetime-sensitive argument.
