// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

[GenerateShape]
public partial class JsonRpcRequest : JsonRpcMessage
{
	[PropertyShape(Name = "method")]
	public required string Method { get; init; }

	[PropertyShape(Ignore = true)]
	public JsonRpcValue Arguments { get; init; }

	/// <summary>Gets or sets the individual arguments within <see cref="Arguments"/>, if the transport split them while reading the message.</summary>
	/// <remarks>
	/// Locating each argument requires walking every token of the params, which costs a sizable fraction of deserializing them.
	/// A transport that must walk the params anyway to find where they end can record the arguments on that same pass.
	/// Whoever dispatches the request may return their pooled array once the arguments are consumed, clearing this property.
	/// </remarks>
	[PropertyShape(Ignore = true)]
	internal ArgumentList SplitArguments { get; set; }

	/// <summary>Gets or sets the <c>JoinableTask</c> token that correlates this request with its caller's context, if any.</summary>
	[PropertyShape(Ignore = true)]
	internal string? JoinableTaskToken
	{
		get => this.TryGetTopLevelProperty(TopLevelProperties.JoinableTaskTokenPropertyName, out TopLevelPropertyValue value) ? value.StringValue : null;
		set
		{
			if (value is null)
			{
				this.TopLevelProperties?.Remove(TopLevelProperties.JoinableTaskTokenPropertyName);
			}
			else
			{
				this.SetTopLevelProperty(TopLevelProperties.JoinableTaskTokenPropertyName, value);
			}
		}
	}
}
