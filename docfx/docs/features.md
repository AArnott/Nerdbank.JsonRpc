# Features

## Core protocol

Nerdbank.JsonRpc implements the JSON-RPC request/response and notification model over MessagePack with strongly typed server dispatch.

Current highlights:

- Typed request and notification APIs on `JsonRpc`
- Server target registration based on PolyType method shapes
- Cancellation propagation using `$/cancelRequest`
- Pipe-based message transport via <xref:Nerdbank.JsonRpc.StreamingJsonRpcMessageChannel>
- One-shot JSON-RPC batch payloads with per-request result/error completion


## Batching

<xref:Nerdbank.JsonRpc.JsonRpc.CreateBatch> creates a one-shot <xref:Nerdbank.JsonRpc.JsonRpcBatch>. It mirrors `RequestAsync`, `NotifyAsync`, and `Attach<T>`. Each request returns its normal <xref:System.Threading.Tasks.ValueTask> immediately, but the request is not transmitted until `SendAsync` seals the batch and queues one protocol payload.

Batch execution follows JSON-RPC semantics:

- Batches are not transactional, and the peer may process entries concurrently, independently, and in any order.
- Responses may arrive in any order and are matched to requests by `id`.
- Each request completes with its own result or <xref:Nerdbank.JsonRpc.JsonRpcException>; `SendAsync` only reports local submission failure.
- Notifications in a batch do not produce responses, and notification-only batches should produce no response payload.
- Empty batches are rejected locally.
- Adding entries or sending again after `SendAsync` fails.
- Disposing an unsent batch cancels pending request tasks.
- Per-request cancellation before `SendAsync` omits that entry. Cancellation after `SendAsync` uses the existing `$/cancelRequest` notification.

Generated proxies can be attached to either a <xref:Nerdbank.JsonRpc.JsonRpc> instance or a <xref:Nerdbank.JsonRpc.JsonRpcBatch>, so callers can use the same contract interface for ordinary and batched calls.

## Generated client proxies

The repository now includes an experimental client proxy generator driven by `[GenerateJsonRpcProxy]` on an interface contract.

The current prototype intentionally does not require users to manually instantiate generated proxy classes. `JsonRpc.Attach<T>(JsonRpcProxyOptions? options = null)` reads generated metadata from the RPC interface and creates the matching proxy for the current connection.

Supported generated method shapes currently include:

- `ValueTask<T>`
- `Task<T>`
- `ValueTask`
- `Task`
- `void` notifications

Argument packing defaults to positional MessagePack arrays. If a contract needs named arguments instead, apply `[GenerateJsonRpcProxy(UseNamedArguments = true)]` to emit a map keyed by parameter name.

The consumer flow is:

1. Declare the RPC interface and annotate it for PolyType shape generation.
2. Let the JsonRpc source generator emit the proxy implementation.
3. Attach the proxy with `rpc.Attach<IMyContract>()`.

For a proxy that implements multiple RPC interfaces, define an annotated composite interface and request that composite type. `Attach<IBase>()` only uses generated metadata on `IBase`; it does not search for composite proxies that happen to implement that base interface.
