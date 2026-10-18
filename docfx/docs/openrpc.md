# OpenRPC discovery

A peer can publish the methods it exposes as an [OpenRPC](https://spec.open-rpc.org/) document. OpenRPC provides the JSON-RPC API-description format: it records method names, parameters, results, and errors, and uses JSON Schema for the data shapes.

Call <xref:Nerdbank.JsonRpc.JsonRpc.GetOpenRpcDocument> to obtain a local snapshot, or invoke the reserved <c>rpc.discover</c> method on the connection to retrieve the same document remotely. The document includes the exact method names and parameter requirements used by dispatch, and schemas generated from the connection's configured serializer. The returned discovery result uses the connection's active wire encoding, including MessagePack connections.

## JSON Schema dialect

Nerdbank.JsonRpc emits JSON Schema Draft 7 for Schema Objects, as required by OpenRPC 1.4.1.

Schemas come from the configured serializer and therefore reflect its configuration and converters. JSON and MessagePack peers may advertise different schemas for the same CLR type when their effective serializer representations differ. Custom converters that do not provide schema support may produce permissive or incomplete schemas, as documented by the corresponding serializer.

## Scope and error reporting

The document describes locally registered methods; it does not enumerate attached client proxies, methods exposed dynamically by marshaled objects, or the transport framing. Event notifications are not callable methods and are not listed. The built-in <c>rpc.discover</c> method is included.

Each method lists invalid-parameter, internal-error, and (when applicable) cancellation errors. Application exceptions are not statically enumerable. If <xref:Nerdbank.JsonRpc.JsonRpcOptions.AdditionalExceptionTypes> allows typed exception payloads, their serializer-generated schemas are included in the <c>x-exceptionPayloads</c> extension on each method object. This placement keeps the extension valid under the OpenRPC meta-schema, which does not allow extension properties on Error Objects. Ordinary diagnostic exception details remain peer-controlled runtime data; see [Error handling](error-handling.md) for their shape and security considerations.
