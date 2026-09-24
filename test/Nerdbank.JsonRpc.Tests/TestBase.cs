// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

public abstract class TestBase
{
	protected static readonly RawMessagePack NilMsgPack = WriteNil();

	protected static readonly RawMessagePack EmptyParamsMsgPack = WriteEmptyArray();

	private readonly CancellationTokenSource timeoutSource = new(UnexpectedTimeout);

	private readonly CancellationTokenSource timeoutJoinedSource;

	public TestBase()
	{
		this.Logger = Console.WriteLine;
		this.timeoutSource.Token.Register(() =>
		{
			this.Logger?.Invoke($"The test has exceeded the unexpected timeout of {UnexpectedTimeout.TotalSeconds} seconds.");
		});

		this.timeoutJoinedSource = CancellationTokenSource.CreateLinkedTokenSource(this.timeoutSource.Token, TestContext.Current?.Execution.CancellationToken ?? default);
	}

	public static TimeSpan UnexpectedTimeout => Debugger.IsAttached ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(5);

	public static TimeSpan ExpectedTimeout => TimeSpan.FromMilliseconds(100);

	public static ILoggerFactory LoggerFactory { get; } = Microsoft.Extensions.Logging.LoggerFactory.Create(
		builder =>
		{
			builder.AddProvider(new TestLoggerProvider());
			builder.SetMinimumLevel(LogLevel.Trace);
		});

	public CancellationToken TimeoutToken => this.timeoutJoinedSource.Token;

	protected Action<string>? Logger { get; }

	public void Log(JsonRpcMessage message, JsonRpc jsonRpc)
	{
		if (this.Logger is not { } logger)
		{
			return;
		}

		string description = message switch
		{
			JsonRpcRequest { Arguments.HasValue: true } request => $"{request.Method}: {((MessagePackSerializerPlugin)((IJsonRpcClient)jsonRpc).Serializer).Serializer.ConvertToJson(request.Arguments.AsMessagePack())}",
			JsonRpcRequest request => request.Method,
			_ => message.GetType().Name,
		};
		logger(description);
	}

	[After(Test)]
	public void Cleanup()
	{
		this.timeoutSource.Dispose();
		this.timeoutJoinedSource.Dispose();
	}

	private static RawMessagePack WriteEmptyArray()
	{
		byte[] msgpack = new byte[1];
		MessagePackPrimitives.TryWriteArrayHeader(msgpack, 0, out _);
		return (RawMessagePack)msgpack;
	}

	private static RawMessagePack WriteNil()
	{
		byte[] msgpack = new byte[1];
		MessagePackPrimitives.TryWriteNil(msgpack, out _);
		return (RawMessagePack)msgpack;
	}
}
