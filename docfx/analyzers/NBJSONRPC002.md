# NBJSONRPC002: Colliding wire method names

| Property | Value |
| --- | --- |
| ID | `NBJSONRPC002` |
| Category | Usage |
| Default severity | Warning |

Two methods on an interface annotated with <xref:Nerdbank.JsonRpc.JsonRpcContractAttribute> or <xref:Nerdbank.JsonRpc.RpcMarshalableAttribute> map to the same JSON-RPC method name under the default naming policy.
Inherited methods are included.
Distinct declarations on unrelated base interfaces remain distinct, even when their CLR signatures match; diamond inheritance of the same declaration is counted only once.
Non-public helpers are excluded unless explicitly included with <xref:PolyType.MethodShapeAttribute>, and methods marked with <xref:PolyType.MethodShapeAttribute.Ignore> are excluded.
The diagnostic identifies the conflicting member, or the derived contract when the conflicting member is inherited.

## Why this is reported

The default naming policy removes a trailing `Async` suffix and lowercases the first character.
Names explicitly assigned with <xref:PolyType.MethodShapeAttribute.Name> are compared verbatim and are not transformed.
Wire names are case-sensitive.

Nerdbank.JsonRpc does not resolve method overloads.
Different parameter counts, parameter types, optional arguments, or cancellation tokens do not disambiguate methods with the same wire name.
Registering such a target throws <xref:System.InvalidOperationException>.
A client proxy can send both calls, but cannot make the receiving target distinguish them.

## Example

Both methods below use `displayMessage` on the wire, even though one has an extra argument:

[!code-csharp[](../../samples/cs/contract-validation.cs#colliding-methods)]

Assign distinct explicit names, or rename the methods so their convention-derived names differ:

[!code-csharp[](../../samples/cs/contract-validation.cs#distinct-methods)]

Changing a wire name changes the protocol.
Coordinate the change between independently deployed clients and servers.

## Custom naming and suppression

This rule assumes <xref:Nerdbank.JsonRpc.CommonMethodNameTransforms.Default>.
Arbitrary delegates assigned to <xref:Nerdbank.JsonRpc.JsonRpcProxyOptions.MethodNameTransform> or <xref:Nerdbank.JsonRpc.JsonRpcTargetOptions.MethodNameTransform> cannot be evaluated by the analyzer.
Suppress this warning locally only when a custom naming policy makes the names distinct, and configure both peers consistently.
The rule remains active when <xref:Nerdbank.JsonRpc.JsonRpcContractAttribute.GenerateProxy?displayProperty=nameWithType> is `false`.
No automatic code fix is offered because selecting a wire name is a protocol design decision.
