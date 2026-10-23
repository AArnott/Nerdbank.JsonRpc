# Comparing StreamJsonRpc and Nerdbank.JsonRpc

This report compares the public capabilities described in the StreamJsonRpc documentation with Nerdbank.JsonRpc as of October 23, 2026.
It focuses on user-visible features, not implementation quality or performance, and treats differences in API design as gaps only where they limit a concrete use case.

## Summary

Nerdbank.JsonRpc covers the common JSON-RPC workflow and several advanced features, including generated typed clients, batching, cancellation, events, progress and observers, by-reference interfaces, out-of-band streams, async enumerables, OpenRPC discovery, and W3C distributed tracing.
The clearest StreamJsonRpc capabilities without a direct equivalent here are runtime-generated interface proxies, runtime overload resolution, registering standalone methods or delegates (including explicit non-public invocation), and forwarding/intercepting calls through remote targets.
StreamJsonRpc also offers legacy tracing and serializer integrations that may matter for migration, but these are not protocol requirements and Nerdbank.JsonRpc has different extension points and defaults.

## Capability comparison

| StreamJsonRpc capability | Nerdbank.JsonRpc status | Notes |
| --- | --- | --- |
| Typed client proxies | Supported, with a different model | `Attach<T>` uses source-generated proxies; there is no general runtime `Reflection.Emit` proxy fallback for an interface that was not generated. The generator-first model is suitable for NativeAOT and trimming. See [generated client proxies](client-proxies.md) and StreamJsonRpc's [proxy documentation](https://microsoft.github.io/vs-streamjsonrpc/docs/proxies.html). |
| Loose/weakly typed calls | Supported | `RequestAsync`, `NotifyAsync`, `JsonRpcValue`, and argument builders cover direct calls without a generated proxy. |
| Multiple RPC targets and bidirectional connections | Supported | One `JsonRpc` connection can both register targets and attach proxies. |
| Overloaded server methods | **No overload dispatch** | Target registration rejects any duplicate wire method name, without considering whether overloads accept disjoint argument counts. For example, `Hi(string)` and `Hi(string, int)` would have non-overlapping positional arities, while making the second `int` optional creates an overlap; Nerdbank.JsonRpc rejects both same-name pairs at registration. StreamJsonRpc resolves overloads from argument shape and optional parameters at runtime. Supporting disjoint arity ranges is a plausible future dispatch rule, but it is not implemented today. See [method naming](method-naming.md) and StreamJsonRpc's [request reception documentation](https://microsoft.github.io/vs-streamjsonrpc/docs/recvrequest.html#overloadcaveats). |
| Individual methods registered from delegates or `MethodInfo` | **No direct equivalent** | Local methods are exposed by registering a shaped target. StreamJsonRpc can register individual delegates or reflected methods without introducing a target contract. See StreamJsonRpc's [individual method registration](https://microsoft.github.io/vs-streamjsonrpc/docs/recvrequest.html#registering-individual-methods). |
| Opt-in invocation of non-public target methods | **No documented equivalent** | Nerdbank.JsonRpc exposes methods from the registered type shape; it has no `AllowNonPublicInvocation`-style target switch. A narrow public interface/shape is the supported way to control exposure. See [security considerations](security.md#what-a-peer-can-invoke). |
| Remote-target forwarding and call interception | **Not supported as a built-in feature** | There is no direct counterpart to StreamJsonRpc's remote-target forwarding/interception facility for routing calls through an intermediate connection and allowing local methods to intercept matching calls. Applications can implement forwarding explicitly, but must own the routing logic. See StreamJsonRpc's [remote targets documentation](https://microsoft.github.io/vs-streamjsonrpc/docs/remotetargets.html). |
| Cancellation, request timeouts, batching, events, progress, observers, streams, async enumerables | Supported | These have direct counterparts; see [batching](batching.md), [events](events.md), [protocol behavior](protocol-behavior.md), and [RPC-marshalable interfaces](rpc-marshalable-interfaces.md). |
| JSON-RPC framing and transports | Different boundary | Nerdbank.JsonRpc provides JSON and MessagePack channels over `IDuplexPipe`, with the documented framing modes. It does not bundle StreamJsonRpc-style message-handler adapters for particular transports; applications adapt their transport to a duplex pipe. See [encodings and framing](encodings.md). |
| W3C activity tracing | Supported | Nerdbank.JsonRpc emits `ActivitySource` activities and propagates W3C `traceparent`/`tracestate`. |
| `TraceSource` / `CorrelationManager` tracing strategies | **No direct equivalent** | StreamJsonRpc documents both strategies. Nerdbank.JsonRpc uses `Microsoft.Extensions.Logging` and `ActivitySource` instead, so code directly consuming the older tracing APIs needs an adapter or migration. See StreamJsonRpc's [trace context documentation](https://microsoft.github.io/vs-streamjsonrpc/docs/tracecontext.html) and local [encoding/tracing documentation](encodings.md#logging-and-distributed-tracing). |
| Legacy formatter choices | **No built-in equivalent for every formatter** | StreamJsonRpc documents Newtonsoft.Json and its older MessagePack-CSharp formatter in addition to System.Text.Json and Nerdbank MessagePack. Nerdbank.JsonRpc provides UTF-8 JSON and Nerdbank MessagePack; applications depending on legacy serializer-specific behavior or exact MessagePack compatibility may need a conversion layer or a custom channel/serializer. See StreamJsonRpc's [protocol extensibility documentation](https://microsoft.github.io/vs-streamjsonrpc/docs/extensibility.html) and local [encoding documentation](encodings.md). |

## Practical priorities

For new applications, the main functional trade-offs to consider are dynamic proxy creation, overload dispatch, standalone method registration, and built-in remote-target forwarding/interception.
The first three can generally be addressed by generating a contract, giving methods distinct names, and registering a shaped target; forwarding/interception requires application-level routing.
The tracing and formatter differences primarily affect migration compatibility rather than JSON-RPC feature parity.

This comparison is a feature inventory, not a security audit or a guarantee of wire compatibility for every serializer configuration.
For interoperability, compare the exact encoding, framing, method-name transform, argument convention, and special-type protocol used by both peers.