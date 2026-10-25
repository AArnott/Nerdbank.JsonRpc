// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Describes a service connection using UTF-8 JSON.</summary>
/// <remarks>Uses the Nerdbank.Json serializer, which supports UTF-8, not UTF-16.</remarks>
/// <typeparam name="T">The primary service interface.</typeparam>
public record JsonRpcUtf8Descriptor<T> : JsonRpcDescriptor<T>
	where T : class
{
	/// <summary>Initializes a new instance of the <see cref="JsonRpcUtf8Descriptor{T}"/> class.</summary>
	/// <param name="moniker">The service identity.</param>
	/// <param name="shape">The generated primary interface shape.</param>
	public JsonRpcUtf8Descriptor(ServiceMoniker moniker, ITypeShape<T> shape)
		: base(moniker, shape)
	{
	}

	/// <summary>Gets the JSON framing, defaulting to Content-Length headers.</summary>
	public JsonRpcJsonFraming Framing { get; init; } = JsonRpcJsonFraming.ContentLength;

	/// <summary>Gets the serializer used for application values and protocol messages.</summary>
	/// <remarks>Use a record copy to configure the serializer without changing other descriptor instances.</remarks>
	public Nerdbank.Json.JsonSerializer Serializer
	{
		get;
		init => field = value ?? throw new ArgumentNullException(nameof(value));
	} = new();

	/// <inheritdoc/>
	protected override JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe)
		=> new JsonRpcJsonChannel(pipe, this.Serializer, this.Framing);
}
