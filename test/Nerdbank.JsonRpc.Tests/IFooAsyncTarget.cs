// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IFooAsyncTarget
{
	Task FooAsync(CancellationToken cancellationToken);
}
