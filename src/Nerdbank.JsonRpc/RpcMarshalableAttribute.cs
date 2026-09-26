// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>Indicates that values typed as this interface are marshaled by reference and can be invoked remotely.</summary>
/// <remarks>
/// Marshalable interfaces must declare methods only. Explicit-lifetime interfaces must extend <see cref="IDisposable"/>;
/// set <see cref="CallScopedLifetime"/> to use a proxy only for the duration of the RPC call that receives it.
/// Apply <c>[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]</c> from PolyType so the methods can be dispatched.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class RpcMarshalableAttribute : Attribute
{
	/// <summary>Gets or sets a value indicating whether the marshaled proxy is limited to the receiving RPC call.</summary>
	/// <remarks>Call-scoped interfaces may be sent only as request arguments, not as results or notification arguments.</remarks>
	public bool CallScopedLifetime { get; set; }
}
