# Encodings and framing

Nerdbank.JsonRpc supports MessagePack (the default) and UTF-8 JSON. Configure the serializer for the chosen encoding before starting a connection. Both peers must agree on the encoding and framing; the transport does not sniff input or switch codecs mid-connection.

## Select a serializer

For JSON, configure a <xref:Nerdbank.Json.JsonSerializer>, pass it to a <xref:Nerdbank.JsonRpc.JsonRpcJsonChannel>, and assign **the same instance** to <xref:Nerdbank.JsonRpc.JsonRpc.Serializer> before calling <xref:Nerdbank.JsonRpc.JsonRpc.Start*>:

[!code-csharp[](../../samples/cs/encodings.cs#json-encoding)]

To customize MessagePack instead, assign a configured <xref:Nerdbank.MessagePack.MessagePackSerializer> to <xref:Nerdbank.JsonRpc.JsonRpc.Serializer> before starting. Both concrete serializers are wrapped automatically by <xref:Nerdbank.JsonRpc.JsonSerializerPlugin> or <xref:Nerdbank.JsonRpc.MessagePackSerializerPlugin>; the wrappers retain the exact configured instances. Use a <xref:Nerdbank.JsonRpc.StreamingJsonRpcMessageChannel> for pipe-based MessagePack transport.

## JSON framing

<xref:Nerdbank.JsonRpc.JsonRpcJsonFraming.NewlineDelimited> sends one compact JSON object or batch per line. <xref:Nerdbank.JsonRpc.JsonRpcJsonFraming.ContentLength> sends `Content-Length: <UTF-8 byte count>\r\n\r\n` followed by exactly that many bytes. Both peers must use the same framing mode. Empty or oversized frames (more than 8 MiB), malformed headers, and incomplete frames fault the connection. See [Protocol behavior](protocol-behavior.md) for the distinction between protocol and application-value failures.

## Already serialized values

Application payloads (<xref:Nerdbank.JsonRpc.JsonRpcRequest.Arguments>, <xref:Nerdbank.JsonRpc.JsonRpcResult.Result>, and optional <xref:Nerdbank.JsonRpc.JsonRpcErrorDetails.Data>) use <xref:Nerdbank.JsonRpc.JsonRpcValue>, an owned, encoding-tagged, **already serialized** value. Use <xref:Nerdbank.JsonRpc.JsonRpcValue.FromJson*> or <xref:Nerdbank.JsonRpc.JsonRpcValue.FromMessagePack*> to copy supplied bytes. These factories do **not** validate the bytes: supply exactly one complete encoded value. Invalid JSON fails when written to a JSON frame; invalid MessagePack may yield a malformed frame. <xref:Nerdbank.JsonRpc.JsonRpcValue.AsMessagePack*> rejects JSON, and <xref:Nerdbank.JsonRpc.JsonRpcValue.Bytes> returns a copy. Code using <xref:Nerdbank.MessagePack.RawMessagePack> can use the MessagePack bridge explicitly.

A default <xref:Nerdbank.JsonRpc.JsonRpcValue> means omitted `params` (or absent error data); an encoded JSON `null` or MessagePack nil is *present*. A result must have a value. Values tagged for the wrong codec are rejected rather than transcoded. Typed calls and generated proxies use the selected serializer and <xref:PolyType.ITypeShape> instances automatically; see [Generated client proxies](client-proxies.md).

## Deployment dependencies

The NuGet package references both serializer packages. On a MessagePack-only deployment, `Nerdbank.Json.dll` can be excluded without loading it. Currently Nerdbank.Json itself loads Nerdbank.MessagePack, so excluding `Nerdbank.MessagePack.dll` from a JSON deployment is not supported.
