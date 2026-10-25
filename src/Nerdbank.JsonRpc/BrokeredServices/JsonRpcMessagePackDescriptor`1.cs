// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Describes a service connection using MessagePack.</summary>
/// <typeparam name="T">The primary service interface.</typeparam>
public record JsonRpcMessagePackDescriptor<T> : JsonRpcDescriptor<T>
	where T : class
{
	/// <summary>Initializes a new instance of the <see cref="JsonRpcMessagePackDescriptor{T}"/> class.</summary>
	/// <param name="moniker">The service identity.</param>
	/// <param name="shape">The generated primary interface shape.</param>
	public JsonRpcMessagePackDescriptor(ServiceMoniker moniker, ITypeShape<T> shape)
		: base(moniker, shape)
	{
	}

	/// <summary>Gets the MessagePack framing, defaulting to a four-byte length prefix.</summary>
	public JsonRpcMessagePackFraming Framing { get; init; } = JsonRpcMessagePackChannel.DefaultFraming;

	/// <summary>Gets the serializer used for application values and protocol messages.</summary>
	/// <remarks>Use a record copy to configure the serializer without changing other descriptor instances.</remarks>
	public Nerdbank.MessagePack.MessagePackSerializer Serializer
	{
		get;
		init => field = value ?? throw new ArgumentNullException(nameof(value));
	} = JsonRpcMessagePackChannel.DefaultSerializer;

	/// <inheritdoc/>
	protected override JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe)
		=> new JsonRpcMessagePackChannel(pipe, this.Serializer, this.Framing);
}
