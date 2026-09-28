# Method name transforms

By default, Nerdbank.JsonRpc maps CLR method names to JSON-RPC wire names by removing a trailing `Async` suffix (if any) and converting the result to camelCase. For example, `GetValueAsync` becomes `getValue` and `Ping` becomes `ping`. This applies symmetrically to server-side target registration (<xref:Nerdbank.JsonRpc.JsonRpc.AddRpcTarget*>) and generated client proxies (<xref:Nerdbank.JsonRpc.JsonRpc.Attach*>), so a contract's methods dispatch under the same wire name on both sides without any extra configuration.

[!code-csharp[](../../samples/cs/method-naming.cs#contract)]

## Explicit names are authoritative

A method annotated with `[MethodShape(Name = "...")]` always dispatches on that exact name, both when registering a server target and when generating a client proxy. The configured transform is not applied to it. This makes explicit names a reliable escape hatch when you need a specific wire name, such as `subtract` in the example above.

## Interoperating with StreamJsonRpc

StreamJsonRpc's default behavior is to send and expect CLR-style method names (for example, `GetValueAsync`) unless its own `MethodNameTransform` is configured. To interoperate with such a peer, use <xref:Nerdbank.JsonRpc.CommonMethodNameTransforms.Identity> on either side, which returns names unchanged:

[!code-csharp[](../../samples/cs/method-naming.cs#identity-target-naming)]

[!code-csharp[](../../samples/cs/method-naming.cs#identity-proxy-naming)]

### Connection-wide defaults

Some RPC targets and proxies are created implicitly and therefore cannot be configured at a call site. The most important case is [RPC-marshalable objects](rpc-marshalable-interfaces.md): when a marshalable value crosses the connection, the sender registers it as a target and the receiver attaches a proxy to it, both without your involvement.

Set <xref:Nerdbank.JsonRpc.JsonRpc.DefaultTargetOptions> and <xref:Nerdbank.JsonRpc.JsonRpc.DefaultProxyOptions> to control naming for those implicit registrations, and as the fallback for <xref:Nerdbank.JsonRpc.JsonRpc.AddRpcTarget*> and <xref:Nerdbank.JsonRpc.JsonRpc.Attach*> calls that do not pass their own options. Both must be set before <xref:Nerdbank.JsonRpc.JsonRpc.Start>. Configuring both with `Identity` makes every method name on the connection, including those on marshaled objects, match a default-configured StreamJsonRpc peer:

```cs
JsonRpc rpc = new(channel)
{
    DefaultTargetOptions = new() { MethodNameTransform = CommonMethodNameTransforms.Identity },
    DefaultProxyOptions = new() { MethodNameTransform = CommonMethodNameTransforms.Identity },
};
```

## Custom transforms

Set <xref:Nerdbank.JsonRpc.JsonRpcTargetOptions.MethodNameTransform> or <xref:Nerdbank.JsonRpc.JsonRpcProxyOptions.MethodNameTransform> to a `Func<string, string>` to fully control implicit name mapping. The function receives the CLR method name. It must return a non-null, non-empty result; registering a target throws if two methods transform to the same wire name, or if the transform returns a null or empty value.

Events use the separate <xref:Nerdbank.JsonRpc.JsonRpcTargetOptions.EventNameTransform>, which defaults to camelCase only and preserves a trailing `Async` suffix. Unlike method naming, the default event transform does not remove `Async`; see [Events as notifications](events.md).
