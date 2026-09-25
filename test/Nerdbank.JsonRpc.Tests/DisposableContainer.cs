// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

[GenerateShape]
internal partial class DisposableContainer
{
	public required IDisposable Value { get; init; }
}
