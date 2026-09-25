// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft;

namespace Nerdbank.JsonRpc;

/// <summary>
/// Provides commonly used transforms for mapping CLR method names to JSON-RPC method names,
/// for use with <see cref="JsonRpcProxyOptions.MethodNameTransform"/> and <see cref="JsonRpcTargetOptions.MethodNameTransform"/>.
/// </summary>
public static class CommonMethodNameTransforms
{
	/// <summary>
	/// Gets the default transform: removes a trailing <c>Async</c> suffix (if any) and converts the result to camelCase.
	/// </summary>
	/// <remarks>
	/// For example, <c>GetValueAsync</c> becomes <c>getValue</c> and <c>Ping</c> becomes <c>ping</c>.
	/// </remarks>
	public static Func<string, string> Default { get; } = name => CamelCase(RemoveAsyncSuffix(name));

	/// <summary>
	/// Gets a transform that returns the CLR method name unchanged.
	/// </summary>
	/// <remarks>
	/// Use this to interoperate with a StreamJsonRpc peer that has not configured its own method name transform,
	/// since StreamJsonRpc's default behavior is to send and expect CLR-style names (e.g. <c>GetValueAsync</c>).
	/// </remarks>
	public static Func<string, string> Identity { get; } = name => Requires.NotNull(name);

	/// <summary>
	/// Removes a trailing ordinal <c>Async</c> suffix from a name, if present.
	/// </summary>
	/// <param name="name">The name to transform.</param>
	/// <returns>The name without a trailing <c>Async</c> suffix.</returns>
	public static string RemoveAsyncSuffix(string name)
	{
		Requires.NotNull(name);
		const string suffix = "Async";
		return name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal)
			? name.Substring(0, name.Length - suffix.Length)
			: name;
	}

	/// <summary>
	/// Converts the first character of a name to lowercase, leaving the rest unchanged.
	/// </summary>
	/// <param name="name">The name to transform.</param>
	/// <returns>The camelCased name.</returns>
	public static string CamelCase(string name)
	{
		Requires.NotNull(name);
		return name.Length == 0 || char.IsLower(name[0])
			? name
			: char.ToLowerInvariant(name[0]) + name.Substring(1);
	}
}
