// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>Represents an event discovered on an RPC target object that should raise a JSON-RPC notification when the event is raised.</summary>
internal interface IEventTargetRegistration
{
	/// <summary>Subscribes a forwarding handler to the event on the given target instance.</summary>
	/// <param name="target">The target object instance that declares the event.</param>
	/// <param name="jsonRpc">The connection over which to send notifications when the event is raised.</param>
	/// <returns>A disposable that, when disposed, unsubscribes the forwarding handler from the event.</returns>
	IDisposable Subscribe(object? target, JsonRpc jsonRpc);
}
