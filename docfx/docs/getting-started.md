# Getting Started

## Installation

Consume this Nerdbank.JsonRpc via its NuGet Package.
Click on the badge to find its latest version and the instructions for consuming it that best apply to your project.

[![NuGet package](https://img.shields.io/nuget/v/Nerdbank.JsonRpc.svg)](https://nuget.org/packages/Nerdbank.JsonRpc)

## Usage

### Server setup

Annotate your contract for PolyType method-shape generation and register an implementation with `JsonRpc`:

[!code-csharp[](../../samples/cs/GettingStarted.cs#server-setup)]

### Generated client proxy prototype

The prototype source generator emits a proxy when the contract is also annotated with `[GenerateJsonRpcProxy]`.

[!code-csharp[](../../samples/cs/GettingStarted.cs#generated-client-proxy)]

Attach the generated proxy to a running `JsonRpc` instance:

[!code-csharp[](../../samples/cs/GettingStarted.cs#attach-proxy)]

The proxy resolves the provider once in its constructor and caches the type shapes it needs for method arguments and results.

`Attach<T>` also accepts an optional immutable options record for future proxy settings:

[!code-csharp[](../../samples/cs/GettingStarted.cs#proxy-options)]

The current prototype supports `ValueTask<T>`, `Task<T>`, `ValueTask`, `Task`, and `void` notification methods.

Positional argument packing is the default. To request named packing for an entire contract, set the attribute property explicitly:

[!code-csharp[](../../samples/cs/GettingStarted.cs#named-arguments)]

### Sending a batch

Use `CreateBatch()` when several calls should be sent as one JSON-RPC payload. Request tasks are created immediately and complete independently after `SendAsync` queues the batch and matching responses arrive.

[!code-csharp[](../../samples/cs/GettingStarted.cs#sending-batch)]

A batch is one-shot: after `SendAsync`, additional calls and duplicate sends fail. Calls are independent rather than transactional, and responses are matched by request ID rather than array position.
