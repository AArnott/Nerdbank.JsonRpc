// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Benchmarks;

/// <summary>Consumes log formatting and scopes without console or file I/O.</summary>
/// <param name="minimum">The lowest enabled log level.</param>
internal sealed class RpcBenchmarkLogger(LogLevel minimum) : ILogger
{
	private readonly LoggerExternalScopeProvider scopes = new();
	private long formattedCharacters;

	/// <inheritdoc/>
	public IDisposable? BeginScope<TState>(TState state)
		where TState : notnull => this.scopes.Push(state);

	/// <inheritdoc/>
	public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

	/// <inheritdoc/>
	public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
	{
		if (this.IsEnabled(logLevel))
		{
			Interlocked.Add(ref this.formattedCharacters, formatter(state, exception).Length);
		}
	}

	/// <summary>Creates the logger for a benchmark configuration.</summary>
	/// <param name="mode">The enabled logging level.</param>
	/// <returns>The benchmark logger.</returns>
	internal static ILogger Create(RpcLoggingMode mode) => mode switch
	{
		RpcLoggingMode.Disabled => NullLogger.Instance,
		RpcLoggingMode.Warning => new RpcBenchmarkLogger(LogLevel.Warning),
		RpcLoggingMode.Debug => new RpcBenchmarkLogger(LogLevel.Debug),
		_ => throw new ArgumentOutOfRangeException(nameof(mode)),
	};
}
