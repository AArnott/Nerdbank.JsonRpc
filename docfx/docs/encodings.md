# Encodings and framing

Nerdbank.JsonRpc supports MessagePack (the default) and UTF-8 JSON. Configure the serializer on the channel for the chosen encoding before starting a connection. The RPC instance always uses its channel's serializer; it does not require separate configuration. Both peers must agree on the encoding and framing; the transport does not sniff input or switch codecs mid-connection.

## Select a serializer

For JSON, configure a <xref:Nerdbank.Json.JsonSerializer>, pass it to a <xref:Nerdbank.JsonRpc.JsonRpcJsonChannel>, and construct <xref:Nerdbank.JsonRpc.JsonRpc> with that channel. The RPC instance uses the channel's serializer. Set <xref:Nerdbank.JsonRpc.JsonRpc.Logger> in an object initializer to capture transport message events and failures:

[!code-csharp[](../../samples/cs/encodings.cs#json-encoding)]

## Logging and distributed tracing

Nerdbank.JsonRpc uses `Microsoft.Extensions.Logging` for transport message and failure events. Its <xref:Nerdbank.JsonRpc.JsonRpc.ActivitySource> emits client activities for outbound requests (including batched requests) and direct notifications, and server activities for dispatched requests. Subscribe with an `ActivityListener` or OpenTelemetry tracer provider using <xref:Nerdbank.JsonRpc.JsonRpc.ActivitySource>.Name; no activity is created when the source is not being sampled. Activities include the `rpc.system` and `rpc.method` tags and are marked as errors when an RPC fails.

When a W3C activity is current, its `traceparent` and optional `tracestate` are carried as top-level JSON-RPC envelope extension properties. Both built-in encodings preserve these fields, and peers that do not use distributed tracing can ignore them. This lets a server activity be a child of the caller's client activity across a process boundary.

To customize MessagePack, supply a configured <xref:Nerdbank.MessagePack.MessagePackSerializer> through the `serializer` parameter of <xref:Nerdbank.JsonRpc.JsonRpcMessagePackChannel>. Otherwise the channel uses <xref:Nerdbank.JsonRpc.JsonRpcMessagePackChannel.DefaultSerializer>. The same serializer instance handles MessagePack application values and protocol envelopes. Each channel exposes a <xref:Nerdbank.JsonRpc.JsonRpcPipeChannel.Serializer> plugin for typed values; <xref:Nerdbank.JsonRpc.JsonRpc> obtains it directly from the channel. By default, MessagePack messages use a 4-byte big-endian length header, compatible with StreamJsonRpc's <xref:StreamJsonRpc.LengthHeaderMessageHandler>. Both framing modes reject inbound frames larger than 8 MiB by default to bound memory use. Configure <xref:Nerdbank.JsonRpc.JsonRpc.MaximumMessageSize> to change this limit for the built-in JSON and MessagePack channels; the JSON channel also applies it to outbound messages. MessagePack additionally rejects batches or parameter collections with more than 65,536 entries. To connect to a peer using bare MessagePack values (including earlier Nerdbank.JsonRpc releases), explicitly pass <xref:Nerdbank.JsonRpc.JsonRpcMessagePackFraming.SelfDelimiting?displayProperty=nameWithType> to the channel constructor on the upgraded side. Both peers must use the same framing.

Replace uses of `StreamingJsonRpcMessageChannel` with <xref:Nerdbank.JsonRpc.JsonRpcMessagePackChannel>. Custom <xref:Nerdbank.JsonRpc.JsonRpcPipeChannel> subclasses must now provide <xref:Nerdbank.JsonRpc.JsonRpcPipeChannel.Encoding> and <xref:Nerdbank.JsonRpc.JsonRpcPipeChannel.Serializer>. Transport processing does not begin until <xref:Nerdbank.JsonRpc.JsonRpcPipeChannel.Start>, which <xref:Nerdbank.JsonRpc.JsonRpc.Start> calls automatically. Code that uses a channel directly, without <xref:Nerdbank.JsonRpc.JsonRpc>, must call <xref:Nerdbank.JsonRpc.JsonRpcPipeChannel.Start> itself. The former MessagePack-specific protected field and serialization helpers are not part of the encoding-neutral channel API.

## JSON framing

<xref:Nerdbank.JsonRpc.JsonRpcJsonFraming.NewlineDelimited> sends one compact JSON object or batch per line. <xref:Nerdbank.JsonRpc.JsonRpcJsonFraming.ContentLength> sends `Content-Length: <UTF-8 byte count>\r\n\r\n` followed by exactly that many bytes. Both peers must use the same framing mode. Empty or oversized frames (more than 8 MiB), malformed headers, and incomplete frames fault the connection. See [Protocol behavior](protocol-behavior.md) for the distinction between protocol and application-value failures.

## Already serialized values

Application payloads (<xref:Nerdbank.JsonRpc.JsonRpcRequest.Arguments>, <xref:Nerdbank.JsonRpc.JsonRpcResult.Result>, and optional <xref:Nerdbank.JsonRpc.JsonRpcErrorDetails.Data>) use <xref:Nerdbank.JsonRpc.JsonRpcValue>, an owned, encoding-tagged, **already serialized** value. Use <xref:Nerdbank.JsonRpc.JsonRpcValue.FromJson*> or <xref:Nerdbank.JsonRpc.JsonRpcValue.FromMessagePack*> to copy supplied bytes. These factories do **not** validate the bytes: supply exactly one complete encoded value. JSON params must be a complete array or object and are checked before queuing; other invalid JSON values may fail when written to a JSON frame. Invalid MessagePack may yield a malformed frame. <xref:Nerdbank.JsonRpc.JsonRpcValue.AsMessagePack*> rejects JSON, and <xref:Nerdbank.JsonRpc.JsonRpcValue.Bytes> returns a copy. Code using <xref:Nerdbank.MessagePack.RawMessagePack> can convert through <xref:Nerdbank.JsonRpc.JsonRpcValue.FromMessagePack*> and <xref:Nerdbank.JsonRpc.JsonRpcValue.AsMessagePack*> explicitly; message types expose only <xref:Nerdbank.JsonRpc.JsonRpcValue> for application data.

A default <xref:Nerdbank.JsonRpc.JsonRpcValue> means omitted `params` (or absent error data); an encoded JSON `null` or MessagePack nil is *present*. A result must have a value. Values tagged for the wrong codec are rejected rather than transcoded. Typed calls and generated proxies use the selected serializer and <xref:PolyType.ITypeShape> instances automatically; see [Generated client proxies](client-proxies.md).

## Deployment dependencies

The NuGet package references both serializer packages. On a MessagePack-only deployment, `Nerdbank.Json.dll` can be excluded without loading it. Currently Nerdbank.Json itself loads Nerdbank.MessagePack, so excluding `Nerdbank.MessagePack.dll` from a JSON deployment is not supported.
