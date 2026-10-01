# Getting started

JSON-RPC is a peer-to-peer protocol. Either party may expose methods, invoke methods exposed by the other party, or do both over the same connection.

## Install the package

Consume Nerdbank.JsonRpc via its NuGet package. The badge links to the latest version and installation instructions.

[![NuGet package](https://img.shields.io/nuget/v/Nerdbank.JsonRpc.svg)](https://www.nuget.org/packages?q=Nerdbank.JsonRpc)

## Define an RPC contract

Define an interface shared by the local target and the strongly typed proxy. <xref:PolyType.GenerateShapeAttribute> generates the method metadata used to dispatch incoming calls, while <xref:Nerdbank.JsonRpc.GenerateJsonRpcProxyAttribute> generates the proxy used to make outgoing calls.

[!code-csharp[](../../samples/cs/getting-started.cs#rpc-contract)]

## Create a JSON-RPC connection

Nerdbank.JsonRpc exchanges messages through an <xref:System.IO.Pipelines.IDuplexPipe>. Most applications begin with a bidirectional <xref:System.IO.Stream>, which may come from a TCP connection, named pipe, Unix-domain socket, child process standard input/output, or another application-specific transport. Use <xref:Nerdbank.Streams.PipeExtensions.UsePipe*> to adapt the stream, then create a channel and the <xref:Nerdbank.JsonRpc.JsonRpc> connection:

[!code-csharp[](../../samples/cs/getting-started.cs#create-connection)]

The channel defines the wire encoding and framing. This example uses MessagePack with its default length-header framing. Both parties must use compatible encoding and framing; see [Encodings and framing](encodings.md) for JSON and other choices.

The application is responsible for establishing the connected stream and managing its lifetime. For in-process tests, <xref:Nerdbank.Streams.FullDuplexStream.CreatePipePair*> creates two connected <xref:System.IO.Pipelines.IDuplexPipe> instances that can be given directly to two channels.

For same-machine IPC, consider <xref:Nerdbank.Streams.SharedMemoryDuplexPipe> from Nerdbank.Streams. This example connects both endpoints in one process for simplicity; separate processes can use the same shared name:

[!code-csharp[](../../samples/cs/getting-started.cs#shared-memory-connection)]

Benchmark against named pipes for your workload, and [authenticate peers](security.md) before exposing RPC methods.

## Expose local methods to the remote party

Implement the contract as an ordinary .NET type:

[!code-csharp[](../../samples/cs/getting-started.cs#rpc-target)]

Register the target before starting the connection. <xref:Nerdbank.JsonRpc.JsonRpc.Start*> begins reading and dispatching messages; awaiting <xref:Nerdbank.JsonRpc.JsonRpc.Completion> keeps this endpoint active until the connection closes or faults.

[!code-csharp[](../../samples/cs/getting-started.cs#expose-target)]

A connection may register any number of targets. Registering a target does not make this endpoint exclusively a "server"; the same connection can also invoke methods on the remote party.

## Call methods exposed by the remote party

Start the connection, attach the generated proxy, and invoke it like an ordinary interface. The proxy sends the request to the remote party that registered the matching target.

[!code-csharp[](../../samples/cs/getting-started.cs#attach-proxy)]

The same <xref:Nerdbank.JsonRpc.JsonRpc> instance may both register local targets and attach remote proxies, enabling calls in either direction.

## Connect to another JSON-RPC implementation

The remote party does not need to use Nerdbank.JsonRpc. The two parties must agree on the wire protocol details:

- Select compatible serialization and message boundaries as described in [Encodings and framing](encodings.md).
- Match the remote method names using [method name transforms](method-naming.md) or explicit PolyType method names.
- Match its positional or named parameter convention using [generated client proxy options](client-proxies.md).
- When applicable, use the documented [RPC-marshalable and exotic-type protocols](rpc-marshalable-interfaces.md), which are compatible with StreamJsonRpc.

## Where to go from here

- Learn which interface members and method signatures can be used by [generated client proxies](client-proxies.md#supported-contracts).
- Understand request cancellation and `CancellationToken` propagation in [Protocol behavior](protocol-behavior.md#cancellation).
- Pass objects by reference, observers, progress callbacks, streams, and asynchronous sequences using [RPC-marshalable interfaces and exotic types](rpc-marshalable-interfaces.md).
- Send several requests in one payload with [Batching](batching.md).
- Propagate .NET events as JSON-RPC notifications with [Events as notifications](events.md).
