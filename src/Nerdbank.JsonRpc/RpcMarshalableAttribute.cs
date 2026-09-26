// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>Indicates that values typed as this interface are marshaled by reference and can be invoked remotely.</summary>
/// <remarks>
/// Marshalable interfaces must extend <see cref="IDisposable"/> and may declare methods only.
/// Apply <c>[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]</c> from PolyType so the methods can be dispatched.
/// The generated proxy is disposed to release the remote object.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class RpcMarshalableAttribute : Attribute
{
}
