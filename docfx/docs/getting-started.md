# Getting Started

## Installation

Consume Nerdbank.JsonRpc via its NuGet package. The badge links to the latest version and installation instructions.

[![NuGet package](https://img.shields.io/nuget/v/Nerdbank.JsonRpc.svg)](https://www.nuget.org/packages?q=Nerdbank.JsonRpc)

## Server setup

Annotate your contract for PolyType method-shape generation and register an implementation with <xref:Nerdbank.JsonRpc.JsonRpc>:

[!code-csharp[](../../samples/cs/getting-started.cs#server-setup)]

## Call the server

Annotate the contract with <xref:Nerdbank.JsonRpc.GenerateJsonRpcProxyAttribute> to generate a typed client:

[!code-csharp[](../../samples/cs/getting-started.cs#generated-client-proxy)]

Attach the proxy to a running <xref:Nerdbank.JsonRpc.JsonRpc> instance:

[!code-csharp[](../../samples/cs/getting-started.cs#attach-proxy)]

MessagePack is the default. For other setup choices, see [Encodings and framing](encodings.md). For named arguments and proxy options, see [Generated client proxies](client-proxies.md); to combine calls in one payload, see [Batching](batching.md). For how CLR method names map to wire names, see [Method name transforms](method-naming.md).
