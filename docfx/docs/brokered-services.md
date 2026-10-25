# Brokered services

Brokered services separate a service's contract and identity from where it is implemented.
Clients request a typed descriptor from a broker rather than constructing implementation classes or choosing transports.
Hosts supply factories, exposure policies, and optional on-demand loading.
Both direct local calls and remote calls use source-generated proxies to provide as consistent an experience as possible.

The APIs live in <xref:Nerdbank.JsonRpc.BrokeredServices>, in the existing `Nerdbank.JsonRpc` package.
General IPC helpers live in the root `Nerdbank.JsonRpc` namespace.
The package includes [proxy ownership](../analyzers/NBJSONRPC004.md) and [rental lifetime](../analyzers/NBJSONRPC005.md) analyzers to help prevent resource leaks.

## Guide

Start with [basic client and server setup](brokered-services/getting-started.md), then use the topics below to build out your host and contracts.
The same acquisition pattern works for local and remote services.

| Topic | What you will learn |
| --- | --- |
| [Basic client and server setup](brokered-services/getting-started.md) | Define contracts and descriptors, proffer a factory, and acquire and dispose a proxy. |
| [On-demand proffering](brokered-services/on-demand.md) | Register catalog metadata at startup and load and proffer implementations on the first request. |
| [Optional interfaces](brokered-services/optional-interfaces.md) | Generate proxy groups, describe potential capabilities, advertise an implementation's subset, and query support. |
| [Versioning services](brokered-services/versioning.md) | Publish versioned monikers and route them to one version-aware implementation or distinct implementations. |
| [Host policy and extensibility](brokered-services/hosting.md) | Filter service audiences, manage offering availability, and customize discovery, routing, and diagnostics. |
| [Lifetimes, caching, and resilience](brokered-services/lifetimes.md) | Understand direct-call boundaries, share activations safely, and recover from service replacement or connection loss. |
| [Remote brokers and IPC](brokered-services/remote-brokers.md) | Host and consume cross-process services, configure forwardable multiplexing, and use same-account IPC helpers. |

## Contract, offering, and activation

A descriptor publishes a service's moniker, wire settings, generated contract shape, and known optional interfaces.
A catalog registration declares a moniker, its audience and actual optional capability IDs, optionally with a loader for its implementation.
Proffering supplies a descriptor and factory for an already registered service.
An activation is the client-owned instance created by that factory.

Keep these boundaries separate: discovering a service need not construct it, knowing an optional contract does not promise it is implemented, and withdrawing a factory does not destroy activations that clients already own.
