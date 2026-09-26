// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>Bundles the method invokers and event registrations discovered on an RPC target object.</summary>
/// <param name="methodInvokers">The method invokers discovered on the target object, keyed by their RPC method name.</param>
/// <param name="events">The event registrations discovered on the target object.</param>
internal sealed class TargetRegistration(Dictionary<string, MethodInvoker> methodInvokers, IReadOnlyList<IEventTargetRegistration> events)
{
	/// <summary>Gets the method invokers discovered on the target object, keyed by their RPC method name.</summary>
	internal Dictionary<string, MethodInvoker> MethodInvokers => methodInvokers;

	/// <summary>Gets the event registrations discovered on the target object.</summary>
	internal IReadOnlyList<IEventTargetRegistration> Events => events;
}
