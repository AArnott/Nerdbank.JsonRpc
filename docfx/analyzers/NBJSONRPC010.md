# NBJSONRPC010: Annotate RPC proxy interfaces

| Property | Value |
| - | - |
| ID | `NBJSONRPC010` |
| Category | Usage |
| Severity | Warning |

Interfaces passed to `JsonRpc.Attach` or `JsonRpcBatch.Attach` must be annotated with
`JsonRpcContractAttribute` or `RpcMarshalableAttribute`. The annotations enable contract
validation and source-generated proxy discovery.

```csharp
// NBJSONRPC010
rpc.Attach<IMyContract>();
```

Apply the appropriate annotation to the interface and resolve any resulting contract diagnostics.

