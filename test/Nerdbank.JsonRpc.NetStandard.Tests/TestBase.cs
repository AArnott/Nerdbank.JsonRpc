// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

global using Nerdbank.JsonRpc;
global using TUnit.Core;
global using Xunit;

public abstract class TestBase
{
	private readonly CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current!.Execution.CancellationToken);

	protected TestBase() => this.timeout.CancelAfter(TimeSpan.FromSeconds(10));

	public CancellationToken TimeoutToken => this.timeout.Token;

	[After(Test)]
	public void Cleanup() => this.timeout.Dispose();
}
