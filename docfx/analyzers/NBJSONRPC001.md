# NBJSONRPC001: Unsupported JSON-RPC proxy input

| Property | Value |
| --- | --- |
| ID | `NBJSONRPC001` |
| Category | Usage |
| Default severity | Warning |

The source generator reports this diagnostic when an interface annotated with `GenerateJsonRpcProxy` or `RpcMarshalable` cannot be represented by the generated JSON-RPC proxy implementation. The reported location identifies the unsupported interface declaration or method.

## Why this is reported

The generator must emit a proxy whose signatures and wire behavior match the contract. It reports unsupported input rather than emitting an incomplete proxy. Current restrictions include non-partial, nested, and generic interfaces; properties and events on RPC-marshalable interfaces; and methods with generic, optional, `params`, or by-reference parameters. Proxy methods must return `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`, or `void` where notifications are allowed. Cancellation tokens must be the final parameter. RPC-marshalable interfaces must extend `IDisposable` unless they use call-scoped lifetime.

## Examples

Unsupported:

```csharp
[GenerateJsonRpcProxy]
internal interface ICalculator
{
    Task<int> AddAsync(int left, int right);
}
```

Corrected:

```csharp
[GenerateJsonRpcProxy]
internal partial interface ICalculator
{
    Task<int> AddAsync(int left, int right);
}
```

For a method-signature diagnostic, remove the unsupported parameter or return shape, or change the contract to a supported asynchronous signature. The generator does not provide a code fix; adjust the contract according to the reported reason. Suppress the warning only when the unsupported declaration is intentionally not used to generate a proxy.
