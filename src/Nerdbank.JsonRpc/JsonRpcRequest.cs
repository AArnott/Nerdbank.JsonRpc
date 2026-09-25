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
