# Versioning services

A <xref:Nerdbank.JsonRpc.BrokeredServices.ServiceMoniker> identifies a service by its case-sensitive name and optional <xref:System.Version>.
The container matches the complete moniker, including its version.
It does not automatically select the latest version, fall back to another version, or treat an unversioned moniker as a wildcard.
Clients request a specific published descriptor, and hosts explicitly proffer each supported version.

## Publish versioned descriptors

Publish a descriptor for each supported service version from the shared contract assembly.
This example keeps the same primary interface so the difference between routing strategies is clear:

[!code-csharp[](../../../samples/cs/brokered-service-versioning.cs#brokered-version-descriptors)]

Use distinct interfaces and descriptors when a new version changes the wire contract incompatibly.
Both endpoints still need to agree on encoding, framing, naming, and multiplexing settings for each version.
Changing a moniker alone does not adapt arguments or results.

## One implementation serving multiple versions

Proffer each versioned descriptor with the same factory.
The <xref:Nerdbank.JsonRpc.BrokeredServices.BrokeredServiceFactory`1> receives the moniker requested by the client, so it can configure an activation for that version.

[!code-csharp[](../../../samples/cs/brokered-service-versioning.cs#brokered-version-shared-factory)]

The implementation stores the requested version and uses it when handling calls:

[!code-csharp[](../../../samples/cs/brokered-service-versioning.cs#brokered-version-shared-implementation)]

These are separate per-client activations of the same implementation type, not one singleton shared across versions.
Keep each proffer lease alive for as long as that version should remain available.
Disposing one lease withdraws only its moniker and does not invalidate already-issued activations.

## Distinct implementations selected by version

Alternatively, proffer each descriptor with its own factory and implementation.
The container's exact-moniker lookup selects the factory; no version switch is needed inside either implementation.

[!code-csharp[](../../../samples/cs/brokered-service-versioning.cs#brokered-version-distinct-factories)]

[!code-csharp[](../../../samples/cs/brokered-service-versioning.cs#brokered-version-distinct-implementations)]

The shared-factory and distinct-factory examples are alternative host configurations.
Do not proffer both configurations for the same monikers in the same container.
The same distinction applies to [on-demand registration](on-demand.md): register each version's moniker and have its loader proffer the corresponding offering.

## Client version selection

Clients choose the descriptor for the version they need, with the service interface still inferred:

[!code-csharp[](../../../samples/cs/brokered-service-versioning.cs#brokered-version-client)]

Both host configurations above support this client unchanged.
The example returns `Hello, Ada!` for version 1 and `Welcome, Ada!` for version 2.
If a requested version is not proffered or discoverable, acquisition returns null.
If an application deliberately supports fallback, it should explicitly attempt its supported descriptors and handle unavailability; activation faults should not be mistaken for a missing version.
For additive features that do not require a new primary contract, consider [optional interfaces](optional-interfaces.md).

## Expose a future-proof application SDK

An application that exposes brokered services to extensions can publish a versioned descriptor catalog as part of its SDK.
Give each application release a nested descriptor class and an entry point such as `MyHostServices.V1_0`.
Application release versions and individual service versions are independent: a new application release need not revise every service.

Version 1 of this application publishes two services, `Greeter` and `Calculator`, each initially using service version `0.1`:

[!code-csharp[](../../../samples/cs/brokered-service-versioning.cs#FutureProofServices_1_0)]

Declare the descriptor properties as <xref:Nerdbank.JsonRpc.BrokeredServices.IJsonRpcDescriptor`1>, rather than exposing concrete descriptor records.
Clients get a strongly typed contract and type inference for <xref:Nerdbank.JsonRpc.BrokeredServices.IServiceBroker.GetProxyAsync*?displayProperty=nameWithType>, without depending on the descriptor implementation or protocol configuration.
The catalog can choose different descriptor implementations, encodings, or other protocol settings for later service versions without changing the property's public type.
The host must still use matching settings for each published service version.

Version 2 derives its descriptor class from version 1, overrides only `Greeter` to request service version `0.2`, and adds a third service, `Weather`:

[!code-csharp[](../../../samples/cs/brokered-service-versioning.cs#FutureProofServices_2_0)]

The `virtual` properties establish the extension points; `override` selects a newer descriptor while preserving the property's contract type.
`Calculator` is inherited unchanged, so callers of `MyHostServices.V2_0.Calculator` still request version `0.1`.
Keep `MyHostServices.V1_0` unchanged: extensions compiled against version 1 continue to request the original service versions even when running in version 2 of the application.
Clients compiled against the newer SDK opt into the revised greeter and the weather service by using `MyHostServices.V2_0`; merely recompiling does not change their selection.
The host must continue registering and proffering the older monikers for those existing clients.

Centralize a client's catalog selection so adopting a new application release is a small, deliberate change:

[!code-csharp[](../../../samples/cs/brokered-service-versioning.cs#FutureProofClient)]

Changing the `AppVersion` initializer to `MyHostServices.V2_0` upgrades every existing call through that field to the version 2 catalog.
Because `V2_0Descriptors` derives from `V1_0Descriptors`, the field can retain its older declared type and virtual dispatch still selects the newer `Greeter` descriptor.
Change the field's declared type to `V2_0Descriptors` as well when the client needs newly added properties such as `Weather`.

The same inheritance pattern supports light-up scenarios: code can work with a `V1_0Descriptors` reference while selecting a newer derived catalog when the host supports it.
Detect host or service availability explicitly, and isolate references to newer SDK members if the client must also load with an older SDK assembly.
Catalog inheritance does not negotiate versions automatically or make newer services available on older hosts.
