# Optional service interfaces

Optional interfaces let a service evolve by adding capabilities without requiring every implementation to support them.
Keep optional interfaces separate from the primary contract's inheritance hierarchy.
There are three distinct declarations: generated proxy groups, descriptor-known capabilities, and the subset advertised by a registration.

## Generate the possible proxy interfaces

Use <xref:Nerdbank.JsonRpc.JsonRpcProxyInterfaceGroupAttribute> to generate the interface combinations clients may need.
An empty group supports the primary contract alone.
The example below supports the primary contract alone and combinations of the reset and status contracts.

See [choosing generated interface groups](../client-proxies.md#choosing-generated-interface-groups) for larger combinations.
All proxies are source generated; descriptors do not dynamically generate missing groups.
Modern .NET shape overloads use `IShapeable<T>` constraints.
For contracts owned elsewhere, supply an explicit generated shape or use a generated witness with the witness overloads.
Legacy extension overloads resolve generated shapes without that constraint; their intentional modern-target compile-time diagnostics help identify missing source-generated shapes.

## Describe known capabilities

The descriptor's additional interfaces are the capabilities a client knows how to use, not a requirement that every service implement them.
Assign stable, application-defined capability identifiers rather than assembly-qualified CLR names.
The identifier and interface contract together must retain the same meaning on both endpoints.
<xref:Nerdbank.JsonRpc.BrokeredServices.ServiceInterface.Create*?displayProperty=nameWithType> and <xref:Nerdbank.JsonRpc.BrokeredServices.JsonRpcDescriptor.CreateInterface*?displayProperty=nameWithType> helpers preserve the captured type as <xref:Nerdbank.JsonRpc.BrokeredServices.ServiceInterface`1>.
Keep this typed handle when manually constructing callback or optional-interface proxies to get a `T` result without a cast; heterogeneous descriptor lists still use the non-generic base type.

The following descriptor knows two optional interfaces, reset and status.
Its generated groups cover the primary-only, reset-only, status-only, and combined cases.

[!code-csharp[](../../../samples/cs/brokered-service-hosting.cs#brokered-optional-contract)]

[!code-csharp[](../../../samples/cs/brokered-service-hosting.cs#brokered-optional-descriptor)]

## Register only the supported subset

The host advertises actual capabilities as stable strings in <xref:Nerdbank.JsonRpc.BrokeredServices.ServiceRegistration.AdditionalInterfaceIds?displayProperty=nameWithType>.
It does not have to advertise every capability in the descriptor.
Here the implementation supports reset, but not status, even though the descriptor knows both:

[!code-csharp[](../../../samples/cs/brokered-service-hosting.cs#brokered-optional-offering)]

[!code-csharp[](../../../samples/cs/brokered-service-hosting.cs#brokered-optional-implementation)]

Omit the registration's additional-interface IDs to advertise only the primary contract.
Advertise a capability only when every activation from that registration implements it; the container validates the factory's returned instance against the advertised interfaces.
Separate registrations or service versions are appropriate when different factories expose different capabilities.

## Register from serialized host metadata

A catalog should not need to locate the contract assembly, descriptor, or <xref:Nerdbank.JsonRpc.BrokeredServices.ServiceInterface`1> objects.
Registration therefore stores only identifiers: the exact strings from <xref:Nerdbank.JsonRpc.BrokeredServices.ServiceInterface.Id?displayProperty=nameWithType>.
These are also the strings returned by capability queries and sent with acquired remote connections; no host-specific ID translation or global type registry is required.
Use stable, application-defined strings such as `sample.reset.v1`, not CLR type names, assembly-qualified names, or generated proxy interface indices.
Matching is ordinal and case-sensitive, and an identifier must keep the same contract meaning across versions and endpoints.

For example, a host may read this manifest without referencing service contract types:

[!code-csharp[](../../../samples/cs/brokered-service-hosting.cs#brokered-serialized-manifest)]

The host parses the data and supplies its module-loading callback:

[!code-csharp[](../../../samples/cs/brokered-service-hosting.cs#brokered-serialized-registration)]

The parser is application code, not a required manifest schema or serializer.
The registration snapshots and validates the ID list, so disposing the JSON document or modifying the source list does not change the catalog.
An absent or empty registration ID list exposes only the primary contract, even when the descriptor knows more.

When the loader proffers the descriptor and factory, the container resolves each registered ID against that **server descriptor's** additional interfaces.
The descriptor supplies the CLR types and source-generated shapes needed for direct and RPC activation; the host catalog never supplies those shapes.
Unknown IDs are configuration errors: proffering fails without publishing the implementation, and host discovery fails before exposing capabilities or activating a factory.
Null, empty, whitespace-only, and duplicate IDs are rejected at registration time.
Unregistered descriptor capabilities remain disabled even if the implementation supports them.

The client independently resolves the advertised IDs against its own descriptor.
Clients that do not know an advertised capability can still use the primary contract and any capabilities they do know.
The client descriptor is not required to know the server's entire optional-interface set.

## Discover capabilities from the acquired proxy

Use <xref:Nerdbank.JsonRpc.IClientProxy.Is*?displayProperty=nameWithType> or <xref:Nerdbank.JsonRpc.JsonRpcExtensions.As*?displayProperty=nameWithType> rather than a CLR cast or `is` expression.
A generated proxy may implement a superset of the enabled CLR interfaces.
Local and remote acquisition enable only capabilities declared by both the client descriptor and the selected registration.

[!code-csharp[](../../../samples/cs/brokered-service-hosting.cs#brokered-optional-client)]

The example can reset the service, but its status query returns null.
Keep all optional-interface references within the owning proxy's lifetime.

## Query metadata without activation

When the broker implements <xref:Nerdbank.JsonRpc.BrokeredServices.IServiceBrokerWithMetadata>, capability identifiers can be queried before acquiring a proxy:

[!code-csharp[](../../../samples/cs/brokered-service-hosting.cs#brokered-optional-metadata)]

A null result means the service is unavailable; an empty list means it advertises no optional interfaces.
This query can trigger [on-demand loading](on-demand.md), but does not invoke the service factory.
Availability can change between a metadata query and acquisition, so use the acquired proxy's capability checks as the authority for that activation.
