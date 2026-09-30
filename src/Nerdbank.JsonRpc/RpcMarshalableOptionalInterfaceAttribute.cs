// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NET
using System.Diagnostics.CodeAnalysis;
#endif

namespace Nerdbank.JsonRpc;

/// <summary>Declares an optional interface that a marshalable object may implement.</summary>
/// <remarks>
/// When a value implements <see cref="OptionalInterface"/>, its marker advertises <see cref="InterfaceId"/> and
/// the receiver's generated proxy implements that interface. Interface IDs are permanent protocol identifiers and
/// must be unique among attributes applied to the same marshalable interface.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = true)]
public sealed class RpcMarshalableOptionalInterfaceAttribute : Attribute
{
	/// <summary>Initializes a new instance of the <see cref="RpcMarshalableOptionalInterfaceAttribute"/> class.</summary>
	/// <param name="interfaceId">The stable signed 32-bit protocol identifier for the optional interface.</param>
	/// <param name="optionalInterface">The optional RPC interface type.</param>
	public RpcMarshalableOptionalInterfaceAttribute(
		int interfaceId,
#if NET
		[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type optionalInterface)
#else
		Type optionalInterface)
#endif
	{
		this.InterfaceId = interfaceId;
		this.OptionalInterface = optionalInterface ?? throw new ArgumentNullException(nameof(optionalInterface));
#if !NETWASM // NetWasm: Type.IsInterface is unavailable.
		if (!optionalInterface.IsInterface)
		{
			throw new ArgumentException("The optional type must be an interface.", nameof(optionalInterface));
		}
#endif
	}

	/// <summary>Gets the stable signed 32-bit protocol identifier.</summary>
	public int InterfaceId { get; }

	/// <summary>Gets the optional RPC interface type.</summary>
#if NET
	[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
#endif
	public Type OptionalInterface { get; }
}
