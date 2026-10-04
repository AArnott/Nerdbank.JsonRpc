// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft;

namespace Nerdbank.JsonRpc;

/// <summary>Provides immutable configuration that can be shared across independent <see cref="JsonRpc"/> connections.</summary>
public sealed record JsonRpcOptions
{
	private RemoteExceptionTypeMapping additionalExceptionTypes = RemoteExceptionTypeMapping.Empty;

	/// <summary>Gets the default options.</summary>
	public static JsonRpcOptions Default { get; } = new();

	/// <summary>Gets a value indicating whether exception details are included in error responses.</summary>
	/// <value>The default is <see langword="true"/>. Local logging always retains the full exception.</value>
	public bool IncludeExceptionDetails { get; init; } = true;

	/// <summary>Gets or initializes additional exception types that may be reconstructed from peer diagnostics.</summary>
	/// <remarks>The mapping is frozen when assigned and may then be shared across connections.</remarks>
	public RemoteExceptionTypeMapping AdditionalExceptionTypes
	{
		get => this.additionalExceptionTypes;
		init => this.additionalExceptionTypes = Requires.NotNull(value).Freeze();
	}
}
