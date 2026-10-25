# Remote brokers and IPC

Client acquisition uses the same descriptor whether a service is local or remote.
A remote broker control connection allocates a separate transport for each service activation.
The control and service descriptors must be agreed by both peers.

## Descriptor configuration

The non-generic <xref:Nerdbank.JsonRpc.BrokeredServices.IJsonRpcDescriptor> contains connection and server registration operations; its generic derivative adds typed client construction.
The concrete encoding descriptors derive from <xref:Nerdbank.JsonRpc.BrokeredServices.JsonRpcDescriptor`1> and are records, so use `with` to configure a copy.
Each exposes a `Framing` property typed for its encoding; MessagePack defaults to a four-byte length prefix, and JSON defaults to Content-Length headers.
Each also exposes its encoding-specific `Serializer` as an init-only property, so descriptor owners can customize serializer settings using nested `with` expressions.
Both peers must agree on encoding, framing, method naming, and multiplexing settings.
<xref:Nerdbank.JsonRpc.BrokeredServices.JsonRpcUtf8Descriptor`1> uses the Nerdbank.Json serializer and supports UTF-8 JSON only, not UTF-16.

## Host a broker over IPC

<xref:Nerdbank.JsonRpc.BrokeredServices.IpcRelayServiceBroker> offers single-client service endpoints over same-account IPC.
<xref:Nerdbank.JsonRpc.BrokeredServices.FrameworkServices.RemoteServiceBroker?displayProperty=nameWithType> describes the generated control contract.
Register that control contract on an agreed transport and adapt it with <xref:Nerdbank.JsonRpc.BrokeredServices.RemoteServiceBroker>.
Supply a `Local`-filtered broker view from the host's container; see [audience filtering](hosting.md#audience-filtering).

[!code-csharp[](../../../samples/cs/brokered-services.cs#brokered-ipc-server)]

The host supplies the endpoint name to clients using an application-specific discovery mechanism.
The client connects to that endpoint, adapts the broker, and acquires a typed proxy:

[!code-csharp[](../../../samples/cs/brokered-services.cs#brokered-ipc-client)]

The control protocol carries activation arguments, requested culture names, and stable capability identifiers, not local targets or setup delegates.
Channel allocation pairs capabilities with the exact offering that supplied the pipe.
When the control connection and service multiplexer use separate transports, the client waits for the matching channel offer even if its control response arrives first.
Canceled, abandoned, and unclaimed requests release their activation; relay connection timeouts bound how long an endpoint can remain unclaimed.
Callback-enabled activation is deferred until the client can receive callbacks; later activation failures close the service connection and are logged.
For events that may arrive immediately after a remote connection starts, subscribe using <xref:Nerdbank.JsonRpc.BrokeredServices.ServiceActivationOptions.ConfigureProxy?displayProperty=nameWithType>.
The setup callback runs before incoming client dispatch starts.
Client callback contracts are captured through <xref:Nerdbank.JsonRpc.BrokeredServices.JsonRpcDescriptor.CallbackInterface?displayProperty=nameWithType>; a factory can await callbacks during activation.

## Multiplexed broker transports

<xref:Nerdbank.JsonRpc.BrokeredServices.MultiplexingRelayServiceBroker> offers service channels on a caller-owned <xref:Nerdbank.Streams.MultiplexingStream>.
Pass the matching transport multiplexer to `RemoteServiceBroker.ConnectAsync`.
This outer multiplexer only carries opaque service connections; it is never used for service resource marshalling.

## Dedicated service multiplexing

Multiplexing within a service connection is disabled by default.
Set <xref:Nerdbank.JsonRpc.BrokeredServices.JsonRpcDescriptor.MultiplexingStreamOptions?displayProperty=nameWithType> on both endpoint descriptors to enable a dedicated, connection-owned multiplexer inside the transport, over either IPC or an outer multiplexed channel.

[!code-csharp[](../../../samples/cs/brokered-service-hosting.cs#brokered-multiplexing)]

We recommend setting `ProtocolMajorVersion = 3` for handshake-free setup; version 3 or later is required.
The descriptor captures a frozen copy of the options and reserves seeded channel 0 for RPC.
If no seeded channels are configured, it creates one for RPC; otherwise it preserves all configured channel indices, and applications may use channels 1 and above.
Other channels carry out-of-band streams and pipes.

Relays can blindly forward the transport across multiple broker hops: only the service and its client interpret this inner multiplexer.
For `A -> B -> C`, B repeats the opaque service stream while A and C remain the multiplexer endpoints.
Leave the options `null` on both endpoint descriptors to use plain RPC framing without out-of-band stream and pipe support.
Disposing the RPC connection also disposes its dedicated multiplexer, but never a caller-owned outer transport multiplexer.

## Same-account IPC helpers

General IPC helpers are in the root `Nerdbank.JsonRpc` namespace and can also be used without brokered services.
Start a listener with <xref:Nerdbank.JsonRpc.IpcServer.Create*?displayProperty=nameWithType> and configure it with <xref:Nerdbank.JsonRpc.IpcServerOptions>.
The returned <xref:Nerdbank.JsonRpc.IIpcServer> exposes the endpoint for <xref:Nerdbank.JsonRpc.IpcClient.ConnectAsync*?displayProperty=nameWithType>.
The listener exists before its endpoint is returned.
The connection callback owns the accepted stream until it completes, and the listener observes callback failures.
Disposal cancels acceptance and active callbacks; asynchronous disposal waits for them to finish.

Windows listeners grant access to the current account and explicitly deny network logons, including network logons using the same credentials.
Before accepting connections, each Windows pipe instance verifies its actual owner and access rules; a pre-existing endpoint with different security is rejected rather than reused.
Clients reject a server owned by a different account.
Unix uses the runtime's current-user-only Unix-domain socket implementation and validates endpoint length.
This is not a security boundary between mutually hostile processes using the same account, and applications must not treat activation arguments as authentication.
