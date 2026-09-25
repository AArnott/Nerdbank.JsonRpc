# Nerdbank.JsonRpc

[![NuGet package](https://img.shields.io/nuget/v/Nerdbank.JsonRpc.svg)](https://www.nuget.org/packages?q=Nerdbank.JsonRpc)
[![Build](https://github.com/AArnott/Nerdbank.JsonRpc/actions/workflows/build.yml/badge.svg)](https://github.com/AArnott/Nerdbank.JsonRpc/actions/workflows/build.yml)

Nerdbank.JsonRpc is a .NET library for building strongly typed JSON-RPC clients and servers over MessagePack or UTF-8 JSON. It supports typed requests and notifications, generated client proxies, batching, cancellation, and configurable JSON framing.

## Install

```shell
dotnet add package Nerdbank.JsonRpc
```

## Generated client proxy quick start

Generated proxies require the source generator, which is currently built from this repository rather than included in the NuGet package. Add it to your project as an analyzer:

```xml
<ItemGroup>
  <ProjectReference Include="path-to-Nerdbank.JsonRpc/src/Nerdbank.JsonRpc.SourceGeneration/Nerdbank.JsonRpc.SourceGeneration.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
</ItemGroup>
```

Then define a shared contract and annotate it for PolyType method-shape and proxy generation:

```csharp
using Nerdbank.JsonRpc;
using PolyType;

[GenerateJsonRpcProxy]
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

MessagePack is the default encoding. Configure a `JsonRpcJsonChannel` when your protocol uses UTF-8 JSON, with either newline-delimited or `Content-Length` framing.

## Features

- **Strongly typed RPC:** Register server targets and issue typed requests or notifications.
- **Generated client proxies:** Use interface-backed clients with positional or named arguments.
- **MessagePack or JSON:** Select MessagePack or UTF-8 JSON; choose newline-delimited or `Content-Length` framing for JSON.
- **Batching:** Send independent requests and notifications in one JSON-RPC payload.
- **Robust protocol behavior:** Propagate cancellation and distinguish malformed protocol messages from application value failures.
- **Deadlock mitigation:** Propagate `JoinableTaskFactory` context across processes, compatible with StreamJsonRpc.

## Documentation

- [Getting started](https://aarnott.github.io/Nerdbank.JsonRpc/docs/getting-started.html)
- [Feature overview](https://aarnott.github.io/Nerdbank.JsonRpc/docs/features.html)
- [Encodings and framing](https://aarnott.github.io/Nerdbank.JsonRpc/docs/encodings.html)
- [API reference](https://aarnott.github.io/Nerdbank.JsonRpc/api/Nerdbank.JsonRpc.html)

## Contributing

Contributions and bug reports are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) for local setup, build, test, and documentation instructions. Please report issues at [GitHub Issues](https://github.com/AArnott/Nerdbank.JsonRpc/issues).

## License

Licensed under the [MIT License](LICENSE).
