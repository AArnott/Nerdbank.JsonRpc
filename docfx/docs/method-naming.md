# Method name transforms

By default, Nerdbank.JsonRpc maps CLR method names to JSON-RPC wire names by removing a trailing `Async` suffix (if any) and converting the result to camelCase. For example, `GetValueAsync` becomes `getValue` and `Ping` becomes `ping`. This applies symmetrically to server-side target registration (<xref:Nerdbank.JsonRpc.JsonRpc.AddRpcTarget*>) and generated client proxies (<xref:Nerdbank.JsonRpc.JsonRpc.Attach*>), so a contract's methods dispatch under the same wire name on both sides without any extra configuration.

[!code-csharp[](../../samples/cs/method-naming.cs#contract)]

## Explicit names are authoritative

A method annotated with `[MethodShape(Name = "...")]` always dispatches on that exact name, both when registering a server target and when generating a client proxy. The configured transform is not applied to it. This makes explicit names a reliable escape hatch when you need a specific wire name, such as `subtract` in the example above.

## Interoperating with StreamJsonRpc

StreamJsonRpc's default behavior is to send and expect CLR-style method names (for example, `GetValueAsync`) unless its own `MethodNameTransform` is configured. To interoperate with such a peer, use <xref:Nerdbank.JsonRpc.CommonMethodNameTransforms.Identity> on either side, which returns names unchanged:

[!code-csharp[](../../samples/cs/method-naming.cs#identity-target-naming)]

[!code-csharp[](../../samples/cs/method-naming.cs#identity-proxy-naming)]

## Custom transforms

Set <xref:Nerdbank.JsonRpc.JsonRpcTargetOptions.MethodNameTransform> or <xref:Nerdbank.JsonRpc.JsonRpcProxyOptions.MethodNameTransform> to a `Func<string, string>` to fully control implicit name mapping. The function receives the CLR method name. It must return a non-null, non-empty result; registering a target throws if two methods transform to the same wire name, or if the transform returns a null or empty value.

> [!NOTE]
> Event-to-notification name transformation is not yet applicable, since this library does not yet support declaring notifications as CLR events. That will be addressed when event support is added.
