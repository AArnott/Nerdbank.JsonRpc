# Features

## Core protocol

Nerdbank.JsonRpc implements the JSON-RPC request/response and notification model over MessagePack with strongly typed server dispatch.

Current highlights:

- Typed request and notification APIs on `JsonRpc`
- Server target registration based on PolyType method shapes
- Cancellation propagation using `$/cancelRequest`
- Pipe-based message transport via `StreamingJsonRpcMessageChannel`

## Generated client proxies

The repository now includes an experimental client proxy generator driven by `[GenerateJsonRpcProxy]` on an interface contract.

The current prototype intentionally does not rely on dynamic shape resolution. Instead, the generated proxy accepts an explicit `ITypeShapeProvider` instance at construction time and uses that provider for argument and result serialization.

That means the consumer flow is:

1. Declare the RPC interface and annotate it for PolyType shape generation.
2. Let the JsonRpc source generator emit the proxy implementation.
3. Pass a PolyType-generated `ITypeShapeProvider` instance into the proxy constructor.

This design proves a reflection-free path for generated proxies, while leaving room for a later convenience layer that hides the explicit provider parameter.
