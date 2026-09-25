// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

internal sealed class FooTarget : IFooTarget
{
	public Task Foo(CancellationToken cancellationToken) => Task.CompletedTask;
}
