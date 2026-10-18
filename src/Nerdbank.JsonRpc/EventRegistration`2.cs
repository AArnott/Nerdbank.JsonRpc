// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType.Abstractions;

namespace Nerdbank.JsonRpc;

/// <summary>Subscribes to a single CLR event and forwards its invocations to the remote party as JSON-RPC notifications.</summary>
/// <typeparam name="TDeclaringType">The type that declares the event.</typeparam>
/// <typeparam name="TEventHandler">The event's handler delegate type.</typeparam>
/// <param name="rpcEventName">The RPC notification (method) name to use for the forwarded event.</param>
/// <param name="createHandler">Creates a handler delegate that forwards invocations to the remote party as a notification.</param>
/// <param name="addHandler">Subscribes a handler delegate to the event on a target instance.</param>
/// <param name="removeHandler">Unsubscribes a handler delegate from the event on a target instance.</param>
internal sealed class EventRegistration<TDeclaringType, TEventHandler>(
	string rpcEventName,
	CreateEventHandlerDelegate createHandler,
	Setter<TDeclaringType?, TEventHandler> addHandler,
	Setter<TDeclaringType?, TEventHandler> removeHandler) : IEventTargetRegistration
{
	/// <inheritdoc/>
	public IDisposable Subscribe(object? target, JsonRpc jsonRpc)
	{
		TDeclaringType? typedTarget = (TDeclaringType?)target;
		TEventHandler handler = (TEventHandler)(object)createHandler(jsonRpc, rpcEventName)!;
		addHandler(ref typedTarget, handler);
		return new Subscription(removeHandler, typedTarget, handler);
	}

	private sealed class Subscription(Setter<TDeclaringType?, TEventHandler> removeHandler, TDeclaringType? target, TEventHandler handler) : IDisposable
	{
		public void Dispose()
		{
			TDeclaringType? typedTarget = target;
			removeHandler(ref typedTarget, handler);
		}
	}
}
