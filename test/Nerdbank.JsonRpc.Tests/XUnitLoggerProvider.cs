// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging;
using Xunit;

namespace Nerdbank.JsonRpc.Tests;

[ProviderAlias("XUnit")]
public class XUnitLoggerProvider(ITestOutputHelper outputHelper) : ILoggerProvider
{
	public ILogger CreateLogger(string categoryName) => new XUnitLogger(outputHelper, categoryName);

	public void Dispose()
	{
	}

	private class XUnitLogger(ITestOutputHelper output, string category) : ILogger
	{
		public LogLevel Level { get; set; } = LogLevel.Debug;

		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => logLevel >= this.Level;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (!this.IsEnabled(logLevel))
			{
				return;
			}

			var message = formatter(state, exception);
			var line = $"[{logLevel}] {category} {eventId}: {message}";
			if (exception != null)
			{
				line += Environment.NewLine + exception;
			}

			try
			{
				output.WriteLine(line);
			}
			catch (InvalidOperationException)
			{
				/* test already finished - swallow */
			}
		}
	}
}
