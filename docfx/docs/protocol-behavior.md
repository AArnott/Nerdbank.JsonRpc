# Protocol behavior

## Failures

Both transports reject invalid JSON-RPC envelopes or batches, log the error, and fault the connection and all pending outbound requests (including requests queued in a batch). They do not try to match a malformed response without a trustworthy `id` to an individual request. Invalid framing also faults the connection; see [Encodings and framing](encodings.md).

When the envelope is valid but a response value cannot be converted to the expected return type, only that request fails; other calls can continue. When request arguments cannot be converted, the server returns an `InvalidParams` error with the original request `id`. A notification receives no reply. Application exception details are not returned to peers.

## Request IDs

Outbound request IDs are monotonically increasing integers. Inbound requests may use integer, string, or explicitly null/nil IDs; responses preserve the ID's type and value. Only an *omitted* `id` denotes a notification. Sequential requests may reuse an explicitly null/nil ID after the earlier request has completed. JSON numeric IDs are supported in the signed and unsigned 64-bit integer ranges; fractional and exponent-form IDs are rejected rather than coerced.

## Cancellation

Cancellation propagates using `$/cancelRequest`. For a [batched request](batching.md), cancellation before sending omits that entry, while cancellation after sending uses the cancellation notification. Generated clients pass one cancellation token for the complete [argument set](client-proxies.md).

## Envelope extension properties

Messages may carry additional top-level envelope properties beyond those defined by JSON-RPC 2.0. String and signed 64-bit integer values are preserved for internal use. Other value types, and integers outside that range, are ignored. Duplicate extension properties are rejected as malformed. Well-known extension properties with a value of the wrong type are also rejected.

## Deadlock mitigation with JoinableTaskFactory

A process with a main thread can set JsonRpc.JoinableTaskFactory. This prevents deadlocks when a remote party must call back into the process while its main thread waits on a request, which works when both parties participate or when they are separated by intermediaries that do not use a JoinableTaskFactory. It interoperates with StreamJsonRpc.

- Outbound requests (not notifications) made within a JoinableTask carry its token in the top-level joinableTaskToken string property.
- An inbound request with a token is dispatched within a JoinableTask joined to it, so the handler can reach the main thread that the original caller blocked.
- A JsonRpc instance without a JoinableTaskFactory forwards a received token to requests it makes while servicing that request, even through other JsonRpc instances that share its JoinableTaskTracker. All instances share one tracker by default; assign a new JoinableTaskTokenTracker to isolate connections.

Both properties must be set before calling Start.
