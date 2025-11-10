// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using Xunit;

public abstract class TestBase : IDisposable
{
	private readonly CancellationTokenSource timeoutSource = new(UnexpectedTimeout);

	private readonly CancellationTokenSource timeoutJoinedSource;

	public TestBase()
	{
		this.Logger = TestContext.Current.TestOutputHelper;
		this.timeoutSource.Token.Register(() =>
		{
			this.Logger?.WriteLine($"The test has exceeded the unexpected timeout of {UnexpectedTimeout.TotalSeconds} seconds.");
		});

		this.timeoutJoinedSource = CancellationTokenSource.CreateLinkedTokenSource(this.timeoutSource.Token, TestContext.Current.CancellationToken);
	}

	public static TimeSpan UnexpectedTimeout => Debugger.IsAttached ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(5);

	public CancellationToken TimeoutToken => this.timeoutJoinedSource.Token;

	public ITestOutputHelper? Logger { get; }

	public virtual void Dispose()
	{
		this.timeoutSource.Dispose();
		this.timeoutJoinedSource.Dispose();
	}
}
