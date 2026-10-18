// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

/// <summary>
/// The subset of a marshaled object's wire marker that tests need to drive the protocol directly.
/// </summary>
/// <remarks>
/// Serialized as named arguments, this also forms a valid <c>$/releaseMarshaledObject</c> payload.
/// </remarks>
[GenerateShape]
internal partial class MarshaledObjectMarker
{
	[PropertyShape(Name = "handle")]
	public long Handle { get; set; }
}
