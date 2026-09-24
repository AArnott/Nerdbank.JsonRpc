# Batching

Use <xref:Nerdbank.JsonRpc.JsonRpc.CreateBatch> to send several JSON-RPC requests or notifications in one protocol payload. The resulting <xref:Nerdbank.JsonRpc.JsonRpcBatch> supports <xref:Nerdbank.JsonRpc.JsonRpcBatch.RequestAsync*>, <xref:Nerdbank.JsonRpc.JsonRpcBatch.NotifyAsync*>, and <xref:Nerdbank.JsonRpc.JsonRpcBatch.Attach*>. Requests return tasks immediately, but nothing is transmitted until <xref:Nerdbank.JsonRpc.JsonRpcBatch.SendAsync*> seals and queues the batch:

[!code-csharp[](../../samples/cs/GettingStarted.cs#sending-batch)]

Batch execution follows JSON-RPC semantics:

- Batches are not transactional. The peer may process entries concurrently, independently, and in any order.
- Responses may arrive in any order and are matched by `id`, not array position. Each request completes with its own result or <xref:Nerdbank.JsonRpc.JsonRpcException>; <xref:Nerdbank.JsonRpc.JsonRpcBatch.SendAsync*> only reports local submission failure.
- Notifications do not produce responses; a notification-only batch should produce no response payload.
- Empty batches are rejected locally. Adding entries or sending again after <xref:Nerdbank.JsonRpc.JsonRpcBatch.SendAsync*> fails.
- Disposing an unsent batch cancels pending request tasks. Per-request cancellation before sending omits that entry; after sending, cancellation uses `$/cancelRequest`.

Generated proxies attach to either a <xref:Nerdbank.JsonRpc.JsonRpc> connection or a <xref:Nerdbank.JsonRpc.JsonRpcBatch>, so one contract works for ordinary and batched calls. Servers need no additional registration or methods: requests use the same server dispatch path. Since entries may execute concurrently, targets sharing mutable state should be safe for concurrent calls.

For request ID and failure behavior, see [Protocol behavior](protocol-behavior.md).
