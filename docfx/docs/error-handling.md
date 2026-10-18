# Error handling

A failed remote call completes locally with <xref:Nerdbank.JsonRpc.RemoteInvocationException>. This is the consistent exception type for error responses received from a peer; it derives from <xref:Nerdbank.JsonRpc.JsonRpcException>, so existing catches for that base type continue to work. Transport failures and other local failures are not wrapped as remote invocation exceptions.

By default, Nerdbank.JsonRpc includes bounded exception diagnostics in the optional JSON-RPC `error.data` property. Its core shape is compatible with StreamJsonRpc's CommonErrorData: `type` (the CLR full name, without assembly identity), `message`, `stack`, `code` (the exception HRESULT), and `inner`. The JSON and MessagePack encodings use the same data, with MessagePack retaining CommonErrorData's integer map keys 0 through 4. Nerdbank.JsonRpc adds `parameterName`, aggregate `children`, string-valued exception `data`, and the optional shape-serialized `exception` payload as extensions. StreamJsonRpc peers may ignore those additions.

The diagnostics are untrusted input. Nerdbank.JsonRpc never resolves a peer-supplied type name or activates arbitrary exception types. It reconstructs only these exact built-in types:

- <xref:System.Exception>
- <xref:System.InvalidOperationException>
- <xref:System.NotSupportedException>
- <xref:System.FormatException>
- <xref:System.ArgumentException>
- <xref:System.ArgumentNullException>
- <xref:System.ArgumentOutOfRangeException>
- <xref:System.AggregateException>
- <xref:System.OperationCanceledException>
- <xref:System.Threading.Tasks.TaskCanceledException>

The list is implemented in [`RemoteExceptionData.Reconstruct`](https://github.com/AArnott/Nerdbank.JsonRpc/blob/main/src/Nerdbank.JsonRpc/RemoteExceptionData.cs). Other types are represented by a diagnostic inner exception, while their original full names and data remain available through <xref:Nerdbank.JsonRpc.RemoteInvocationException.RemoteException>. The captured cause chain and text are bounded to limit resource use. Only string-valued entries from <xref:System.Exception.Data> are included in the interoperable diagnostic fields. Arbitrary custom exception properties are not serialized unless the type is explicitly allowlisted with a marshaled shape.

## Catching and classifying remote failures

Catch the stable remote wrapper first. If a proprietary type is available locally and allowlisted on the receiving connection, branch on the reconstructed inner exception type; otherwise branch on the preserved assembly-independent type name:

[!code-csharp[](../../samples/cs/error-handling.cs#catch-remote-exception)]

`RemoteInvocationException.ToString()` prints remote throw stacks and nested remote causes as labeled sections, followed by the local rethrow stack. Treat remote stack traces and messages as diagnostic text, not as trustworthy local call stacks. Logging on the receiving side should also be considered when exposing peer-supplied text to log viewers.

## Allowing additional exception types

Applications may opt in to serializing and reconstructing selected exception types by registering their source-generated shapes. Each exception shape must declare a PolyType marshaler to a data-only surrogate. This avoids serializing the framework's `Exception` implementation details and gives the application explicit control over which properties cross the boundary; PolyType uses the marshaler to reconstruct the exception without a registration factory. Constructors used by the marshaler should only consume validated data:

[!code-csharp[](../../samples/cs/error-handling.cs#allow-exception-type)]

The exception mapping is frozen when assigned to `JsonRpcOptions`, so the options can be reused safely across independent connections. Types match by assembly-independent CLR full name. A shape's marshaler defines the typed payload; the common diagnostics remain bounded and available through <xref:Nerdbank.JsonRpc.RemoteInvocationException.RemoteException>, including when no compatible local shape is registered. To send extra diagnostic text that non-.NET peers can inspect, add string keys and values to the thrown exception's <xref:System.Exception.Data>; the snapshot exposes those entries through `RemoteExceptionData.Data`.

## Disabling peer-visible diagnostics

Full details are included by default for trusted peers. For a connection whose peer should receive only a generic message, use reusable options:

[!code-csharp[](../../samples/cs/error-handling.cs#disable-details)]

The full local exception is still logged by the server. This option controls only details placed in error responses. Connection-loss diagnostic improvements are tracked separately in [issue #107](https://github.com/AArnott/Nerdbank.JsonRpc/issues/107).
