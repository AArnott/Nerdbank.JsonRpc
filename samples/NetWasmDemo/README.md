# NetWasm demo: Nerdbank.JsonRpc in the browser

A proof of concept that runs Nerdbank.JsonRpc, compiled to WebAssembly by [NetWasm](https://github.com/zion-sati/NetWasm)
(`netwasm0.1`), in a browser page. The page talks JSON-RPC to an ASP.NET Core server over a WebSocket.

* `Contract/DemoContract.cs` holds the shared RPC contract. `IDemoServer` has `AddAsync` and `GreetAsync`, and
  `IDemoClient.Tick` is a server-to-client notification. Both projects compile it.
* `Server/` is ASP.NET Core (net10.0), referencing `src/Nerdbank.JsonRpc`. It serves the published client and
  a `/rpc` WebSocket endpoint: WebSocket → `IDuplexPipe` (Nerdbank.Streams `UsePipe`) → `JsonRpcJsonChannel`
  (newline-delimited JSON) → `JsonRpc` with an `IDemoServer` target. After the client connects, it sends a `tick`
  notification every 2 seconds through a generated `IDemoClient` proxy.
* `Client/` is a NetWasm (`netwasm0.1`) executable that consumes the `Nerdbank.JsonRpc` **-netwasm package** from
  the local feed.
  * `index.html` implements the `[JSImport]` module `app.host` (WebSocket + DOM).
  * `WebSocketPipe` bridges WebSocket messages to System.IO.Pipelines.
  * The client calls the server through the source-generated `DemoServerProxy`.
  * It receives `tick` through `AddRpcTarget` with an explicit PolyType shape.

## Prerequisites

* .NET SDK 10.0.4xx. `global.json` pins `NetWasm.Sdk` 0.5.0.
* The `-netwasm` packages in the local feed (see `nuget.config`). They come from the `netwasm-poc` branches of
  vs-validation, PolyType, Nerdbank.Json, Nerdbank.MessagePack and this repo.

## Build and run

```sh
./build-client.sh   # Release (Oz): ~4.5 minutes, ~31 MB module
./run-server.sh     # http://localhost:5080
```

Then open http://localhost:5080/.

## NetWasm-specific notes

* The client uses NetWasm's raw `async-command` browser deployment (`NetWasmRawWasm=true`,
  `NetWasmComponentContract=async-command`). That is the only configuration in NetWasm.Sdk 0.5.0 where async code
  works.
* The stock browser bootstrap can't pass `[JSImport]` modules, so `host/custom-raw-bootstrap.template.mjs` is a
  copy of it that merges `globalThis.netwasmConsumerModules`.
* `JsonRpc.Attach<T>()` needs reflection, so on netwasm the generated proxy is constructed directly with the
  witness's `GeneratedTypeShapeProvider`.
* `Exception.StackTrace` is `null` and `GetType().Name` throws on NetWasm, so diagnostics are message-only.
