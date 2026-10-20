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

	/// <summary>Gets or initializes the timeout applied to each outbound request that expects a response.</summary>
	/// <value>The timeout, or <see cref="Timeout.InfiniteTimeSpan"/> to disable this behavior. The default is <see cref="Timeout.InfiniteTimeSpan"/>.</value>
	/// <remarks>
	/// When the timeout expires, the request is canceled locally and a cancellation notification is sent to the peer.
	/// The peer may still be processing the request.
	/// </remarks>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when set to zero or a negative value other than <see cref="Timeout.InfiniteTimeSpan"/>.</exception>
	public TimeSpan OutboundRequestTimeout
	{
		get => field;
		init
		{
			if (value != Timeout.InfiniteTimeSpan && value <= TimeSpan.Zero)
			{
				throw new ArgumentOutOfRangeException(nameof(value), "The outbound request timeout must be positive or Timeout.InfiniteTimeSpan.");
			}

			field = value;
		}
	} = Timeout.InfiniteTimeSpan;

	/// <summary>Gets or initializes additional exception types that may be reconstructed from peer diagnostics.</summary>
	/// <remarks>The mapping is frozen when assigned and may then be shared across connections.</remarks>
	public RemoteExceptionTypeMapping AdditionalExceptionTypes
	{
		get => this.additionalExceptionTypes;
		init => this.additionalExceptionTypes = Requires.NotNull(value).Freeze();
	}
}
