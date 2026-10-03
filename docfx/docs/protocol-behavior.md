# Protocol behavior

## Failures

Both transports reject invalid JSON-RPC envelopes or batches, log the error, and fault the connection and all pending outbound requests (including requests queued in a batch). They do not try to match a malformed response without a trustworthy `id` to an individual request. Invalid framing also faults the connection; see [Encodings and framing](encodings.md).

When the envelope is valid but a response value cannot be converted to the expected return type, only that request fails; other calls can continue. When request arguments cannot be converted, the server returns an `InvalidParams` error with the original request `id`. A notification receives no reply. Application exception details are not returned to peers.

## Request IDs

Outbound request IDs are monotonically increasing integers. Inbound requests may use integer, string, or explicitly null/nil IDs; responses preserve the ID's type and value. Only an *omitted* `id` denotes a notification. Sequential requests may reuse an explicitly null/nil ID after the earlier request has completed. JSON numeric IDs are supported in the signed and unsigned 64-bit integer ranges; fractional and exponent-form IDs are rejected rather than coerced.

## Cancellation

Cancellation propagates using `$/cancelRequest`. For a [batched request](batching.md), cancellation before sending omits that entry, while cancellation after sending uses the cancellation notification. Generated clients pass one cancellation token for the complete [argument set](client-proxies.md).

Outbound requests that complete with <xref:System.OperationCanceledException> include the caller's cancellation token only if that token is canceled; otherwise their exception uses <xref:System.Threading.CancellationToken.None>. This also applies to direct and batched requests made through generated clients.

A peer's `RequestCancelled` response produces <xref:System.OperationCanceledException>, not <xref:Nerdbank.JsonRpc.JsonRpcException>. If the caller's token is canceled when the response is processed, the exception includes that token and the peer's cancellation message. Otherwise, it omits the caller's token and explains that the remote party canceled processing without the caller requesting cancellation. The original remote error details and message are retained in a <xref:Nerdbank.JsonRpc.JsonRpcException> inner exception. Other remote error responses still produce <xref:Nerdbank.JsonRpc.JsonRpcException>.

Canceling a token after a request has been sent requests remote cancellation; it does not complete the request before the peer responds. Unsent batched requests may be canceled locally by their tokens, disposing the batch, or calling <xref:Nerdbank.JsonRpc.JsonRpcBatch.CancelAllAsync>.

## Envelope extension properties

Messages may carry additional top-level envelope properties beyond those defined by JSON-RPC 2.0. String and signed 64-bit integer values are preserved for internal use. Other value types, and integers outside that range, are ignored. Duplicate extension properties are rejected as malformed. Well-known extension properties with a value of the wrong type are also rejected.

## Dispatch order and concurrency

Inbound requests are dispatched so that their handlers *begin* executing in the order the remote party sent them. <xref:Nerdbank.JsonRpc.JsonRpc.SynchronizationContext?displayProperty=nameWithType> controls this and defaults to a non-sticky <xref:Microsoft.VisualStudio.Threading.NonConcurrentSynchronizationContext>, which starts each invocation on the thread pool, one at a time, in arrival order.

Because that context is non-sticky, it does not become the current synchronization context while a handler runs. As soon as a handler yields at its first `await` (or returns), the next queued handler starts. Long-running handlers therefore overlap and may complete in any order; only their *start* order is guaranteed.

- Initialize the property to `null` to drop the ordering guarantee. Each invocation is then queued to the thread pool independently, so handlers may start in any order and run with full concurrency. This offers the highest throughput when start order does not matter.
- Initialize the property with your own <xref:System.Threading.SynchronizationContext>, such as one that marshals to an application's main thread, to start every handler there. A context that runs callbacks one at a time keeps the ordering guarantee; one that runs them concurrently does not.

Request parsing and cancellation bookkeeping always run on the reader loop in message order and are unaffected by this property. The property is init-only, so it is set in the object initializer when constructing <xref:Nerdbank.JsonRpc.JsonRpc>.

Inbound `$/cancelRequest` notifications are exempt and always begin on the thread pool. Because their purpose is to interrupt work that is already running, queueing them behind that work would prevent a handler that occupies the dispatcher without yielding from ever being canceled.

## Deadlock mitigation with JoinableTaskFactory

A process with a main thread can set JsonRpc.JoinableTaskFactory. This prevents deadlocks when a remote party must call back into the process while its main thread waits on a request, which works when both parties participate or when they are separated by intermediaries that do not use a JoinableTaskFactory. It interoperates with StreamJsonRpc.

- Outbound requests (not notifications) made within a JoinableTask carry its token in the top-level joinableTaskToken string property.
- An inbound request with a token is dispatched within a JoinableTask joined to it, so the handler can reach the main thread that the original caller blocked.
- A JsonRpc instance without a JoinableTaskFactory forwards a received token to requests it makes while servicing that request, even through other JsonRpc instances that share its JoinableTaskTracker. All instances share one tracker by default; assign a new JoinableTaskTokenTracker to isolate connections.

Both properties must be set before calling Start.
