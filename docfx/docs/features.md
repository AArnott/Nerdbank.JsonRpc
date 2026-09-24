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

<xref:Nerdbank.JsonRpc.JsonRpc.CreateBatch> creates a one-shot <xref:Nerdbank.JsonRpc.JsonRpcBatch>. It mirrors <xref:Nerdbank.JsonRpc.JsonRpcBatch.RequestAsync*>, <xref:Nerdbank.JsonRpc.JsonRpcBatch.NotifyAsync*>, and <xref:Nerdbank.JsonRpc.JsonRpcBatch.Attach*>. Each request returns its normal <xref:System.Threading.Tasks.ValueTask> immediately, but the request is not transmitted until <xref:Nerdbank.JsonRpc.JsonRpcBatch.SendAsync*> seals the batch and queues one protocol payload.

Batch execution follows JSON-RPC semantics:

- Batches are not transactional, and the peer may process entries concurrently, independently, and in any order.
- Responses may arrive in any order and are matched to requests by `id`.
- Each request completes with its own result or <xref:Nerdbank.JsonRpc.JsonRpcException>; <xref:Nerdbank.JsonRpc.JsonRpcBatch.SendAsync*> only reports local submission failure.
- Notifications in a batch do not produce responses, and notification-only batches should produce no response payload.
- Empty batches are rejected locally.
- Adding entries or sending again after <xref:Nerdbank.JsonRpc.JsonRpcBatch.SendAsync*> fails.
- Disposing an unsent batch cancels pending request tasks.
- Per-request cancellation before <xref:Nerdbank.JsonRpc.JsonRpcBatch.SendAsync*> omits that entry. Cancellation after <xref:Nerdbank.JsonRpc.JsonRpcBatch.SendAsync*> uses the existing `$/cancelRequest` notification.

Generated proxies can be attached to either a <xref:Nerdbank.JsonRpc.JsonRpc> instance or a <xref:Nerdbank.JsonRpc.JsonRpcBatch>, so callers can use the same contract interface for ordinary and batched calls.

Servers do not need special target methods or registration changes to support batching. Batched requests are dispatched through the same server method binding path as ordinary requests; the server-side consideration is simply that independent batch entries may be processed concurrently, so target objects that share mutable state should already be safe for concurrent calls.

## Protocol failures and request IDs

The MessagePack transport rejects an invalid JSON-RPC envelope or batch, logs the error, and faults the connection and every pending request (including requests queued in a batch). It does not try to match a malformed response to a request without a trustworthy `id`. A valid response with a value that cannot be converted to the expected return type instead fails only that request; other calls can continue. A valid request whose arguments cannot be converted receives an `InvalidParams` error with its original `id`; a notification receives no reply. Application exception details are not returned to peers.

Outbound request IDs are increasing integers. Inbound requests may use integer, string, or explicitly nil IDs; responses echo the same kind and value. Only an *omitted* `id` denotes a notification. Sequential requests may reuse an explicitly nil ID once the earlier request has completed.

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
