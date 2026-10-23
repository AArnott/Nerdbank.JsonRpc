# Nerdbank.JsonRpc

Nerdbank.JsonRpc is a NativeAOT-safe .NET library for building strongly typed JSON-RPC clients and servers over MessagePack or UTF-8 JSON.

## Features

- **Strongly typed RPC:** Register local targets and issue typed requests or notifications.
- **Generated client proxies:** A built-in source generator creates interface-backed proxies with positional or named arguments.
- **MessagePack or JSON:** MessagePack (default, length-header or self-delimiting framing) or UTF-8 JSON (newline-delimited or `Content-Length` framing).
- **Peer-to-peer:** One connection can both expose local methods and invoke remote ones.
- **Batching:** Send independent requests and notifications in one JSON-RPC payload.
- **Cancellation and robust error handling:** Propagates cancellation and distinguishes malformed protocol messages from application failures.
- **Ordered dispatch:** Inbound requests start in the order sent, while running concurrently.
- **Deadlock mitigation:** Propagates `JoinableTaskFactory` context across processes, compatible with StreamJsonRpc.
- **Exotic types:** Marshal interfaces, observers, progress, streams, and async sequences by reference.

## Quick start

Define a contract shared by both parties:

```csharp
using Nerdbank.JsonRpc;
using PolyType;

[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ICalculator
{
    ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
}

public class Calculator : ICalculator
{
    public ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken) => new(a + b);
}
```

Connect over any bidirectional stream, expose a local target, and call the remote party through a proxy:

```csharp
using Nerdbank.Streams;

using JsonRpc rpc = new(new JsonRpcMessagePackChannel(stream.UsePipe()));

// Expose a local target the remote party can call.
rpc.AddRpcTarget<ICalculator>(new Calculator());
rpc.Start();

// Invoke a method on the remote party.
ICalculator remote = rpc.Attach<ICalculator>();
int sum = await remote.AddAsync(1, 2, CancellationToken.None);
```

## Learn more

- [Getting started](https://aarnott.github.io/Nerdbank.JsonRpc/docs/getting-started.html)
- [Features](https://aarnott.github.io/Nerdbank.JsonRpc/docs/features.html)
- [Encodings and framing](https://aarnott.github.io/Nerdbank.JsonRpc/docs/encodings.html)
- [API reference](https://aarnott.github.io/Nerdbank.JsonRpc/api/Nerdbank.JsonRpc.html)
- [Source and issues](https://github.com/AArnott/Nerdbank.JsonRpc)
