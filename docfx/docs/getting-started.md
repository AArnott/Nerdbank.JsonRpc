# Getting Started

## Installation

Consume this Nerdbank.JsonRpc via its NuGet Package.
Click on the badge to find its latest version and the instructions for consuming it that best apply to your project.

[![NuGet package](https://img.shields.io/nuget/v/Nerdbank.JsonRpc.svg)](https://nuget.org/packages/Nerdbank.JsonRpc)

## Usage

### Server setup

Annotate your contract for PolyType method-shape generation and register an implementation with `JsonRpc`:

```csharp
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ICalculator
{
	ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
}

public sealed class Calculator : ICalculator
{
	public ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken) => new(a + b);
}
```

### Generated client proxy prototype

The prototype source generator emits a proxy when the contract is also annotated with `[GenerateJsonRpcProxy]`.

```csharp
[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ICalculator
{
	ValueTask<int> AddAsync(int a, int b, CancellationToken cancellationToken);
}
```

The generated proxy currently takes an explicit PolyType shape provider instance:

```csharp
using ShapeProvider = PolyType.SourceGenerator.TypeShapeProvider_MyAssembly;

JsonRpc rpc = new(channel);
rpc.Start();

var client = new CalculatorProxy(rpc, ShapeProvider.Default);
int sum = await client.AddAsync(1, 2, CancellationToken.None);
```

This is the key workaround for source-generator non-chaining: the proxy uses an explicitly supplied provider instead of relying on runtime reflection or on PolyType discovering shapes for generator-emitted DTOs.

The current prototype supports `ValueTask<T>`, `Task<T>`, `ValueTask`, `Task`, and `void` notification methods.

Named argument packing is the default. To request positional packing for a specific method, annotate it explicitly:

```csharp
[JsonRpcArgumentMatch(JsonRpcArgumentMatch.Positional)]
ValueTask<int> SubtractAsync(int a, int b, CancellationToken cancellationToken);
```
