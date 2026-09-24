// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

[MessagePackConverter(typeof(JsonRpcMessageConverter))]
[GenerateShape]
public abstract partial class JsonRpcMessage
{
	private RequestId? id;

	[PropertyShape(Name = "id")]
	public RequestId? Id
	{
		get => this.id;
		init => this.SetReceivedId(value);
	}

	/// <summary>Gets a value indicating whether an ID was supplied, including an explicit nil ID.</summary>
	[PropertyShape(Ignore = true)]
	public bool HasId { get; private set; }

	[PropertyShape(IsRequired = true, Name = "jsonrpc")]
	public string Version { get; init; } = "2.0";

	internal void SetReceivedId(RequestId? id)
	{
		this.id = id;
		this.HasId = id.HasValue;
	}
}
