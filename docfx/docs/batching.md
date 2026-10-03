# Batching

Use <xref:Nerdbank.JsonRpc.JsonRpc.CreateBatch> to send several JSON-RPC requests or notifications in one protocol payload. The resulting <xref:Nerdbank.JsonRpc.JsonRpcBatch> supports <xref:Nerdbank.JsonRpc.JsonRpcBatch.RequestAsync*>, <xref:Nerdbank.JsonRpc.JsonRpcBatch.NotifyAsync*>, and <xref:Nerdbank.JsonRpc.JsonRpcBatch.Attach*>. Requests return awaitables immediately, but nothing is transmitted until <xref:Nerdbank.JsonRpc.JsonRpcBatch.SendAsync*> seals and queues the batch:

[!code-csharp[](../../samples/cs/batching.cs#sending-batch)]

## Consuming batched request results

Queue all requests, send the batch, and then await the saved results. **Do not await a queued request before sending the batch.** The request normally cannot complete until it is sent, so awaiting it before reaching SendAsync prevents progress. This applies equally to Task- and ValueTask-returning methods. SendAsync reports local submission failure, not completion of the remote requests.

For a small, fixed batch, storing each <xref:System.Threading.Tasks.ValueTask`1> and awaiting it once after sending adds little ceremony, as shown above. For dynamic batches or aggregate completion, convert each ValueTask with <xref:System.Threading.Tasks.ValueTask`1.AsTask?displayProperty=nameWithType> once and use <xref:System.Threading.Tasks.Task.WhenAll*?displayProperty=nameWithType> after sending:

[!code-csharp[](../../samples/cs/batching.cs#dynamic-batch-results)]

Task.WhenAll waits for all supplied operations to finish even if one fails. In contrast, an exception from the first of several sequential awaits can leave later results unobserved. Retain the converted Tasks if you need to inspect individual failures or results; do not await or convert the original ValueTasks again. Task-returning RPC interfaces are also a reasonable choice for applications that frequently compose batch results. See [Choosing request return types](client-proxies.md#choosing-request-return-types).

Discarding a request's awaitable does not cancel it or send the batch. Dispose an unsent batch to cancel queued requests and release their arguments. Disposing a sent batch does not cancel in-flight requests; observe their failures and manage any returned resources according to their contracts.

## Execution semantics

Batch execution follows JSON-RPC semantics:

- Batches are not transactional. The peer may process entries concurrently, independently, and in any order.
- Responses may arrive in any order and are matched by `id`, not array position. Each request completes with its own result or <xref:Nerdbank.JsonRpc.JsonRpcException>; <xref:Nerdbank.JsonRpc.JsonRpcBatch.SendAsync*> only reports local submission failure.
- Notifications do not produce responses; a notification-only batch should produce no response payload.
- Empty batches are rejected locally. Adding entries or sending again after <xref:Nerdbank.JsonRpc.JsonRpcBatch.SendAsync*> fails.
- Disposing an unsent batch cancels pending request tasks. Per-request cancellation before sending omits that entry; after sending, cancellation uses `$/cancelRequest`.

Generated proxies attach to either a <xref:Nerdbank.JsonRpc.JsonRpc> connection or a <xref:Nerdbank.JsonRpc.JsonRpcBatch>, so one contract works for ordinary and batched calls. Servers need no additional registration or methods: requests use the same server dispatch path. Since entries may execute concurrently, targets sharing mutable state should be safe for concurrent calls.

For request ID and failure behavior, see [Protocol behavior](protocol-behavior.md).

## Connection shutdown

Once sent, registered batch requests follow the same [connection lifecycle](protocol-behavior.md#connection-lifecycle) as ordinary and reverse requests: shutdown faults pending entries with the original cause (or ObjectDisposedException for deliberate disposal). Unsent entries are not counted as pending outbound requests; attempting to submit them after shutdown fails with the shutdown cause. A later user call to Dispose overrides subsequent batch submissions with ObjectDisposedException without erasing the original connection-loss reason.
