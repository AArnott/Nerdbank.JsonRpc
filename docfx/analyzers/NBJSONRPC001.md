# NBJSONRPC001: Unsupported JSON-RPC proxy input

| Property | Value |
| --- | --- |
| ID | `NBJSONRPC001` |
| Category | Usage |
| Default severity | Warning |

The source generator reports this diagnostic when an interface annotated with <xref:Nerdbank.JsonRpc.JsonRpcContractAttribute> or <xref:Nerdbank.JsonRpc.RpcMarshalableAttribute> cannot be represented by the generated JSON-RPC proxy implementation.
The reported location identifies the unsupported interface declaration, method, or event.
Setting <xref:Nerdbank.JsonRpc.JsonRpcContractAttribute.GenerateProxy?displayProperty=nameWithType> to `false` suppresses proxy generation and this diagnostic, but [contract analysis](index.md) remains active.

## Why this is reported

The generator must emit a proxy whose signatures and wire behavior match the contract.
It reports unsupported input rather than emitting an incomplete proxy.
Current restrictions include non-partial, nested, and generic interfaces; properties and events on RPC-marshalable interfaces; static or generic methods; and methods with `params`, by-reference, ref-like, or pointer parameters.
Proxy methods must return `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`, `IAsyncEnumerable<T>`, or `void` where notifications are allowed.
Cancellation tokens must be the final parameter.
RPC-marshalable interfaces must extend `IDisposable` unless they use call-scoped lifetime.

Proxy events must be instance events whose delegates return `void` and accept only by-value, non-ref-like parameters.
Asynchronous, value-returning, static, and by-reference event contracts report this diagnostic.
Event parameters also cannot contain RPC-marshalable interfaces, including within arrays or generic types, because notifications do not support marshaled handles.

Interface groups must contain non-null, non-generic, non-nested interface types with supported RPC members.
On a marshalable contract, every group member must also be declared with <xref:Nerdbank.JsonRpc.RpcMarshalableOptionalInterfaceAttribute>.
Invalid groups report this diagnostic instead of emitting an incomplete proxy; see [generated interface groups](../docs/client-proxies.md#choosing-generated-interface-groups).

## Examples

Unsupported:

```csharp
[JsonRpcContract]
internal interface ICalculator
{
    Task<int> AddAsync(int left, int right);
}
```

Corrected:

```csharp
[JsonRpcContract]
internal partial interface ICalculator
{
    Task<int> AddAsync(int left, int right);
}
```

For a method-signature diagnostic, remove the unsupported parameter or return shape, or change the contract to a supported asynchronous signature. The generator does not provide a code fix; adjust the contract according to the reported reason. Suppress the warning only when the unsupported declaration is intentionally not used to generate a proxy.
