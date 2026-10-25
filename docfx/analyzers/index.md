# Contract and lifetime analysis

The Nerdbank.JsonRpc package includes C# analyzers and a client proxy source generator.
Apply <xref:Nerdbank.JsonRpc.JsonRpcContractAttribute> to an interface to identify it as an RPC contract.
The same checks also apply to <xref:Nerdbank.JsonRpc.RpcMarshalableAttribute> interfaces.
The package also checks ownership of brokered-service proxies and cached rentals, independently of contract attributes.

| Rule | Default severity | Description |
| --- | --- | --- |
| [NBJSONRPC001](NBJSONRPC001.md) | Warning | Unsupported proxy input |
| [NBJSONRPC002](NBJSONRPC002.md) | Warning | Colliding wire method names |
| [NBJSONRPC003](NBJSONRPC003.md) | Warning | Unsupported contract member |
| [NBJSONRPC004](NBJSONRPC004.md) | Warning | Dispose of brokered-service proxies |
| [NBJSONRPC005](NBJSONRPC005.md) | Warning | Do not store service rentals in fields or properties |

By default, a contract also generates a client proxy.
Set <xref:Nerdbank.JsonRpc.JsonRpcContractAttribute.GenerateProxy?displayProperty=nameWithType> to `false` to retain contract analysis without generating a proxy.
This also suppresses proxy generation when the interface has <xref:Nerdbank.JsonRpc.RpcMarshalableAttribute>.
An interface without a generated proxy does not need to be `partial` unless its PolyType shape generation requires it.

[!code-csharp[](../../samples/cs/contract-validation.cs#target-only)]

The former `GenerateJsonRpcProxyAttribute` has been replaced by <xref:Nerdbank.JsonRpc.JsonRpcContractAttribute>.
Replace the attribute name; default proxy generation behavior is unchanged.
