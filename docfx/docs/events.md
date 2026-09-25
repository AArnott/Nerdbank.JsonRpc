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

Event names are mapped to wire names the same way as method names: <xref:Nerdbank.JsonRpc.JsonRpcTargetOptions.EventNameTransform> defaults to <xref:Nerdbank.JsonRpc.CommonMethodNameTransforms.CamelCase>, and an explicit `[EventShape(Name = "...")]` is authoritative and bypasses the transform. See [Method name transforms](method-naming.md) for more on how transforms and explicit names interact.

## Opting out

Set <xref:Nerdbank.JsonRpc.JsonRpcTargetOptions.NotifyClientOfEvents> to `false` to register a target's methods without subscribing to its events:

[!code-csharp[](../../samples/cs/events.cs#opt-out)]

## Receiving the notifications

A notification is just a request without an `id`, so the remote party receives it the same way it would any other RPC call: by registering a target object whose method names and parameter shapes match the notification. Method name mapping works exactly like [regular RPC methods](method-naming.md) — the default camelCase transform turns a method named `PriceChanged` into the wire name `priceChanged`, matching the notification sent above.

[!code-csharp[](../../samples/cs/events.cs#remote-receiver-contract)]

[!code-csharp[](../../samples/cs/events.cs#remote-receiver-registration)]
