# RPC-marshalable interfaces

A value declared as an interface marked with <xref:Nerdbank.JsonRpc.RpcMarshalableAttribute> is sent by reference instead of by value. The receiving endpoint gets a generated proxy; calls on that proxy are sent to the endpoint that owns the object. For wire-protocol and lifetime details, see StreamJsonRpc's [RPC-marshalable objects documentation](https://microsoft.github.io/vs-streamjsonrpc/exotic_types/rpc_marshalable_objects.html); this page describes Nerdbank.JsonRpc's support and usage requirements.

Marshalable interfaces declare methods only and need a generated PolyType shape that includes public instance methods. Explicit-lifetime interfaces (the default) must inherit <xref:System.IDisposable>; the owner disposes the target when the receiver disposes the proxy or when the connection closes. Methods must return `Task`, `Task<T>`, `ValueTask`, or `ValueTask<T>`; `void` methods are not supported except for `IDisposable.Dispose()`.

[!code-csharp[](../../samples/cs/rpc-marshalable-interfaces.cs#explicit-lifetime-counter)]

The interface itself does not also need <xref:Nerdbank.JsonRpc.GenerateJsonRpcProxyAttribute>; the JSON-RPC source generator creates its marshaled proxy from <xref:Nerdbank.JsonRpc.RpcMarshalableAttribute>. A typical use is to return the interface from a normal RPC contract, call methods on the returned proxy, and dispose it when finished.

Set <xref:Nerdbank.JsonRpc.RpcMarshalableAttribute.CallScopedLifetime> to `true` for a call-scoped interface. Such an interface need not inherit `IDisposable`; its proxy is valid while the receiving RPC method is running, and the target remains under the sender's lifetime control. If the successful result contains `IAsyncEnumerable<T>` values, this scope extends through their enumeration and asynchronous disposal. All returned sequences sharing that scope must finish or be disposed; this also applies to nested sequences delivered in later batches. An error response does not extend the scope.

Call-scoped interfaces are supported only in request arguments, not return values. No marshalable interface may be sent in a notification because there is no response to confirm acceptance. When a request returns a JSON-RPC error, its marshaled arguments are released; successful explicit-lifetime proxies remain valid until disposed or the connection closes.

### Method naming on marshaled objects

Methods on RPC-marshalable interfaces use the same default transform as ordinary RPC methods: `AddAsync`, for example, is invoked as `$/invokeProxy/0/add`. The options for these implicitly registered targets and proxies are exposed separately via <xref:Nerdbank.JsonRpc.JsonRpc.MarshaledTargetOptions> and <xref:Nerdbank.JsonRpc.JsonRpc.MarshaledProxyOptions>. Explicit options passed to `AddRpcTarget` or `Attach` do not affect them. The target options also carry event settings, although marshalable interfaces currently do not support events.

StreamJsonRpc uses verbatim CLR names instead (such as `$/invokeProxy/0/AddAsync`). To [interoperate with StreamJsonRpc](method-naming.md#marshaled-objects), set both marshaled options' `MethodNameTransform` to <xref:Nerdbank.JsonRpc.CommonMethodNameTransforms.Identity> before `Start`. This does not change the naming of ordinary RPC methods. Individual methods can override their wire names with `[MethodShape(Name = "...")]`.

## Optional interfaces

Apply <xref:Nerdbank.JsonRpc.RpcMarshalableOptionalInterfaceAttribute> to a marshalable base interface when implementations may expose additional RPC capabilities. Each optional interface has a stable signed 32-bit ID that must never be reused for a different interface. The optional interface must have a generated PolyType method shape, just like the base interface.

[!code-csharp[](../../samples/cs/rpc-marshalable-interfaces.cs#optional-interfaces)]

The sender advertises only interfaces implemented by the actual target. The receiver's generated proxy implements exactly the advertised interfaces it recognizes, so normal C# type tests and casts provide capability discovery (`counter is IResettableCounter`). Unknown IDs are ignored for version tolerance. Optional calls use `$/invokeProxy/{handle}/{interfaceId}.{method}` on the wire, which keeps same-named methods on different interfaces distinct. Optional capabilities preserve the base object's identity and lifetime; disposing any generated proxy releases that marshaled handle. As with the base interface, optional capabilities cannot be forwarded to a third connection.

## Connection boundaries

A received marshaled proxy may be sent back over the same `JsonRpc` connection. Its owner receives the original object, not a proxy to a proxy.

Forwarding a received proxy over a **different** `JsonRpc` connection (for example, from A through B to C) is not supported and fails serialization with `NotSupportedException` (wrapped in `MessagePackSerializationException` when using MessagePack). This applies to `[RpcMarshalable]` interfaces, `IDisposable`, and `IObserver<T>`, including values nested in object graphs. The rejected forwarding attempt does not release the original proxy. Handles, identity, and lifetime ownership are connection-local; multi-party forwarding would require additional ownership rules. Application-written forwarding wrappers are not detected and are responsible for their own lifetime management.

## Owner revocation

The owner may terminate every active marshaled relationship for one target by calling <xref:Nerdbank.JsonRpc.JsonRpc.RevokeMarshaledObject(System.Object)>. This object-wide operation returns the number of handles revoked, sends an `ownedBySender: true` release notification for each handle, and leaves handles for other objects untouched. It does not dispose the target; after revocation, its owner is solely responsible for disposal. To revoke relationships independently, marshal distinct wrapper objects instead of the same target instance.

After the peer processes the notification, generated proxies reject new calls with `ObjectDisposedException`; a raw invocation that races after the owning endpoint has removed the handle receives JSON-RPC error -32001. Calls already dispatched before revocation may complete. Repeated revocation, proxy disposal racing revocation, and later scope cleanup are idempotent. Call-scoped targets normally expire with their request (including any async-enumerable extension), but their owner may explicitly revoke them early through the same API.

Together with optional interfaces, receiver disposal, call-scoped and explicit lifetimes, same-connection round trips, and connection cleanup, this completes the general marshalable-object work tracked by [issue #69](https://github.com/AArnott/Nerdbank.JsonRpc/issues/69). Third-party forwarding remains deliberately unsupported as described below.

## Observer callbacks

`IObserver<T>` parameters and return values are marshaled by reference without `[RpcMarshalable]` or a generated observer proxy. Provide a PolyType shape for `T` through the containing RPC contract's shape provider. The endpoint receiving the observer can call `OnNext(T)` repeatedly, then `OnCompleted()` or `OnError(Exception)` once. Terminal callbacks release the remote handle; subsequent callbacks fail with `ObjectDisposedException`. Releasing an observer handle does not dispose the observer instance. Observers may not be passed in notifications.

[!code-csharp[](../../samples/cs/rpc-marshalable-interfaces.cs#observer-subscription-contract)]

Returning `IDisposable` from a subscription method lets the caller unsubscribe by disposing the returned proxy. Disposal is a notification: updates already in flight can still arrive until the server processes the unsubscribe.

`OnError` transports the exception message; the remote endpoint receives an `Exception` with that message, not the original exception type. See StreamJsonRpc's [observer documentation](https://microsoft.github.io/vs-streamjsonrpc/exotic_types/observer.html) for protocol details.

## Progress notifications

`IProgress<T>` request parameters are encoded as opaque progress tokens. During the request, the server receives an `IProgress<T>` that sends each `Report` call as a `$/progress` notification with named `token` and `value` parameters. The client invokes its supplied `IProgress<T>` in report order before completing the RPC call. The server-side progress instance becomes inert when the request completes, and subsequent notifications for the token are ignored. `IProgress<T>` is supported only in request arguments; passing one in a notification is rejected. Pass `null` when a caller does not want updates.

[!code-csharp[](../../samples/cs/rpc-marshalable-interfaces.cs#progress-contract)]

This matches StreamJsonRpc's [`IProgress<T>` protocol](https://microsoft.github.io/vs-streamjsonrpc/exotic_types/progresssupport.html).

## Out-of-band streams

`Stream`, `IDuplexPipe`, `PipeReader`, and `PipeWriter` parameters and return values transfer their bytes over a separate `MultiplexingStream` channel rather than encoding them into the JSON-RPC message. Set <xref:Nerdbank.JsonRpc.JsonRpc.MultiplexingStream> on each endpoint before calling <xref:Nerdbank.JsonRpc.JsonRpc.Start>.

[!code-csharp[](../../samples/cs/rpc-marshalable-interfaces.cs#out-of-band-stream-connection)]

The sender serializes an anonymous multiplex-channel ID as the parameter or result value; the receiving endpoint accepts that channel automatically. Stream-shaped arguments may only be used in requests, never notifications. If a request fails, the channel is closed automatically; after a successful response, both peers own their pipe ends and must complete or dispose them when finished. This matches StreamJsonRpc's [out-of-band stream protocol](https://microsoft.github.io/vs-streamjsonrpc/exotic_types/oob_streams.html).

## Async enumerables

`IAsyncEnumerable<T>` arguments and return values are marshaled by reference so the receiver pulls items on demand instead of waiting for the whole sequence to be produced and encoded. This works anywhere it is declared in the object graph reachable from an argument or return value, not just at the top level (for example, a property of a DTO typed `IAsyncEnumerable<T>` is marshaled the same way). As with the other exotic types on this page, the value must be *declared* as `IAsyncEnumerable<T>` (or as an interface/DTO whose shape resolves to it) at that point in the graph; a value merely typed `object` or boxed at runtime is sent by value like any other data, because dispatch is driven by the declared shape, not a runtime type check. Use it for long, expensive, or unbounded sequences where the consumer may stop early. Provide a PolyType shape for the element type through the containing RPC contract's shape provider.

[!code-csharp[](../../samples/cs/rpc-marshalable-interfaces.cs#async-enumerable-contract)]

A proxy method should return `IAsyncEnumerable<T>` directly rather than `Task<IAsyncEnumerable<T>>`/`ValueTask<IAsyncEnumerable<T>>`. The request is sent immediately and the response is awaited lazily on first enumeration, so a bare return type loses nothing and is more idiomatic. Wrapping in `Task<T>`/`ValueTask<T>` still works and remains useful when the RPC contract needs to compose with other Task-returning APIs.

### Resource lifetime

Each marshaled sequence holds resources on the producing endpoint until it is drained to completion or discarded, so the consumer **must** enumerate it to the end or dispose it. `await foreach` does both automatically, including when the loop is exited with `break` or an exception. A received sequence may be enumerated only once; a second attempt throws <xref:System.InvalidOperationException>.

Call-scoped marshalable callbacks supplied to a sequence-producing method remain usable after its initial response, including from the iterator's `finally` block. When such callbacks are present, early disposal waits for acknowledgment of `$/enumerator/abort` before releasing the caller's handles. Disposing an enumerator before its first `MoveNextAsync` also releases the remote sequence. Closing the connection ends these lifetimes as well.

Sequences passed as request arguments are released when the response arrives, so the server must finish consuming them before returning. Sequences returned as results live until the consumer stops enumerating or the connection closes. Because there is no response to confirm acceptance, `IAsyncEnumerable<T>` may not be sent in a notification.

### Tuning

By default each `MoveNextAsync` that is not already satisfied costs one round trip. <xref:Nerdbank.JsonRpc.JsonRpcEnumerableSettings> tunes that for a sequence you send, and `WithJsonRpcSettings` applies it:

[!code-csharp[](../../samples/cs/rpc-marshalable-interfaces.cs#tuned-sequence)]

- `MinBatchSize` (default 1) makes the producer wait until it has at least this many values before answering a request, amortizing round trips over many small items.
- `MaxReadAhead` (default 0) lets the producer generate up to this many values ahead of what the consumer has asked for, so values are usually ready the moment they are requested. Combine it with `MinBatchSize` to keep a filled buffer that is handed over in chunks.
- `Prefetch` (default 0) includes that many values inline in the message that carries the sequence, so the consumer's first items cost no extra round trip. If the whole sequence fits within `Prefetch`, no token is sent at all and the exchange costs exactly one message.

Prefetching must run before the sequence is serialized. Setting `Prefetch` does this automatically for sequences returned from an RPC method. For a sequence passed as an *argument*, await `WithPrefetchAsync` first:

[!code-csharp[](../../samples/cs/rpc-marshalable-interfaces.cs#prefetch-argument)]

`AsAsyncEnumerable` adapts an existing synchronous sequence so it can be marshaled.

### Wire protocol

The sequence is serialized as an object with an optional `token` and an optional inline `values` array. The consumer then sends `$/enumerator/next` with that token and receives `{ "values": [...], "finished": bool }`, and sends an `$/enumerator/abort` notification if it stops early. An unrecognized token fails with error code -32001. This matches StreamJsonRpc's [async enumerable protocol](https://microsoft.github.io/vs-streamjsonrpc/exotic_types/asyncenumerable.html).
