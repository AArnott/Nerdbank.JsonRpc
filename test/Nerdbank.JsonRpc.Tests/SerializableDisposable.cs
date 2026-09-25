// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using PolyType;

internal class SerializableDisposable : IDisposable
{
	public int Number { get; init; }

	[PropertyShape(Ignore = true)]
	public bool IsDisposed { get; private set; }

	public void Dispose() => this.IsDisposed = true;
}
