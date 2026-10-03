# Protocol behavior

## Failures

Both transports reject invalid JSON-RPC envelopes or batches, log the error, and fault the connection and all pending outbound requests (including requests queued in a batch). They do not try to match a malformed response without a trustworthy `id` to an individual request. Invalid framing also faults the connection; see [Encodings and framing](encodings.md).

When the envelope is valid but a response value cannot be converted to the expected return type, only that request fails; other calls can continue. When request arguments cannot be converted, the server returns an `InvalidParams` error with the original request `id`. A notification receives no reply. Application exception details are not returned to peers.

## Connection lifecycle

A connection is single-use. Local disposal, EOF, an unrecoverable protocol rejection, or a transport failure initiates automatic teardown: input and new application sends stop, target events are unsubscribed, inbound handlers are canceled, marshaled relationships and owned objects are released, out-of-band channels are closed, and asynchronous generator disposal is started. Generator cleanup runs independently so an enumerator that ignores cancellation cannot keep <xref:Nerdbank.JsonRpc.JsonRpc.Completion?displayProperty=nameWithType> pending indefinitely. The application still owns its supplied transport stream and multiplexing stream; manage those lifetimes as described in [Getting started](getting-started.md).

<xref:Nerdbank.JsonRpc.JsonRpc.Completion?displayProperty=nameWithType> completes after the connection's teardown attempt. Pending calls can fail before teardown finishes. Successful completion means the lifetime has ended, **not** that this connection can be reused, and does not imply all notifications accepted earlier were transmitted.

| Original shutdown reason | Completion | Registered pending outbound calls | Subsequent requests and notifications, before a user calls Dispose | State |
| --- | --- | --- | --- | --- |
| Deliberate local disposal | Succeeds | <xref:System.ObjectDisposedException> | <xref:System.ObjectDisposedException> | Disposed |
| EOF between messages, with no pending outbound calls | Succeeds | None | <xref:System.IO.EndOfStreamException> | Disconnected |
| EOF between messages, with pending outbound calls | Faults with <xref:System.IO.EndOfStreamException>; its message records the pending outbound count | Same EOF exception | <xref:System.IO.EndOfStreamException> | Disconnected |
| Unrecoverable protocol violation | Faults with <xref:System.Net.ProtocolViolationException> | Same protocol exception | <xref:System.Net.ProtocolViolationException> | Faulted |
| Transport or other connection failure | Faults with the original exception | Same exception type and cause | Original shutdown exception | Faulted |

The pending count is captured atomically when shutdown wins the race, and includes ordinary requests, requests registered by a sent [batch](batching.md), and reverse calls made while servicing a peer's request. Calls whose responses were already accepted are not pending. Entries in an unsent batch are not yet outbound requests; submitting that batch after shutdown fails with the shutdown exception.

EOF midway through a frame is **not** successful idle EOF. Truncated JSON frames fail with <xref:System.Net.ProtocolViolationException>; truncated MessagePack frames fail with <xref:System.IO.EndOfStreamException>. Both leave the connection Faulted, even without pending calls. [Encoding and framing](encodings.md) describe message boundaries and size limits.

### Inspecting the original reason

<xref:Nerdbank.JsonRpc.JsonRpc.IsDisposed?displayProperty=nameWithType> becomes true for both deliberate and automatic shutdown. <xref:Nerdbank.JsonRpc.JsonRpc.State?displayProperty=nameWithType> preserves the original shutdown mode: Disposed for deliberate disposal, Disconnected for ordinary EOF, and Faulted for other failures. Inspect <xref:Nerdbank.JsonRpc.JsonRpc.TerminationException?displayProperty=nameWithType> for the original connection-loss cause, including EOF when Completion succeeded. It is null before connection loss and when deliberate disposal occurred without prior connection loss.

Calling <xref:Nerdbank.JsonRpc.JsonRpc.Dispose> after automatic shutdown changes **later calls** to throw <xref:System.ObjectDisposedException>. It does not erase TerminationException, change the original State, change the exception already delivered to pending calls, or replace Completion's outcome. Cleanup runs only once. The first terminal transition wins races against responses, cancellation, disposal, or a second failure. Secondary notification-delivery and cleanup errors are logged without replacing the primary exception.

For example, observe the lifetime and retain the reason before disposing the transport:

