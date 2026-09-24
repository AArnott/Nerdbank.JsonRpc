# Features

## Core protocol

Nerdbank.JsonRpc implements the JSON-RPC request/response and notification model over MessagePack or UTF-8 JSON with strongly typed server dispatch. MessagePack remains the default encoding.

Current highlights:

- Typed request and notification APIs on `JsonRpc`
- Server target registration based on PolyType method shapes
- Cancellation propagation using `$/cancelRequest`
- Pipe-based MessagePack transport via <xref:Nerdbank.JsonRpc.StreamingJsonRpcMessageChannel>, or JSON transport via <xref:Nerdbank.JsonRpc.JsonRpcJsonChannel>
- One-shot JSON-RPC batch payloads with per-request result/error completion


## Selecting an encoding

MessagePack is the default. To use JSON, configure a `Nerdbank.Json.JsonSerializer`, pass it to the JSON pipe channel, and assign **the same instance** to `JsonRpc.Serializer` before calling `Start`:

[!code-csharp[](../../samples/cs/GettingStarted.cs#json-encoding)]

`JsonRpcJsonFraming.NewlineDelimited` sends one compact JSON object or batch per line; `ContentLength` sends `Content-Length: <UTF-8 byte count>\r\n\r\n` followed by exactly that many bytes. Both peers must agree on the framing; neither endpoint sniffs the input or switches codecs mid-connection. An empty frame, oversized frame (more than 8 MiB), malformed header, or incomplete frame faults the connection. To customize MessagePack instead, assign your `MessagePackSerializer` to `JsonRpc.Serializer` before starting; it is wrapped automatically. The wrappers (`JsonSerializerPlugin` and `MessagePackSerializerPlugin`) retain the exact configured serializer instances.

Application payloads (`JsonRpcRequest.Arguments`, `JsonRpcResult.Result`, and optional `JsonRpcErrorDetails.Data`) use `JsonRpcValue`: an owned, encoding-tagged, **already serialized** value. Create one with `JsonRpcValue.FromJson(utf8)` or `JsonRpcValue.FromMessagePack(raw)`; both factories copy the bytes without validating them, so callers must supply exactly one complete value. Invalid JSON fails when the value is written to a JSON frame; invalid MessagePack may yield a malformed frame. `AsMessagePack()` rejects JSON, and `Bytes` returns a copy. A default value means omitted `params` (or absent error data); an encoded JSON `null` or MessagePack nil is *present*. A result must always have a value. Sending a value tagged for the wrong codec is rejected, not transcoded. Existing code using `RawMessagePack` can use the MessagePack bridge explicitly. Typed calls and generated proxies use the selected serializer and PolyType shapes automatically; generated proxies send arrays by default or objects when `UseNamedArguments = true`.

NuGet references both serializer packages. On a MessagePack-only deployment, `Nerdbank.Json.dll` can be excluded without loading it. Currently Nerdbank.Json itself loads Nerdbank.MessagePack, so the inverse deployment exclusion is not supported.


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

Both transports reject an invalid JSON-RPC envelope or batch, log the error, and fault the connection and every pending request (including requests queued in a batch). Neither transport tries to match a malformed response to a request without a trustworthy `id`. A valid response with a value that cannot be converted to the expected return type instead fails only that request; other calls can continue. A valid request whose arguments cannot be converted receives an `InvalidParams` error with its original `id`; a notification receives no reply. Application exception details are not returned to peers.

Outbound request IDs are increasing integers. Inbound requests may use integer, string, or explicitly null/nil IDs; responses echo the same kind and value. Only an *omitted* `id` denotes a notification. Sequential requests may reuse an explicitly null/nil ID once the earlier request has completed. JSON numeric IDs are supported in the signed 64-bit and unsigned 64-bit integer ranges; fractional and exponent-form IDs are rejected rather than coerced.

## Generated client proxies

The repository now includes an experimental client proxy generator driven by `[GenerateJsonRpcProxy]` on an interface contract.

The current prototype intentionally does not require users to manually instantiate generated proxy classes. `JsonRpc.Attach<T>(JsonRpcProxyOptions? options = null)` reads generated metadata from the RPC interface and creates the matching proxy for the current connection.

Supported generated method shapes currently include:

- `ValueTask<T>`
- `Task<T>`
- `ValueTask`
- `Task`
- `void` notifications

Argument packing defaults to positional arrays in the selected encoding. If a contract needs named arguments instead, apply `[GenerateJsonRpcProxy(UseNamedArguments = true)]` to emit an object/map keyed by parameter name.

The consumer flow is:

1. Declare the RPC interface and annotate it for PolyType shape generation.
2. Let the JsonRpc source generator emit the proxy implementation.
3. Attach the proxy with `rpc.Attach<IMyContract>()`.

For a proxy that implements multiple RPC interfaces, define an annotated composite interface and request that composite type. `Attach<IBase>()` only uses generated metadata on `IBase`; it does not search for composite proxies that happen to implement that base interface.
