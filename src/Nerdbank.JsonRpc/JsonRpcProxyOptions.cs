// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft;

namespace Nerdbank.JsonRpc;

/// <summary>
/// Provides options that control generated JSON-RPC client proxy attachment.
/// </summary>
public sealed record JsonRpcProxyOptions
{
	private Func<string, string> methodNameTransform = CommonMethodNameTransforms.Default;
	private Func<string, string> eventNameTransform = CommonMethodNameTransforms.CamelCase;

	/// <summary>Gets a shared instance with default settings.</summary>
	public static JsonRpcProxyOptions Default { get; } = new();

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
