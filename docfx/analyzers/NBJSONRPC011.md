# NBJSONRPC011: Use supported interfaces for RPC proxies

| Property | Value |
| - | - |
| ID | `NBJSONRPC011` |
| Category | Usage |
| Severity | Warning |

Types passed to `JsonRpc.Attach` or `JsonRpcBatch.Attach` must be non-generic interfaces that
can be implemented by the generated proxy. Classes and generic interfaces are not supported.

```csharp
// NBJSONRPC011
rpc.Attach<MyClass>();
```

Use a non-generic interface contract instead.

