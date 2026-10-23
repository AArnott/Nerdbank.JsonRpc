// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft;

namespace Nerdbank.JsonRpc;

/// <summary>
/// Provides options that control how a local object's methods are registered as JSON-RPC targets.
/// </summary>
public sealed record JsonRpcTargetOptions
{
	private Func<string, string> methodNameTransform = CommonMethodNameTransforms.Default;
	private Func<string, string> eventNameTransform = CommonMethodNameTransforms.CamelCase;

	/// <summary>Gets a shared instance with default settings.</summary>
	public static JsonRpcTargetOptions Default { get; } = new();

	/// <summary>
	/// Gets a function that maps a CLR method name without an explicit <see cref="MethodShapeAttribute.Name"/> to the JSON-RPC method name that dispatches to it.
	/// </summary>
	/// <value>
	/// A non-<see langword="null" /> function. The default value is <see cref="CommonMethodNameTransforms.Default"/>, which removes a trailing
	/// <c>Async</c> suffix and converts the result to camelCase.
	/// </value>
	/// <remarks>
	/// Methods explicitly named via <see cref="MethodShapeAttribute.Name"/> always dispatch on that exact name and bypass this transform.
	/// Use <see cref="CommonMethodNameTransforms.Identity"/> to interoperate with a StreamJsonRpc peer using its default (untransformed) method naming.
	/// </remarks>
	/// <exception cref="ArgumentNullException">Thrown when set to <see langword="null"/>.</exception>
	public Func<string, string> MethodNameTransform
	{
		get => this.methodNameTransform;
		init => this.methodNameTransform = Requires.NotNull(value);
	}

	/// <summary>
	/// Gets a value indicating whether named arguments may omit target method parameters or include names that do not match any parameter.
	/// </summary>
	/// <value>The default is <see langword="false"/>.</value>
	/// <remarks>
	/// <para>
	/// When <see langword="true"/>, named arguments that do not match any target method parameter are ignored.
	/// Parameters without a corresponding named argument receive their declared default value, or the default value for their parameter type when no default is declared.
	/// Parameters annotated with <c>[ParameterShape(IsRequired = true)]</c> must have a corresponding named argument.
	/// </para>
	/// <para>
	/// This option does not affect positional arguments.
	/// </para>
	/// </remarks>
	public bool AllowFlexibleNamedArgumentMatching { get; init; }

	/// <summary>
	/// Gets a value indicating whether events raised on the target object should be relayed to the remote party
	/// via a JSON-RPC notification.
	/// </summary>
	/// <value>The default is <see langword="true"/>.</value>
	public bool NotifyClientOfEvents { get; init; } = true;

	/// <summary>
	/// Gets a function that maps a CLR event name without an explicit <see cref="EventShapeAttribute.Name"/> to the JSON-RPC method name used in the outbound notification.
	/// </summary>
	/// <value>
	/// A non-<see langword="null" /> function. The default value is <see cref="CommonMethodNameTransforms.CamelCase"/>.
	/// </value>
	/// <remarks>
	/// Events explicitly named via <see cref="EventShapeAttribute.Name"/> always notify under that exact name and bypass this transform.
	/// This property has no effect when <see cref="NotifyClientOfEvents"/> is <see langword="false"/>.
	/// </remarks>
	/// <exception cref="ArgumentNullException">Thrown when set to <see langword="null"/>.</exception>
	public Func<string, string> EventNameTransform
	{
		get => this.eventNameTransform;
		init => this.eventNameTransform = Requires.NotNull(value);
	}
}
