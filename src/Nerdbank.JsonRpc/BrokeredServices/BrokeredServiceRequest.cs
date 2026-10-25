// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Correlates one broker operation's structured logs and distributed trace.</summary>
internal sealed class BrokeredServiceRequest : IDisposable
{
	private readonly ILogger logger;
	private readonly ServiceMoniker moniker;
	private readonly string operation;
	private readonly Activity activity;
	private readonly IDisposable? scope;
	private readonly Stopwatch elapsed = Stopwatch.StartNew();

	/// <summary>Initializes a new instance of the <see cref="BrokeredServiceRequest"/> class.</summary>
	/// <param name="logger">The operation logger.</param>
	/// <param name="moniker">The requested service.</param>
	/// <param name="operation">The acquisition stage.</param>
	/// <param name="kind">The trace span kind.</param>
	internal BrokeredServiceRequest(ILogger logger, ServiceMoniker moniker, string operation, ActivityKind kind = ActivityKind.Internal)
	{
		this.logger = logger;
		this.moniker = moniker ?? throw new ArgumentNullException(nameof(moniker));
		this.operation = operation;
		string name = $"Brokered service {operation}";
		this.activity = JsonRpc.ActivitySource.CreateActivity(name, kind)?.SetIdFormat(ActivityIdFormat.W3C).Start()
			?? new Activity(name).SetIdFormat(ActivityIdFormat.W3C).Start();
		this.activity.SetTag("rpc.system", "nerdbank.brokeredservices");
		this.activity.SetTag("rpc.service", moniker.Name);
		this.activity.SetTag("brokeredservice.version", moniker.Version?.ToString());
		this.activity.SetTag("brokeredservice.operation", operation);
		this.scope = logger.BeginScope(new Dictionary<string, object?>
		{
			["ServiceMoniker"] = moniker.ToString(),
			["Operation"] = operation,
			["TraceId"] = this.activity.TraceId.ToString(),
			["SpanId"] = this.activity.SpanId.ToString(),
			["ParentSpanId"] = this.activity.ParentSpanId.ToString(),
		});
		logger.LogInformation(
			new EventId(1000, "ServiceRequestStarted"),
			"Service request started: {ServiceMoniker}, {Operation}, TraceId={TraceId}, SpanId={SpanId}, ParentSpanId={ParentSpanId}.",
			moniker,
			operation,
			this.activity.TraceId.ToString(),
			this.activity.SpanId.ToString(),
			this.activity.ParentSpanId.ToString());
	}

	/// <summary>Gets or sets the reason an acquisition cannot return a service.</summary>
	internal string RejectionReason { get; set; } = "Unavailable";

	/// <inheritdoc/>
	public void Dispose()
	{
		this.scope?.Dispose();
		this.activity.Dispose();
	}

	/// <summary>Records the operation result, including an explicit rejection reason.</summary>
	/// <param name="available">Whether the operation succeeded.</param>
	internal void Complete(bool available)
	{
		string outcome = available ? "Succeeded" : this.RejectionReason;
		this.activity.SetTag("brokeredservice.outcome", outcome);
		this.logger.LogInformation(
			new EventId(1001, "ServiceRequestCompleted"),
			"Service request completed: {ServiceMoniker}, {Operation}, {Outcome}, {ElapsedMilliseconds}ms, TraceId={TraceId}, SpanId={SpanId}.",
			this.moniker,
			this.operation,
			outcome,
			this.elapsed.Elapsed.TotalMilliseconds,
			this.activity.TraceId.ToString(),
			this.activity.SpanId.ToString());
	}

	/// <summary>Records failure with the full exception, distinguishing request cancellation.</summary>
	/// <param name="exception">The operation failure.</param>
	/// <param name="cancellationToken">The request cancellation token.</param>
	internal void Fail(Exception exception, CancellationToken cancellationToken)
	{
		bool canceled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
		string outcome = canceled ? "Canceled" : this.RejectionReason == "Unavailable" ? "Exception" : this.RejectionReason;
		this.activity.SetTag("brokeredservice.outcome", outcome);
		if (!canceled)
		{
			this.activity.SetStatus(ActivityStatusCode.Error, exception.Message);
			this.activity.SetTag("error.type", exception.GetType().FullName);
		}

		this.logger.Log(
			canceled ? LogLevel.Information : LogLevel.Error,
			new EventId(1002, "ServiceRequestFailed"),
			exception,
			"Service request failed: {ServiceMoniker}, {Operation}, {Outcome}, {ElapsedMilliseconds}ms, TraceId={TraceId}, SpanId={SpanId}.",
			this.moniker,
			this.operation,
			outcome,
			this.elapsed.Elapsed.TotalMilliseconds,
			this.activity.TraceId.ToString(),
			this.activity.SpanId.ToString());
	}
}
