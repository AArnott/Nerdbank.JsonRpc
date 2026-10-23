// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>
/// Identifies an interface that describes a JSON-RPC contract and enables contract validation.
/// </summary>
/// <remarks>
/// By default, a client proxy is generated for the interface. Apply
/// <c>[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]</c> from PolyType
/// to include the methods required for RPC dispatch.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = false)]
public sealed class JsonRpcContractAttribute : Attribute
{
	/// <summary>
	/// Gets or sets a value indicating whether a client proxy should be generated for this interface.
	/// </summary>
	/// <value><see langword="true"/> by default. Setting this to <see langword="false"/> does not disable contract analysis.</value>
	public bool GenerateProxy { get; set; } = true;
}
