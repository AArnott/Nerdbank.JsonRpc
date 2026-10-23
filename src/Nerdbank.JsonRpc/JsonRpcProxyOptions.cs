// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft;

namespace Nerdbank.JsonRpc;

/// <summary>
/// Provides options that control generated JSON-RPC client proxy attachment.
/// </summary>
public sealed record JsonRpcProxyOptions
{
	private IReadOnlyList<Type>? enabledInterfaces;

	private Func<string, string> methodNameTransform = CommonMethodNameTransforms.Default;
	private Func<string, string> eventNameTransform = CommonMethodNameTransforms.CamelCase;

	/// <summary>Gets a shared instance with default settings.</summary>
	public static JsonRpcProxyOptions Default { get; } = new();

	/// <summary>Gets the contract interfaces considered enabled by <see cref="IClientProxy.Is"/>.</summary>
	/// <value>
	/// A list of enabled interfaces, or <see langword="null"/> (the default) to enable all contract interfaces implemented by the proxy.
	/// An empty list disables all interfaces for capability queries.
	/// </value>
	/// <remarks>
	/// The list is copied when assigned. Inherited interfaces of each listed interface are also enabled,
	/// but interfaces not implemented by the generated proxy are never reported as enabled.
	/// For root proxies, supply capabilities learned out of band. The smallest generated group covering the known enabled
	/// interfaces is selected; it may implement extra CLR interfaces. With a null list, the smallest generated group is selected.
	/// For marshaled proxies, capability queries are based on the peer's advertisement; this list can only restrict them further.
	/// This setting does not prevent direct method calls or negotiate capabilities with the peer.
	/// </remarks>
	/// <exception cref="ArgumentException">Thrown when an entry is null or is not an interface type.</exception>
	public IReadOnlyList<Type>? EnabledInterfaces
	{
		get => this.enabledInterfaces;
		init
		{
			if (value is null)
			{
				this.enabledInterfaces = null;
				return;
			}

			Type[] interfaces = value.ToArray();
			Requires.Argument(interfaces.All(static type => type is not null && type.IsInterface), nameof(value), "Enabled types must be non-null interface types.");
			this.enabledInterfaces = Array.AsReadOnly(interfaces);
		}
	}

	/// <summary>Gets a value indicating whether requests encode arguments by parameter name rather than position.</summary>
	public bool UseNamedArguments { get; init; }

	/// <summary>
	/// Gets a function that maps a CLR method name without an explicit <see cref="MethodShapeAttribute.Name"/> to the JSON-RPC method name sent on the wire.
	/// </summary>
	/// <value>
	/// A non-<see langword="null" /> function. The default value is <see cref="CommonMethodNameTransforms.Default"/>, which removes a trailing
	/// <c>Async</c> suffix and converts the result to camelCase.
	/// </value>
	/// <remarks>
	/// Methods explicitly named via <see cref="MethodShapeAttribute.Name"/> always use that exact name and bypass this transform.
	/// Use <see cref="CommonMethodNameTransforms.Identity"/> to interoperate with a StreamJsonRpc peer using its default (untransformed) method naming.
	/// </remarks>
	/// <exception cref="ArgumentNullException">Thrown when set to <see langword="null"/>.</exception>
	public Func<string, string> MethodNameTransform
	{
		get => this.methodNameTransform;
		init => this.methodNameTransform = Requires.NotNull(value);
	}

	/// <summary>Gets the transform from CLR event names to incoming notification names.</summary>
	/// <value>A non-null function, defaulting to <see cref="CommonMethodNameTransforms.CamelCase"/>.</value>
	/// <remarks>An explicit <see cref="EventShapeAttribute.Name"/> bypasses this transform. Subscribe before starting the connection to avoid losing early notifications.</remarks>
	/// <exception cref="ArgumentNullException">Thrown when set to <see langword="null"/>.</exception>
	public Func<string, string> EventNameTransform
	{
		get => this.eventNameTransform;
		init => this.eventNameTransform = Requires.NotNull(value);
	}
}
