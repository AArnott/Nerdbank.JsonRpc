// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

/// <summary>A JSON-RPC test contract whose methods collide under the default method name transform (<c>FooAsync</c> and <c>Foo</c> both transform to <c>foo</c>).</summary>
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ICollidingNamesTarget
{
	Task FooAsync(CancellationToken cancellationToken);

	Task Foo(CancellationToken cancellationToken);
}
