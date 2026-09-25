// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft;

internal class TestDisposable : IDisposableObservable
{
	public bool IsDisposed => this.DisposalCount > 0;

	public int DisposalCount { get; private set; }

	public void Dispose() => this.DisposalCount++;
}
