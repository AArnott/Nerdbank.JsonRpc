// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

/// <summary>Typed arguments used to exercise the direct JSON client overload.</summary>
[GenerateShape]
internal partial struct JsonDirectArgs
{
	[PropertyShape(Name = "a")]
	public int A { get; set; }

	[PropertyShape(Name = "b")]
	public int B { get; set; }
}
