// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>Specifies the additional interfaces implemented by one source-generated proxy for the annotated contract.</summary>
/// <remarks>
/// The annotated interface is always included. Each distinct group generates one proxy class.
/// When any groups are declared, only those groups are generated; use an empty group to request a base-only proxy.
/// Without groups, a base-only proxy is generated, together with a full optional-interface proxy for marshalable contracts.
/// On a marshalable contract, additional interfaces must be declared with <see cref="RpcMarshalableOptionalInterfaceAttribute"/>.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, Inherited = false, AllowMultiple = true)]
public sealed class JsonRpcProxyInterfaceGroupAttribute : Attribute
{
	/// <summary>Initializes a new instance of the <see cref="JsonRpcProxyInterfaceGroupAttribute"/> class.</summary>
	/// <param name="additionalInterfaces">The additional interfaces included alongside the annotated contract.</param>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="additionalInterfaces"/> is null.</exception>
	/// <exception cref="ArgumentException">Thrown when an entry is null or is not an interface.</exception>
	public JsonRpcProxyInterfaceGroupAttribute(params Type[] additionalInterfaces)
	{
		if (additionalInterfaces is null)
		{
			throw new ArgumentNullException(nameof(additionalInterfaces));
		}

		if (additionalInterfaces.Any(static type => type is null || !type.IsInterface))
		{
			throw new ArgumentException("Proxy groups must contain non-null interface types.", nameof(additionalInterfaces));
		}

		this.AdditionalInterfaces = Array.AsReadOnly((Type[])additionalInterfaces.Clone());
	}

	/// <summary>Gets the additional interfaces included alongside the annotated contract.</summary>
	public IReadOnlyList<Type> AdditionalInterfaces { get; }
}
