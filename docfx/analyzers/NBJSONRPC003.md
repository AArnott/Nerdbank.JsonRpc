# NBJSONRPC003: Unsupported contract member

| Property | Value |
| --- | --- |
| ID | `NBJSONRPC003` |
| Category | Usage |
| Default severity | Warning |

An RPC contract contains an unsupported member.
Abstract properties and indexers cannot be invoked through the RPC protocol; use asynchronous methods instead.
Default interface property implementations are allowed because they execute locally.

When <xref:Nerdbank.JsonRpc.JsonRpcContractAttribute.GenerateProxy?displayProperty=nameWithType> is `false`, this rule also validates method signatures and event delegates.
When generating a proxy, the source generator reports these signature and event restrictions as [NBJSONRPC001](NBJSONRPC001.md), avoiding duplicate diagnostics.
Supported methods return `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`, `IAsyncEnumerable<T>`, or `void` for notifications.
RPC methods must not be static or generic, and parameters must not be `params`, by-reference, ref-like, or pointers.
Concrete static interface helpers are excluded from proxy generation and contract analysis; static abstract methods remain unsupported.
Cancellation tokens must appear last.
Notifications cannot carry RPC-marshalable interfaces.
Events must be instance events with synchronous, void-returning delegates and supported by-value parameters.
Marshalable interfaces cannot declare events.

Inherited members are included.
These checks require <xref:Nerdbank.JsonRpc.JsonRpcContractAttribute> or <xref:Nerdbank.JsonRpc.RpcMarshalableAttribute>.

## Example

Unsupported:

[!code-csharp[](../../samples/cs/contract-validation.cs#unsupported-member)]

Corrected:

[!code-csharp[](../../samples/cs/contract-validation.cs#supported-member)]

No automatic code fix is offered.
Change the contract to supported RPC members; suppress the diagnostic only when the member is intentionally not part of the RPC surface.
