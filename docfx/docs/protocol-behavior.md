# Protocol behavior

## Failures

Both transports reject invalid JSON-RPC envelopes or batches, log the error, and fault the connection and all pending outbound requests (including requests queued in a batch). They do not try to match a malformed response without a trustworthy `id` to an individual request. Invalid framing also faults the connection; see [Encodings and framing](encodings.md).

When the envelope is valid but a response value cannot be converted to the expected return type, only that request fails; other calls can continue. When request arguments cannot be converted, the server returns an `InvalidParams` error with the original request `id`. A notification receives no reply. Application exception details are not returned to peers.

## Request IDs

Outbound request IDs are monotonically increasing integers. Inbound requests may use integer, string, or explicitly null/nil IDs; responses preserve the ID's type and value. Only an *omitted* `id` denotes a notification. Sequential requests may reuse an explicitly null/nil ID after the earlier request has completed. JSON numeric IDs are supported in the signed and unsigned 64-bit integer ranges; fractional and exponent-form IDs are rejected rather than coerced.

## Cancellation

Cancellation propagates using `$/cancelRequest`. For a [batched request](batching.md), cancellation before sending omits that entry, while cancellation after sending uses the cancellation notification. Generated clients pass one cancellation token for the complete [argument set](client-proxies.md).
