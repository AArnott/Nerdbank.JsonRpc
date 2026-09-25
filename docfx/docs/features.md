# Features

Nerdbank.JsonRpc provides strongly typed JSON-RPC requests, notifications, and server dispatch over MessagePack or UTF-8 JSON. MessagePack is the default encoding.

- **Typed RPC:** Register server targets using PolyType method shapes and call methods through typed APIs. [Get started](getting-started.md).
- **Choice of encoding and framing:** Configure a MessagePack or JSON serializer; JSON supports newline-delimited and `Content-Length` framing. [Choose an encoding](encodings.md).
- **Generated client proxies:** Attach an interface-backed proxy instead of constructing requests manually, with positional or named arguments. [Use client proxies](client-proxies.md).
- **Method name transforms:** CLR method names map to camelCase wire names by default, with explicit names and StreamJsonRpc interop supported. [Configure method naming](method-naming.md).
- **Events as notifications:** Raising a CLR event on a registered target sends a JSON-RPC notification to the remote party. [Send events as notifications](events.md).
- **Batching:** Send several independent requests or notifications in one JSON-RPC payload. [Send a batch](batching.md).
- **Cancellation and failure handling:** Propagate cancellation and distinguish malformed protocol envelopes from application-value conversion failures. [Understand protocol behavior](protocol-behavior.md).
