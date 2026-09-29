// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>Carries the scopes that apply to one serialization or deserialization job.</summary>
/// <remarks>
/// <para>
/// Converters that marshal objects, progress reporters, out-of-band streams and async enumerables need to reach the
/// scope established by the RPC call that is currently being encoded or decoded. This state is handed to the
/// serializer for each job and republished to converters through the serializer's own context state bag, which keeps
/// concurrent calls isolated without the execution-context copying that ambient storage would require.
/// </para>
/// </remarks>
internal sealed class RpcCallState
{
	/// <summary>The key under which an instance of this class is stored in a serializer's context state.</summary>
	internal static readonly object ContextKey = new();

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

	/// <summary>Retrieves the state published to a MessagePack (de)serialization job.</summary>
	/// <param name="context">The converter's context.</param>
	/// <returns>The state, or <see langword="null"/> if the job carries no RPC call scopes.</returns>
	internal static RpcCallState? From(in Nerdbank.MessagePack.SerializationContext context) => (RpcCallState?)context[ContextKey];

	/// <summary>Retrieves the state published to a JSON (de)serialization job.</summary>
	/// <param name="context">The converter's context.</param>
	/// <returns>The state, or <see langword="null"/> if the job carries no RPC call scopes.</returns>
	internal static RpcCallState? From(in Nerdbank.Json.SerializationContext context) => (RpcCallState?)context[ContextKey];
}
