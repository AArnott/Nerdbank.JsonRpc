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
}
