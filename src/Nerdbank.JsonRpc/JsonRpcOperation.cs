// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Nerdbank.JsonRpc;

/// <summary>Snapshots one RPC operation's correlation and records a single terminal outcome.</summary>
internal struct JsonRpcOperation
{
	private static long nextId;

	private readonly JsonRpc? owner;
	private readonly JsonRpcRequest? request;
	private readonly Activity? activity;
	private readonly Activity? correlationActivity;
	private readonly string? role;
	private readonly long started;
	private int completed;

	private JsonRpcOperation(JsonRpc owner, JsonRpcRequest request, Activity? activity, string role)
	{
		this.owner = owner;
		this.request = request;
		this.activity = activity;
		this.role = role;
		this.Id = Interlocked.Increment(ref nextId);
		this.started = Stopwatch.GetTimestamp();
		this.correlationActivity = activity ?? Activity.Current;
		this.completed = 0;
		activity?.SetTag("rpc.message_kind", JsonRpcDiagnostics.GetMessageKind(request));
		activity?.SetTag("rpc.request_id", request.Id?.ToString());
		activity?.SetTag("rpc.request_id_kind", JsonRpcDiagnostics.GetRequestIdKind(request));
		activity?.SetTag("rpc.connection_id", owner.Channel.DiagnosticConnectionId);
		this.Log(new EventId(100, "RpcOperationStarted"), "Started", errorCode: null, exception: null);
	}

	/// <summary>Gets the process-local operation identifier without formatting it.</summary>
	internal readonly long Id { get; }

	/// <summary>Gets a value indicating whether operation diagnostics are enabled.</summary>
	internal readonly bool IsEnabled => this.owner is not null;

	/// <summary>Starts diagnostics when logging or activity collection is enabled.</summary>
	/// <param name="owner">The connection.</param>
	/// <param name="request">The operation envelope.</param>
	/// <param name="activity">The operation's activity, if any.</param>
	/// <param name="role">Client or Server.</param>
	/// <returns>The diagnostics, or a no-op value when disabled.</returns>
	internal static JsonRpcOperation Start(JsonRpc owner, JsonRpcRequest request, Activity? activity, string role)
		=> activity is not null || owner.Logger.IsEnabled(LogLevel.Information) || owner.Logger.IsEnabled(LogLevel.Warning)
			? new(owner, request, activity, role)
			: default;

	/// <summary>Attaches operation metadata to nested logs within an asynchronous call boundary.</summary>
	/// <returns>The logging scope.</returns>
	internal readonly IDisposable? BeginScope()
	{
		if (this.owner is null || !this.owner.Logger.IsEnabled(LogLevel.Information))
		{
			return null;
		}

		return this.owner.Logger.BeginScope(new Dictionary<string, object?>(9)
		{
			["ConnectionId"] = this.owner.Channel.DiagnosticConnectionId,
			["OperationId"] = this.Id.ToString(CultureInfo.InvariantCulture),
			["Role"] = this.role,
			["Method"] = this.request!.Method,
			["MessageKind"] = JsonRpcDiagnostics.GetMessageKind(this.request),
			["RequestId"] = this.request.Id?.ToString(),
			["RequestIdKind"] = JsonRpcDiagnostics.GetRequestIdKind(this.request),
			["TraceId"] = this.correlationActivity?.TraceId.ToString(),
			["SpanId"] = this.correlationActivity?.SpanId.ToString(),
		});
	}

	/// <summary>Records protocol completion or local notification submission.</summary>
	/// <param name="response">The response, if applicable.</param>
	/// <param name="isError">Whether dispatch failed without an error envelope.</param>
	/// <param name="errorCode">The dispatch error code when no response envelope exists.</param>
	internal void Complete(JsonRpcResponse? response = null, bool isError = false, JsonRpcErrorCode? errorCode = null)
	{
		if (this.owner is null)
		{
			return;
		}

		long? code = (long?)((response as JsonRpcError)?.Error.Code ?? errorCode);
		string outcome;
		if (code == (long)JsonRpcErrorCode.RequestCancelled)
		{
			outcome = "Canceled";
		}
		else if (code.HasValue)
		{
			outcome = this.role == "Client" ? "RemoteError" : "HandlerError";
		}
		else if (isError)
		{
			outcome = "HandlerError";
		}
		else
		{
			outcome = this.role == "Client" && !this.request!.HasId ? "Submitted" : "Succeeded";
		}

		this.Finish(outcome, code, exception: null);
	}

	/// <summary>Records a failed operation without treating cancellation as an error.</summary>
	/// <param name="exception">The failure.</param>
	/// <param name="timedOut">Whether the library's outbound deadline expired.</param>
	internal void Fail(Exception exception, bool timedOut = false)
	{
		if (this.owner is null)
		{
			return;
		}

		string outcome;
		if (timedOut)
		{
			outcome = "TimedOut";
		}
		else if (exception is OperationCanceledException)
		{
			outcome = "Canceled";
		}
		else
		{
			outcome = this.owner.IsDisposed ? "ConnectionError" : "Exception";
		}

		this.Finish(outcome, errorCode: null, exception);
	}

	private void Finish(string outcome, long? errorCode, Exception? exception)
	{
		if (Interlocked.Exchange(ref this.completed, 1) != 0)
		{
			return;
		}

		this.activity?.SetTag("rpc.outcome", outcome);
		this.activity?.SetTag("rpc.error_code", errorCode);
		if (outcome is not ("Succeeded" or "Submitted" or "Canceled"))
		{
			this.activity?.SetStatus(ActivityStatusCode.Error);
			this.activity?.SetTag("error.type", exception?.GetType().FullName ?? outcome);
		}

		this.Log(new EventId(101, "RpcOperationCompleted"), outcome, errorCode, exception);
	}

	private readonly void Log(EventId eventId, string outcome, long? errorCode, Exception? exception)
	{
		LogLevel level = outcome is "Started" or "Succeeded" or "Submitted" or "Canceled" ? LogLevel.Information : LogLevel.Warning;
		if (this.owner is null || !this.owner.Logger.IsEnabled(level))
		{
			return;
		}

		JsonRpcRequest request = this.request!;
		this.owner.Logger.Log(
			level,
			eventId,
			exception,
			"JSON-RPC {Role} {MessageKind} {Method}: {Outcome}; id: {RequestId}; id kind: {RequestIdKind}; error code: {ErrorCode}; elapsed: {ElapsedMilliseconds}ms; connection: {ConnectionId}; operation: {OperationId}; trace: {TraceId}; span: {SpanId}.",
			this.role,
			JsonRpcDiagnostics.GetMessageKind(request),
			request.Method,
			outcome,
			request.Id?.ToString(),
			JsonRpcDiagnostics.GetRequestIdKind(request),
			errorCode,
			(Stopwatch.GetTimestamp() - this.started) * 1000.0 / Stopwatch.Frequency,
			this.owner.Channel.DiagnosticConnectionId,
			this.Id.ToString(CultureInfo.InvariantCulture),
			this.correlationActivity?.TraceId.ToString(),
			this.correlationActivity?.SpanId.ToString());
	}
}
