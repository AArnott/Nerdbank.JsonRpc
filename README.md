# Nerdbank.JsonRpc

[![NuGet package](https://img.shields.io/nuget/v/Nerdbank.JsonRpc.svg)](https://www.nuget.org/packages?q=Nerdbank.JsonRpc)
[![Build](https://github.com/AArnott/Nerdbank.JsonRpc/actions/workflows/build.yml/badge.svg)](https://github.com/AArnott/Nerdbank.JsonRpc/actions/workflows/build.yml)

Nerdbank.JsonRpc is a NativeAOT-safe .NET library for building strongly typed JSON-RPC clients and servers over MessagePack or UTF-8 JSON. It supports requests and notifications, generated client proxies, batching, cancellation, and configurable JSON framing.

## Install

```shell
dotnet add package Nerdbank.JsonRpc
```

## Generated client proxy quick start

The `Nerdbank.JsonRpc` NuGet package includes its source generator as a C# analyzer. After installing the package, define a shared contract and annotate it for PolyType method-shape and proxy generation:

```csharp
using Nerdbank.JsonRpc;
using PolyType;

[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ICalculator
{
    ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
}
```

After creating and starting a `JsonRpc` connection, attach the generated proxy and call it like an ordinary interface:

```csharp
JsonRpc rpc = new(channel);
rpc.Start();

ICalculator calculator = rpc.Attach<ICalculator>();
int sum = await calculator.AddAsync(1, 2, CancellationToken.None);
```

MessagePack is the default encoding. By default each MessagePack message is preceded by a 4-byte big-endian length header (compatible with StreamJsonRpc's `LengthHeaderMessageHandler`); pass `JsonRpcMessagePackFraming.SelfDelimiting` to send bare MessagePack structures instead. Configure a `JsonRpcJsonChannel` when your protocol uses UTF-8 JSON, with either newline-delimited or `Content-Length` framing.

## Features

- **Strongly typed RPC:** Register server targets and issue typed requests or notifications.
- **Generated client proxies:** Use interface-backed clients with positional or named arguments. [Learn about client proxies](https://aarnott.github.io/Nerdbank.JsonRpc/docs/client-proxies.html).
- **MessagePack or JSON:** Select MessagePack or UTF-8 JSON; choose length-header or self-delimiting framing for MessagePack, and newline-delimited or `Content-Length` framing for JSON. [Compare encodings and framing](https://aarnott.github.io/Nerdbank.JsonRpc/docs/encodings.html).
- **OpenRPC discovery:** Describe registered methods, parameters, results, and errors with serializer-generated JSON Schemas, locally or remotely through `rpc.discover`, using either encoding. [Discover an API](https://aarnott.github.io/Nerdbank.JsonRpc/docs/openrpc.html).
- **Method name transforms:** Map CLR method names to camelCase wire names by default, specify explicit names, and interoperate with StreamJsonRpc. [Configure method naming](https://aarnott.github.io/Nerdbank.JsonRpc/docs/method-naming.html).
- **Events as notifications:** Raise events on registered targets to send JSON-RPC notifications to the remote party. [Send events as notifications](https://aarnott.github.io/Nerdbank.JsonRpc/docs/events.html).
- **Batching:** Send independent requests and notifications in one JSON-RPC payload. [Send a batch](https://aarnott.github.io/Nerdbank.JsonRpc/docs/batching.html).
- **By-reference marshaling:** Marshal interfaces, `IObserver<T>`, `IProgress<T>`, streams, pipes, and `IAsyncEnumerable<T>` by reference instead of by value. [Marshal objects by reference](https://aarnott.github.io/Nerdbank.JsonRpc/docs/rpc-marshalable-interfaces.html).
- **Cancellation and failure handling:** Propagate cancellation and distinguish malformed protocol envelopes from application-value conversion failures. [Understand protocol behavior](https://aarnott.github.io/Nerdbank.JsonRpc/docs/protocol-behavior.html).
- **Ordered dispatch:** Inbound requests start executing in the order they were sent, while still running concurrently on the thread pool. Configure this behavior with `JsonRpc.SynchronizationContext`; [learn about dispatch order and concurrency](https://aarnott.github.io/Nerdbank.JsonRpc/docs/protocol-behavior.html#dispatch-order-and-concurrency).
- **Main-thread deadlock mitigation:** Propagate `JoinableTaskFactory` context across processes, compatible with StreamJsonRpc. [Learn more](https://aarnott.github.io/Nerdbank.JsonRpc/docs/protocol-behavior.html#deadlock-mitigation-with-joinabletaskfactory).
- **Security guidance:** Learn how to authenticate connections and protect resources; the application is responsible for authenticating peers before attaching JSON-RPC. [Review security considerations](https://aarnott.github.io/Nerdbank.JsonRpc/docs/security.html).

## Documentation

- [Getting started](https://aarnott.github.io/Nerdbank.JsonRpc/docs/getting-started.html)
- [Feature overview](https://aarnott.github.io/Nerdbank.JsonRpc/docs/features.html)
- [Encodings and framing](https://aarnott.github.io/Nerdbank.JsonRpc/docs/encodings.html)
- [API reference](https://aarnott.github.io/Nerdbank.JsonRpc/api/Nerdbank.JsonRpc.html)

## Contributing

Contributions and bug reports are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) for local setup, build, test, and documentation instructions. Please report issues at [GitHub Issues](https://github.com/AArnott/Nerdbank.JsonRpc/issues).

## License

Licensed under the [MIT License](LICENSE).
