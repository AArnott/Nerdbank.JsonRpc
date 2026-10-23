# Events as notifications

Registering a target object also wires up any of its public instance events: raising an event on the target automatically sends a JSON-RPC notification to the remote party, using the same PolyType shape metadata used for method dispatch.

[!code-csharp[](../../samples/cs/events.cs#contract)]

[!code-csharp[](../../samples/cs/events.cs#default-registration)]

## Argument mapping

- Sender exclusion applies only to the BCL's `EventHandler` and `EventHandler<TEventArgs>` delegate types specifically — not to any delegate that merely happens to share their `(object? sender, TEventArgs e)` shape. A custom delegate with that same signature is treated like any other delegate: both parameters are forwarded.
- Any other delegate shape sends all of its parameters, positionally, as the notification's arguments.
- Only synchronous, `void`-returning delegates are supported. Registering a target with an event of another shape (for example, a `Task`-returning delegate) throws <xref:System.NotSupportedException>.
- Static events are ignored, since there is no single target instance to associate handler subscription and removal with.

## Naming

Event names use their own <xref:Nerdbank.JsonRpc.JsonRpcTargetOptions.EventNameTransform>, which defaults to `CommonMethodNameTransforms.CamelCase` and converts to camelCase without removing an `Async` suffix. This is separate from the method-name transform, which removes a trailing `Async` suffix by default. An explicit `[EventShape(Name = "...")]` is authoritative and bypasses the event transform. See [Method name transforms](method-naming.md) for more on how transforms and explicit names interact.

## Opting out

Set <xref:Nerdbank.JsonRpc.JsonRpcTargetOptions.NotifyClientOfEvents> to `false` to register a target's methods without subscribing to its events:

[!code-csharp[](../../samples/cs/events.cs#opt-out)]

## Receiving the notifications

Generated proxies implement CLR events declared on their contract, including inherited events.
The proxy raises each event when it receives a notification with the matching wire name and arguments.
For <xref:System.EventHandler> and <xref:System.EventHandler`1>, the local proxy supplies itself as the sender; custom delegates receive all wire arguments unchanged.

[!code-csharp[](../../samples/cs/events.cs#proxy-contract)]

### Subscribe before starting

Attach the proxy and add handlers **before** calling <xref:Nerdbank.JsonRpc.JsonRpc.Start>.
Attaching a proxy does not start reading messages, even if its transport is already connected.
This lets a connection owner expose the proxy for subscriptions before it enables incoming dispatch.
If a factory starts the connection before returning its proxy, provide a setup callback or separate initialization phase so callers can subscribe first.

[!code-csharp[](../../samples/cs/events.cs#proxy-receiver-registration)]

Handlers can also be added and removed after startup, with ordinary CLR multicast event semantics.
Notifications dispatched while no handler is subscribed are discarded, not buffered or replayed.
Proxy events run through the connection's normal dispatch synchronization context, and handler exceptions use the same logging and error handling as local target methods.

<xref:Nerdbank.JsonRpc.JsonRpcProxyOptions.EventNameTransform> controls incoming event names independently of method names and defaults to <xref:Nerdbank.JsonRpc.CommonMethodNameTransforms.CamelCase(System.String)?displayProperty=nameWithType>.
An explicit <xref:PolyType.EventShapeAttribute.Name> bypasses the transform.
Use <xref:Nerdbank.JsonRpc.CommonMethodNameTransforms.Identity> when receiving events from a StreamJsonRpc peer with default naming.
Event receivers are registered for the connection lifetime, including for proxies attached to a batch.
Attaching another proxy or local target with a conflicting incoming wire name throws <xref:System.InvalidOperationException>.
RPC-marshalable interfaces still cannot declare events.

### Receiving without a proxy

A notification is just a request without an `id`, so the remote party receives it the same way it would any other RPC call: by registering a target object whose method names and parameter shapes match the notification. Method name mapping works exactly like [regular RPC methods](method-naming.md) — the default camelCase transform turns a method named `PriceChanged` into the wire name `priceChanged`, matching the notification sent above.

Because it's an ordinary RPC method, a receiving method may also be declared asynchronously: naming it `PriceChangedAsync` (with the trailing `Async` stripped by the default <xref:Nerdbank.JsonRpc.JsonRpcTargetOptions.MethodNameTransform>, just like any other method) and returning `Task` or `ValueTask` both work. The notification carries no reply, so the returned task's result (if any) is discarded, but the method still runs to completion and any exception it throws is logged the same way a fire-and-forget request's would be. Since the default event-name transform preserves `Async`, an event whose CLR name ends in `Async` will not automatically match a same-named receiver method; configure the event or receiver transform (or an explicit event name) so both sides use the same wire name.

[!code-csharp[](../../samples/cs/events.cs#remote-receiver-contract)]

[!code-csharp[](../../samples/cs/events.cs#remote-receiver-registration)]
