// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>Carries the scopes that apply to one serialization or deserialization job.</summary>
/// <remarks>
/// <para>
/// Converters that marshal objects, progress reporters, out-of-band streams and async enumerables need to reach the
/// scope established by the RPC call that is currently being encoded or decoded.
/// </para>
/// <para>
/// A (de)serialization job runs to completion synchronously on the thread that starts it, so the job's state is
/// published in a thread-static slot for the duration of that call. Concurrent calls therefore remain isolated
/// without the execution-context copying that ambient storage requires, and without the per-job state bag
/// allocation that publishing through the serializer's context would incur.
/// </para>
/// </remarks>
internal sealed class RpcCallState
{
	[ThreadStatic]
	private static RpcCallState? current;

	/// <summary>Gets the state of the (de)serialization job running on this thread, if any.</summary>
	internal static RpcCallState? Current => current;

	/// <summary>Gets or sets the scope collecting objects marshaled into the message being written.</summary>
	internal MarshaledObjectManager.HandleScope? MarshaledObjects { get; set; }

	/// <summary>Gets or sets the scope describing the inbound call whose values are being decoded.</summary>
	internal MarshaledObjectManager.InboundCallScope? InboundCall { get; set; }

	/// <summary>Gets or sets the scope collecting progress registrations created while writing the message.</summary>
	internal ProgressManager.RegistrationScope? ProgressRegistrations { get; set; }

	/// <summary>Gets or sets the scope receiving progress proxies created while reading the message.</summary>
	internal ProgressManager.InboundScope? ProgressInbound { get; set; }

	/// <summary>Gets or sets the scope collecting out-of-band channels offered by the message being written.</summary>
	internal OutOfBandStreamManager.OutboundScope? OutOfBandStreamsOutbound { get; set; }

	/// <summary>Gets or sets the scope accepting out-of-band channels named by the message being read.</summary>
	internal OutOfBandStreamManager.InboundScope? OutOfBandStreamsInbound { get; set; }

	/// <summary>Gets or sets the scope collecting sequence generators created while writing the message.</summary>
	internal AsyncEnumerableManager.OutboundScope? AsyncEnumerablesOutbound { get; set; }

	/// <summary>Gets or sets the scope receiving sequence consumers created while reading the message.</summary>
	internal AsyncEnumerableManager.InboundScope? AsyncEnumerablesInbound { get; set; }

	/// <summary>Publishes state to the converters taking part in one (de)serialization job.</summary>
	/// <param name="callState">The state to publish, if any.</param>
	/// <returns>A frame that must be disposed when the job completes.</returns>
	/// <remarks>
	/// A converter may encode a nested value with its own state; the prior state is restored when the nested job's
	/// frame is disposed.
	/// </remarks>
	internal static Frame Enter(RpcCallState? callState)
	{
		RpcCallState? prior = current;
		current = callState;
		return new(prior);
	}

	/// <summary>Restores the state that was current before a (de)serialization job began.</summary>
	/// <param name="prior">The state to restore.</param>
	internal readonly struct Frame(RpcCallState? prior) : IDisposable
	{
		/// <inheritdoc/>
		public void Dispose() => current = prior;
	}
}
