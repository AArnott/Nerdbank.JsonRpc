# Security considerations

Nerdbank.JsonRpc lets a remote party invoke code in your process. Treat every connection as a security boundary, and decide deliberately who may connect and what they may do once connected.

## Authentication

**Nerdbank.JsonRpc does not authenticate the remote party.** The JSON-RPC protocol has no notion of identity, so authentication is outside its scope. Establish who is on the other end of the connection *before* you attach <xref:Nerdbank.JsonRpc.JsonRpc> to it. Once attached, every message is processed as if it came from whoever you authenticated.

Common ways to authenticate a connection first:

- **Local pipes:** create the pipe with <xref:System.IO.Pipes.PipeOptions.CurrentUserOnly?displayProperty=nameWithType> or a restrictive <xref:System.IO.Pipes.PipeSecurity>, so only intended accounts can connect. On Windows, the server can also check the client's identity, for example with <xref:System.IO.Pipes.NamedPipeServerStream.GetImpersonationUserName?displayProperty=nameWithType>.
- **Network connections:** wrap the stream in <xref:System.Net.Security.SslStream> with mutual TLS (client certificates), or use an authenticated stream such as <xref:System.Net.Security.NegotiateStream>.
- **Application handshake:** exchange and verify a token or credential over the raw stream, then create the channel over the same stream if verification succeeds.
- **Process ancestry:** a parent process that launches its own child can pass it a secret channel name or an inherited handle, so only that child can find the connection.

**Authorization** is likewise up to you. If different callers deserve different access, decide that when the connection is authenticated, and register an RPC target that exposes only what that caller may use. Methods can then trust that every request on that connection comes from that caller.

## Confidentiality and integrity

Nerdbank.JsonRpc does not encrypt or sign messages. It relies on the transport for that. Across machines, use TLS or a similarly protected transport. Within a machine, use transports whose access is limited to the intended accounts, such as a named pipe with a restrictive ACL.

## What a peer can invoke

A connected peer can call the methods exposed by targets registered with <xref:Nerdbank.JsonRpc.JsonRpc.AddRpcTarget*?displayProperty=nameWithType>. The callable surface also includes built-in protocol methods such as `$/cancelRequest` and methods on objects you marshal by reference. Register a narrow interface containing only the application methods you intend to expose; if you register a class contract, its shaped public methods may be exposed as well. Account for the built-in and marshaled-object surfaces when reviewing the connection's attack surface.

Treat every argument as untrusted input, just as you would for a web API: validate sizes, ranges, paths and identifiers before you act on them.

Objects passed by reference are callable by the peer according to their protocol-defined lifetimes. These include [RPC-marshalable interfaces](rpc-marshalable-interfaces.md), <xref:System.IProgress`1>, <xref:System.IObserver`1>, <xref:System.Collections.Generic.IAsyncEnumerable`1> and <xref:System.IO.Stream> instances. Marshal only objects that are safe for the peer to use. <xref:Nerdbank.JsonRpc.JsonRpc.RevokeMarshaledObject(System.Object)> revokes active handles for RPC-marshalable objects and observers; it does not revoke progress values, asynchronous enumerables or streams, which have their own lifetimes. Handles are local to their connection, so a peer cannot use a handle to reach objects that were marshaled over other connections.

## Resource limits

Nerdbank.JsonRpc bounds the cost of malformed or hostile input:

- Received messages larger than <xref:Nerdbank.JsonRpc.JsonRpc.MaximumMessageSize?displayProperty=nameWithType> (8 MiB by default) are rejected. Lower this when messages are known to be small and the peer is not fully trusted.
- MessagePack batches and parameter collections are limited to 65,536 entries.
- The channel holds only a bounded number of received messages that are waiting to be processed (100 by default).
- Malformed framing or envelopes fault the connection rather than being partially processed. See [Protocol behavior](protocol-behavior.md#failures).

These limits do not stop a peer from making many valid but expensive requests. Throttle or reject expensive operations in your own methods where that matters.

## Error details

By default, a thrown exception's bounded diagnostic details are included in the JSON-RPC error response. This may disclose messages and stack traces to the peer, so set <xref:Nerdbank.JsonRpc.JsonRpcOptions.IncludeExceptionDetails?displayProperty=nameWithType> to `false` when a connection is not trusted to receive them. Full exceptions are logged locally regardless of this setting. The diagnostic type name is never used to activate an arbitrary type; see [Error handling](error-handling.md) for the wire schema, safe reconstruction rules, and client examples.
