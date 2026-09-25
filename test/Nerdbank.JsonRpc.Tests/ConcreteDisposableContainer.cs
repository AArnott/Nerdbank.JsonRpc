// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

[GenerateShape]
internal partial class ConcreteDisposableContainer
{
	public required SerializableDisposable Value { get; init; }
}
