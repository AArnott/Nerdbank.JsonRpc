// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

internal sealed class SmokeLoggerFactory(bool writeToConsole = false) : ILoggerFactory
{
	private readonly bool writeToConsole = writeToConsole;

	internal ConcurrentQueue<string> Entries { get; } = new();

	public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

	public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

	public void Dispose()
	{
	}

	private sealed class Logger(SmokeLoggerFactory owner, string category) : ILogger
	{
		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			using MemoryStream stream = new();
			using (Utf8JsonWriter writer = new(stream))
			{
				writer.WriteStartObject();
				writer.WriteString("Category", category);
				writer.WriteNumber("EventId", eventId.Id);
				writer.WriteString("Level", logLevel.ToString());
				writer.WriteString("Exception", exception?.ToString());
				writer.WriteString("AmbientTraceId", Activity.Current?.TraceId.ToString());
				writer.WriteString("AmbientTraceState", Activity.Current?.TraceStateString);
				if (state is IEnumerable<KeyValuePair<string, object?>> fields)
				{
					foreach (KeyValuePair<string, object?> field in fields)
					{
						writer.WriteString(field.Key, field.Value?.ToString());
					}
				}

				writer.WriteEndObject();
			}

			string json = Encoding.UTF8.GetString(stream.ToArray());
			owner.Entries.Enqueue(json);
			if (owner.writeToConsole)
			{
				Console.WriteLine(json);
			}
		}
	}
}
