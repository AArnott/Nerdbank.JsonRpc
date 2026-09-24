// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging;

namespace Nerdbank.JsonRpc.Tests;

internal sealed class TestLoggerProvider : ILoggerProvider
{
	public ILogger CreateLogger(string categoryName) => new TestLogger(categoryName);

	public void Dispose()
	{
	}

	private sealed class TestLogger(string category) : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (!this.IsEnabled(logLevel))
			{
				return;
			}

			string line = $"[{logLevel}] {category} {eventId}: {formatter(state, exception)}";
			if (exception is not null)
			{
				line += Environment.NewLine + exception;
			}

			Console.WriteLine(line);
		}
	}
}
