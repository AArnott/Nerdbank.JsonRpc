# NBJSONRPC005: Do not store service rentals in fields or properties

| Property | Value |
| --- | --- |
| ID | `NBJSONRPC005` |
| Category | Usage |
| Default severity | Warning |

Rentals obtained from <xref:Nerdbank.JsonRpc.BrokeredServices.ServiceBrokerClient.GetProxyAsync*?displayProperty=nameWithType> should be used and disposed of within the acquiring asynchronous method.
Storing a rental in a field or property extends its lifetime beyond that scope and interferes with timely cache invalidation and service replacement.
This rule also diagnoses storing an existing local rental or storing a rental through a conversion to `object`.

Avoid storing the rental:

[!code-csharp[](../../samples/cs/brokered-service-lifetimes.cs#brokered-stored-rental)]

Instead, keep it local:

[!code-csharp[](../../samples/cs/brokered-services.cs#brokered-cache)]

For longer-lived service ownership, acquire the proxy directly from <xref:Nerdbank.JsonRpc.BrokeredServices.IServiceBroker> and follow [NBJSONRPC004](NBJSONRPC004.md).
No automatic code fix is offered because moving a rental changes its lifetime and may require redesigning its owner.
Suppress this diagnostic only when deliberately retaining a rental, and ensure it is disposed.
This rule is the Nerdbank.JsonRpc counterpart of ServiceHub's ISB002.
