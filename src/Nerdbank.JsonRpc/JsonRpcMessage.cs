// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

[GenerateShape]
public abstract partial class JsonRpcMessage
{
	private RequestId id = RequestId.Absent;

	[PropertyShape(Name = "id")]
	public RequestId? Id
	{
		get => this.id.IsAbsent ? (RequestId?)null : this.id;
		init => this.SetReceivedId(value);
	}

	/// <summary>Gets a value indicating whether an ID was supplied, including an explicit nil ID.</summary>
	[PropertyShape(Ignore = true)]
	public bool HasId => !this.id.IsAbsent;

	// The default value is deliberately not declared to the serializer: the JSON-RPC envelope must always carry this property.
#pragma warning disable NBMsgPack110
	[PropertyShape(IsRequired = true, Name = "jsonrpc")]
	public string Version { get; init; } = "2.0";
#pragma warning restore NBMsgPack110

	/// <summary>Gets or sets the extension properties carried at the top level of this message's envelope, if any.</summary>
	[PropertyShape(Ignore = true)]
	internal TopLevelProperties? TopLevelProperties { get; set; }

	/// <summary>Sets a top-level extension property on this message.</summary>
	/// <param name="name">The property name, which must not be a JSON-RPC reserved name.</param>
	/// <param name="value">The primitive value.</param>
	internal void SetTopLevelProperty(string name, TopLevelPropertyValue value) => (this.TopLevelProperties ??= new()).Set(name, value);

	/// <summary>Gets a top-level extension property from this message.</summary>
	/// <param name="name">The property name.</param>
	/// <param name="value">Receives the value.</param>
	/// <returns><see langword="true"/> if the property is present.</returns>
	internal bool TryGetTopLevelProperty(string name, out TopLevelPropertyValue value)
	{
		if (this.TopLevelProperties is { } properties)
		{
			return properties.TryGet(name, out value);
		}

		value = default;
		return false;
	}

	internal void SetReceivedId(RequestId? id) => this.id = id ?? RequestId.Absent;
}
