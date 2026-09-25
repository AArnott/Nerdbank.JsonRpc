# Events as notifications

Registering a target object also wires up any of its public instance events: raising an event on the target automatically sends a JSON-RPC notification to the remote party, using the same PolyType shape metadata used for method dispatch.

[!code-csharp[](../../samples/cs/events.cs#contract)]

[!code-csharp[](../../samples/cs/events.cs#default-registration)]

## Argument mapping

- The classic .NET event pattern — a handler shaped like `(object? sender, TEventArgs e)` — is recognized by its first parameter being named `sender`. That parameter is omitted from the notification; only `e` is sent, as the notification's sole argument.
- Any other delegate shape sends all of its parameters, positionally, as the notification's arguments.
- Only synchronous, `void`-returning delegates are supported. Registering a target with an event of another shape (for example, a `Task`-returning delegate) throws <xref:System.NotSupportedException>.
- Static events are ignored, since there is no single target instance to associate handler subscription and removal with.

## Naming

Event names are mapped to wire names the same way as method names: <xref:Nerdbank.JsonRpc.JsonRpcTargetOptions.EventNameTransform> defaults to <xref:Nerdbank.JsonRpc.CommonMethodNameTransforms.CamelCase>, and an explicit `[EventShape(Name = "...")]` is authoritative and bypasses the transform. See [Method name transforms](method-naming.md) for more on how transforms and explicit names interact.

## Opting out

Set <xref:Nerdbank.JsonRpc.JsonRpcTargetOptions.NotifyClientOfEvents> to `false` to register a target's methods without subscribing to its events:

[!code-csharp[](../../samples/cs/events.cs#opt-out)]
