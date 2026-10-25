# Basic client and server setup

Brokered services separate contract publication, implementation hosting, and client acquisition.
Put the contract and descriptor in an assembly shared by clients and service implementations; clients do not need to reference implementation assemblies.
Start with an in-process container to learn the same acquisition pattern used by [remote brokers](remote-brokers.md).

## Define the contract

Declare the primary contract with <xref:Nerdbank.JsonRpc.JsonRpcContractAttribute> and a PolyType-generated method shape.
This service has one greeting method and an event raised when it greets someone.

[!code-csharp[](../../../samples/cs/brokered-services.cs#brokered-contract)]

## Publish a descriptor

Publish a typed <xref:Nerdbank.JsonRpc.BrokeredServices.IJsonRpcDescriptor`1> property from the contract assembly.
Choose <xref:Nerdbank.JsonRpc.BrokeredServices.JsonRpcMessagePackDescriptor`1> for MessagePack or <xref:Nerdbank.JsonRpc.BrokeredServices.JsonRpcUtf8Descriptor`1> for UTF-8 JSON.

[!code-csharp[](../../../samples/cs/brokered-services.cs#brokered-descriptor)]

The descriptor identifies the service and supplies the contract information needed to construct its proxies.
Use the same descriptor on the client and server.
See [remote broker configuration](remote-brokers.md#descriptor-configuration) for serializer, framing, and transport settings.

## Implement and host the service

The service implements its contract as an ordinary class.

[!code-csharp[](../../../samples/cs/brokered-services.cs#brokered-implementation)]

<xref:Nerdbank.JsonRpc.BrokeredServices.BrokeredServiceContainerExtensions> provides strongly typed factories for any <xref:Nerdbank.JsonRpc.BrokeredServices.IBrokeredServiceContainer>: synchronous factories with no parameters, or asynchronous factories with no parameters or just a cancellation token.
The descriptor determines the factory's return contract, catching mismatched implementations at compile time.
The full <xref:Nerdbank.JsonRpc.BrokeredServices.BrokeredServiceFactory`1> receives the requested moniker, activation options, a dependency broker, and cancellation, and returns the descriptor's service contract.
A factory creates a separate client-owned instance for each acquisition and may return null when unavailable.
When a service needs dependencies, use the full factory's supplied broker so the host can apply dependency-routing policies.

[!code-csharp[](../../../samples/cs/brokered-services.cs#brokered-registration)]

Registration describes what a service exposes; proffering supplies its implementation.
A <xref:Nerdbank.JsonRpc.BrokeredServices.ServiceRegistration> declares the moniker, audience and supported optional-interface IDs, without referencing contract or implementation types.
<xref:Nerdbank.JsonRpc.BrokeredServices.IBrokeredServiceContainer.Register*?displayProperty=nameWithType> records that metadata.
Pass the descriptor and per-client factory directly to <xref:Nerdbank.JsonRpc.BrokeredServices.IBrokeredServiceContainer.Proffer*?displayProperty=nameWithType> to make an already registered service available.
Register first even when proffering eagerly.
See [on-demand proffering](on-demand.md) to defer implementation loading until a client requests the service.

## Acquire and dispose a proxy

The client interface is inferred from the descriptor, including when the descriptor property is declared using the interface:

[!code-csharp[](../../../samples/cs/brokered-services.cs#brokered-acquisition)]

The same acquisition works with a container's broker view or a remote broker.
The acquired proxy owns its activation or connection; dispose it when finished, preferring asynchronous disposal when available.
Dispose returned streams, subscriptions, and marshalable objects, and complete returned pipe ends, before awaiting proxy disposal.
The broker does not take ownership of caller-supplied callback targets.
See [lifetimes, caching, and resilience](lifetimes.md) for reuse and recovery patterns.

## Next steps

Use [optional interfaces](optional-interfaces.md) to expose additional capabilities without complicating the primary contract.
See [remote brokers and IPC](remote-brokers.md) for cross-process hosting, early event subscriptions, and client callbacks.