[!code-csharp[](../../samples/cs/connection-lifecycle.cs#observing-completion)]

Catch these same exception types when invoking a proxy, awaiting a request or a sent batch entry, or submitting a notification. Depending on the overload, rejection of a call made after shutdown may be synchronous, so include the invocation itself inside the `try` block. A notification's normal awaitable reports queue acceptance, not delivery. Marshaled proxies and asynchronous sequence consumers also check the connection cause before making further calls; a user Dispose overrides that cause with ObjectDisposedException. Out-of-band streams are closed during teardown; their individual stream/pipe APIs retain their own end-of-stream/disposal semantics rather than acting as RPC requests.

A received RPC error is a request-level <xref:Nerdbank.JsonRpc.JsonRpcException>, not proof of connection loss. Invalid parameters, unreadable return values, and cooperative request cancellation do not themselves end the connection. Do not interpret a local framing or I/O exception as an exception thrown by a remote target, or combine unrelated local and remote stacks.

### Protocol rejection notification

Before disconnecting for a locally detected protocol violation, the connection stops reading and dispatching incoming messages and rejects new application sends. It then makes a best-effort attempt to send and **flush** the reserved `$/protocolViolation` notification using the connection's encoding and framing. This is a notification with no `id`. Its parameters carry one `reason`: a named property for JSON and a key-0 value for MessagePack. For example:

[!code-json[](../../samples/protocol-violation.json)]

The reason is the full protocol-violation exception message, without a whitelist, generic substitution, or diagnostic-specific truncation. This preserves details such as rejected request IDs, invalid lengths, and JSON parser locations. Exception messages may quote peer-supplied data; consider this when controlling access to diagnostics. Raw argument/result payloads and stack traces are not separately attached. Queued application messages that have not started transmission may be abandoned so they do not delay the diagnostic. A write already in progress may prevent transmission.

The final send/flush attempt has a one-second budget. If output is closed, blocked, canceled, or otherwise fails, the connection logs the delivery failure and disconnects anyway. The original ProtocolViolationException remains the cause seen by callers. This is not reliable delivery: a broken transport, incompatible framing, very small message-size limit, or an uncooperative peer can prevent receipt.

A supporting peer handles this notification internally, ahead of user target dispatch, and logs the full explanation at **Critical** severity as **peer-reported** evidence. Receipt does not proactively terminate the connection: the peer decides whether its reported violation warrants disconnecting, and the connection remains usable if the peer keeps it open. It does not echo the notification, invoke an application target, or treat the report as an independently validated local protocol violation. The sender's eventual disconnect is still observed as EOF, with the usual pending-call policy. Other JSON-RPC implementations may ignore the extension. Inspect logs on both peers when delivery succeeds.

### Logging and causal information

Configure <xref:Nerdbank.JsonRpc.JsonRpc.Logger?displayProperty=nameWithType> before starting the connection. Shutdown logs include the original state, encoding, pending outbound count, and exception; transport logs identify inbound versus outbound failure. Use your logger's scopes or a connection-specific logger to attach a safe session label. Do not put secrets in labels. Message logs identify the envelope type without logging argument/result contents.

[!code-csharp[](../../samples/cs/connection-lifecycle.cs#connection-logging)]

Transport exceptions retain their causal chains. Because ProtocolViolationException has no public inner-exception constructor, a JSON syntax rejection retains the original <xref:System.Text.Json.JsonException> in its <xref:System.Exception.Data> under `ParserException`; the local transport logs that exception separately as well. The parser message is included in the protocol rejection message transmitted to the peer. Cleanup errors are logged independently of the original cause.

## Request IDs

Outbound request IDs are monotonically increasing integers. Inbound requests may use integer, string, or explicitly null/nil IDs; responses preserve the ID's type and value. Only an *omitted* `id` denotes a notification. Sequential requests may reuse an explicitly null/nil ID after the earlier request has completed. JSON numeric IDs are supported in the signed and unsigned 64-bit integer ranges; fractional and exponent-form IDs are rejected rather than coerced.

## Cancellation

Cancellation propagates using `$/cancelRequest`. For a [batched request](batching.md), cancellation before sending omits that entry, while cancellation after sending uses the cancellation notification. Generated clients pass one cancellation token for the complete [argument set](client-proxies.md).

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
