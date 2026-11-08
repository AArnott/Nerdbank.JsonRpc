// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Nerdbank.JsonRpc;

/// <summary>Emits payload-free structured protocol and operation diagnostics.</summary>
internal static class JsonRpcDiagnostics
{
	/// <summary>Gets the application error code using the same policy as error responses.</summary>
	/// <param name="exception">The application exception.</param>
	/// <param name="fallback">The code used when the exception does not supply local error details.</param>
	/// <returns>The diagnostic error code.</returns>
	internal static JsonRpcErrorCode GetApplicationErrorCode(Exception exception, JsonRpcErrorCode fallback)
		=> exception is JsonRpcException rpcException && exception is not RemoteInvocationException
			? rpcException.ErrorDetails.Code
			: fallback;

	/// <summary>Gets the semantic kind of an envelope.</summary>
	/// <param name="message">The envelope.</param>
	/// <returns>The diagnostic kind.</returns>
	internal static string GetMessageKind(JsonRpcMessage message) => message switch
	{
		JsonRpcRequest request => request.HasId ? "Request" : "Notification",
		JsonRpcResult => "Result",
		JsonRpcError => "Error",
		JsonRpcMessageBatch => "Batch",
		_ => "Invalid",
	};

	/// <summary>Snapshots transport metadata, including each entry in a batch.</summary>
	/// <param name="channel">The transport.</param>
	/// <param name="message">The envelope.</param>
	/// <param name="sent">Whether the envelope was flushed to output.</param>
	internal static void LogMessage(JsonRpcPipeChannel channel, JsonRpcMessage message, bool sent)
	{
		ILogger logger = channel.GetLogger();
		if (!logger.IsEnabled(LogLevel.Debug))
		{
			return;
		}

		LogEntry(channel, logger, message, sent, batchId: null);
	}

	/// <summary>Gets the ID's wire token kind without conflating numeric and string IDs.</summary>
	/// <param name="message">The envelope.</param>
	/// <returns>The token kind.</returns>
	internal static string GetRequestIdKind(JsonRpcMessage message) => message.Id is not RequestId id
		? "Absent"
		: id.IsNull ? "Null" : id.IsString ? "String" : "Number";

	private static void LogEntry(JsonRpcPipeChannel channel, ILogger logger, JsonRpcMessage entry, bool sent, string? batchId)
	{
		string? ownBatchId = entry is JsonRpcMessageBatch ? Guid.NewGuid().ToString("N") : batchId;
		ActivityContext context = default;
		if (entry.TryGetTopLevelProperty(TopLevelProperties.TraceParentPropertyName, out TopLevelPropertyValue traceParent))
		{
			ActivityContext.TryParse(traceParent.StringValue, null, out context);
		}

		logger.LogDebug(
			new EventId(sent ? 1 : 2, sent ? "Message sent" : "Message received"),
			"JSON-RPC {Direction} {MessageKind}: {Method}; id: {RequestId}; id kind: {RequestIdKind}; error code: {ErrorCode}; connection: {ConnectionId}; batch: {BatchId}; count: {BatchCount}; trace: {TraceId}; span: {SpanId}.",
			sent ? "Sent" : "Received",
			GetMessageKind(entry),
			(entry as JsonRpcRequest)?.Method,
			entry.Id?.ToString(),
			GetRequestIdKind(entry),
			(long?)(entry as JsonRpcError)?.Error.Code,
			channel.DiagnosticConnectionId,
			ownBatchId,
			(entry as JsonRpcMessageBatch)?.Messages.Length,
			context != default ? context.TraceId.ToString() : null,
			context != default ? context.SpanId.ToString() : null);
		if (entry is JsonRpcMessageBatch batch)
		{
			foreach (JsonRpcMessage child in batch.Messages)
			{
				LogEntry(channel, logger, child, sent, ownBatchId);
			}
		}
	}
}
