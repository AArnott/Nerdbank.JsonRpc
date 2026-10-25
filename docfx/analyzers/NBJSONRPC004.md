# NBJSONRPC004: Dispose of brokered-service proxies

| Property | Value |
| --- | --- |
| ID | `NBJSONRPC004` |
| Category | Usage |
| Default severity | Warning |

Proxies acquired from <xref:Nerdbank.JsonRpc.BrokeredServices.IServiceBroker.GetProxyAsync*?displayProperty=nameWithType> own resources even when their service interfaces do not derive from <xref:System.IDisposable>.
Dispose of each proxy when finished, or explicitly transfer ownership by returning it to the caller.
The analyzer also recognizes implementations of the broker interface, local aliases, casts, `using` statements, `using` declarations, awaited asynchronous disposal, and `try`/`finally`.

## Local proxies

The following acquisition leaks its proxy:

[!code-csharp[](../../samples/cs/brokered-service-lifetimes.cs#brokered-undisposed-proxy)]

Dispose of the proxy through its lifetime interface:

[!code-csharp[](../../samples/cs/brokered-service-lifetimes.cs#brokered-dispose-with-cast)]

An `await using IAsyncDisposable lifetime = (IAsyncDisposable)greeter;` declaration is also supported.
Disposal must occur on every analyzed exit path, not just one branch.
Calling `DisposeAsync` without awaiting or returning its result is not sufficient.

## Longer-lived proxies

When assigning a proxy to a field or property, its owning type must implement <xref:System.IDisposable> or <xref:System.IAsyncDisposable> and dispose of the member in its disposal method.
Dispose of the previous value before replacement, or guard acquisition with a null check or `??=` that establishes that no previous value exists.
When the owner implements both disposal interfaces, both disposal implementations must release the proxy.

[!code-csharp[](../../samples/cs/brokered-service-lifetimes.cs#brokered-disposable-owner)]

The analysis is local to the acquisition method and the owner's disposal implementation.
It does not prove arbitrary ownership transfers through helper methods, containers, or custom lifetime wrappers.
It also does not model exceptions implicitly thrown by arbitrary calls.
No automatic code fix is offered because choosing the correct lifetime and ownership boundary requires application context.
For an intentional transfer that the analyzer cannot recognize, suppress the diagnostic narrowly and document the receiving owner's disposal responsibility.
This rule is the Nerdbank.JsonRpc counterpart of ServiceHub's ISB001.
