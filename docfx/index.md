# Overview

Nerdbank.JsonRpc is a NativeAOT-safe .NET library for building strongly typed JSON-RPC clients and servers over MessagePack or UTF-8 JSON.

## Install

```shell
dotnet add package Nerdbank.JsonRpc
```

[![NuGet package](https://img.shields.io/nuget/v/Nerdbank.JsonRpc.svg)](https://www.nuget.org/packages?q=Nerdbank.JsonRpc)

## Highlights

- **Typed RPC:** Register server targets using PolyType method shapes and call methods through typed APIs. [Get started](docs/getting-started.md).
- **Encodings and framing:** Configure a MessagePack or JSON serializer. JSON supports newline-delimited and `Content-Length` framing. [Choose an encoding](docs/encodings.md).
- **Generated client proxies:** Attach an interface-backed proxy instead of constructing requests manually, with positional or named arguments. [Use client proxies](docs/client-proxies.md).
- **OpenRPC discovery:** Describe registered methods, parameters, results, and errors using serializer-generated JSON Schemas, locally or remotely through `rpc.discover`. [Discover an API](docs/openrpc.md).
- **Batching:** Send several independent requests or notifications in one JSON-RPC payload. [Send a batch](docs/batching.md).
- **By-reference marshaling:** Marshal interfaces, `IObserver<T>`, `IProgress<T>`, streams and pipes, and `IAsyncEnumerable<T>` by reference instead of by value. [Marshal objects by reference](docs/rpc-marshalable-interfaces.md).

Read the full [feature overview](docs/features.md), including method naming, events, [protocol behavior](docs/protocol-behavior.md), and security.

## Getting started

JSON-RPC is a peer-to-peer protocol. Either party may expose methods, invoke methods exposed by the other party, or do both over the same connection.

[Get started](docs/getting-started.md) by defining a shared contract, opening a connection, and calling it through a generated proxy.
