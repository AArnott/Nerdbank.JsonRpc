// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft;

namespace Nerdbank.JsonRpc;

/// <summary>Provides immutable configuration that can be shared across independent <see cref="JsonRpc"/> connections.</summary>
/// <remarks>
/// These options describe how a connection should behave: its policies, limits, and conventions.
/// Supply live runtime infrastructure, such as a logger, synchronization context, or multiplexing stream,
/// on <see cref="JsonRpc"/> instead. Reusing options does not share connection runtime state.
/// </remarks>
public sealed record JsonRpcOptions
{
	private RemoteExceptionTypeMapping additionalExceptionTypes = RemoteExceptionTypeMapping.Empty;

	/// <summary>Gets the default options.</summary>
	public static JsonRpcOptions Default { get; } = new();

	/// <summary>Gets or initializes the maximum encoded message size, in bytes.</summary>
	/// <value>Defaults to 8 MiB. The built-in JSON and MessagePack channels apply this limit to received messages; the JSON channel also applies it to sent messages.</value>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when set to zero or a negative value.</exception>
	public int MaximumMessageSize
	{
		get => field;
		init
		{
			if (value <= 0)
			{
				throw new ArgumentOutOfRangeException(nameof(value), value, "A positive size is required.");
			}

			field = value;
		}
	} = JsonRpcPipeChannel.DefaultMaximumMessageSize;

	/// <summary>Gets or initializes the options used for proxies implicitly created for RPC-marshalable objects.</summary>
	/// <value>Defaults to <see cref="JsonRpcProxyOptions.Default"/>, the same naming convention used for ordinary RPC proxies.</value>
	/// <remarks>
	/// To communicate with StreamJsonRpc's default RPC-marshalable objects, set this property to
	/// <c>new() { MethodNameTransform = CommonMethodNameTransforms.Identity }</c> in the <see cref="JsonRpcOptions"/> object initializer.
	/// This does not change options for proxies attached through <see cref="JsonRpc.Attach{T}(JsonRpcProxyOptions?)"/>.
	/// </remarks>
	/// <exception cref="ArgumentNullException">Thrown when set to <see langword="null"/>.</exception>
	public JsonRpcProxyOptions MarshaledProxy
	{
		get => field;
		init => field = Requires.NotNull(value);
	} = JsonRpcProxyOptions.Default;

	/// <summary>Gets or initializes the options used when implicitly registering RPC-marshalable targets.</summary>
	/// <value>Defaults to <see cref="JsonRpcTargetOptions.Default"/>, the same naming convention used for ordinary RPC targets.</value>
	/// <remarks>
	/// To communicate with StreamJsonRpc's default RPC-marshalable objects, set this property to
	/// <c>new() { MethodNameTransform = CommonMethodNameTransforms.Identity }</c> in the <see cref="JsonRpcOptions"/> object initializer.
	/// This does not change options for targets registered through <see cref="JsonRpc.AddRpcTarget{T}(T, ITypeShape{T}, JsonRpcTargetOptions?)"/>.
	/// </remarks>
	/// <exception cref="ArgumentNullException">Thrown when set to <see langword="null"/>.</exception>
	public JsonRpcTargetOptions MarshaledTarget
	{
		get => field;
		init => field = Requires.NotNull(value);
	} = JsonRpcTargetOptions.Default;

	/// <summary>Gets a value indicating whether exception details are included in error responses.</summary>
	/// <value>The default is <see langword="true"/>. Local logging always retains the full exception.</value>
	public bool IncludeExceptionDetails { get; init; } = true;

	/// <summary>Gets or initializes the timeout applied to each outbound request that expects a response.</summary>
	/// <value>The timeout, or <see cref="Timeout.InfiniteTimeSpan"/> to disable this behavior. The default is <see cref="Timeout.InfiniteTimeSpan"/>.</value>
	/// <remarks>
	/// When the timeout expires, the request is canceled locally and a cancellation notification is sent to the peer.
	/// The peer may still be processing the request.
	/// The maximum finite timeout is <see cref="int.MaxValue"/> milliseconds, for compatibility with all supported target frameworks.
	/// </remarks>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when set to zero, a negative value other than <see cref="Timeout.InfiniteTimeSpan"/>, or more than <see cref="int.MaxValue"/> milliseconds.</exception>
	public TimeSpan OutboundRequestTimeout
	{
		get => field;
		init
		{
			if (value != Timeout.InfiniteTimeSpan && (value <= TimeSpan.Zero || value > TimeSpan.FromMilliseconds(int.MaxValue)))
			{
				throw new ArgumentOutOfRangeException(nameof(value), "The outbound request timeout must be positive and no greater than int.MaxValue milliseconds, or Timeout.InfiniteTimeSpan.");
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
