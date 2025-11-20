// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

public abstract class TestBase : IDisposable
{
	protected static readonly RawMessagePack NilMsgPack = WriteNil();

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

	public static TimeSpan ExpectedTimeout => TimeSpan.FromMilliseconds(100);

	public static ILoggerFactory LoggerFactory { get; } = Microsoft.Extensions.Logging.LoggerFactory.Create(
		builder =>
		{
			if (TestContext.Current.TestOutputHelper is { } helper)
			{
				builder.AddProvider(new XUnitLoggerProvider(helper));
			}

			builder.SetMinimumLevel(LogLevel.Trace);
		});

	public CancellationToken TimeoutToken => this.timeoutJoinedSource.Token;

	public ITestOutputHelper? Logger { get; }

	public void Log(JsonRpcMessage message, JsonRpc jsonRpc)
		=> this.Logger?.WriteLine(jsonRpc.Serializer.ConvertToJson(jsonRpc.Serializer.Serialize(message, TestContext.Current.CancellationToken)));

	public virtual void Dispose()
	{
		this.timeoutSource.Dispose();
		this.timeoutJoinedSource.Dispose();
	}

	private static RawMessagePack WriteNil()
	{
		byte[] msgpack = new byte[1];
		MessagePackPrimitives.TryWriteNil(msgpack, out _);
		return (RawMessagePack)msgpack;
	}
}
