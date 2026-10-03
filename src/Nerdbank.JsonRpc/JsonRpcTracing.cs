// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;

namespace Nerdbank.JsonRpc;

/// <summary>Creates and propagates distributed trace activities for JSON-RPC calls.</summary>
internal static class JsonRpcTracing
{
	/// <summary>Gets the ActivitySource used for this library's RPC activities.</summary>
	internal static ActivitySource ActivitySource { get; } = new("Nerdbank.JsonRpc");

	/// <summary>Starts an activity for an outbound RPC operation.</summary>
	/// <param name="method">The remote method name.</param>
	/// <returns>The activity, or <see langword="null"/> when no listener samples it.</returns>
	internal static Activity? StartClientActivity(string method) => StartActivity(method, ActivityKind.Client);

	/// <summary>Adds the ambient W3C trace context to each request in a message.</summary>
	/// <param name="message">The message to update.</param>
	internal static void ApplyTraceContext(JsonRpcMessage message)
	{
		if (message is JsonRpcMessageBatch batch)
		{
			foreach (JsonRpcMessage entry in batch.Messages)
			{
				ApplyTraceContext(entry);
			}

			return;
		}

		if (message is not JsonRpcRequest request || request.TryGetTopLevelProperty(TopLevelProperties.TraceParentPropertyName, out _))
		{
			return;
		}

		Activity? activity = Activity.Current;
		if (activity?.IdFormat == ActivityIdFormat.W3C && activity.Id is string traceParent)
		{
			request.SetTopLevelProperty(TopLevelProperties.TraceParentPropertyName, traceParent);
			if (activity.TraceStateString is string traceState)
			{
				request.SetTopLevelProperty(TopLevelProperties.TraceStatePropertyName, traceState);
			}
		}
	}

	/// <summary>Starts a server activity with the request's propagated W3C context as its parent.</summary>
	/// <param name="request">The request being dispatched.</param>
	/// <returns>The activity, or <see langword="null"/> when no listener samples it.</returns>
	internal static Activity? StartServerActivity(JsonRpcRequest request)
	{
		ActivityContext parentContext = default;
		if (request.TryGetTopLevelProperty(TopLevelProperties.TraceParentPropertyName, out TopLevelPropertyValue traceParent) &&
			traceParent.StringValue is string traceParentValue)
		{
			string? traceState = request.TryGetTopLevelProperty(TopLevelProperties.TraceStatePropertyName, out TopLevelPropertyValue traceStateValue)
				? traceStateValue.StringValue
				: null;
			ActivityContext.TryParse(traceParentValue, traceState, isRemote: true, out parentContext);
		}

		return StartActivity(request.Method, ActivityKind.Server, parentContext);
	}

	private static Activity? StartActivity(string method, ActivityKind kind, ActivityContext parentContext = default)
	{
		if (!ActivitySource.HasListeners())
		{
			return null;
		}

		Activity? activity = kind == ActivityKind.Server
			? ActivitySource.StartActivity($"JSON-RPC {method}", kind, parentContext)
			: ActivitySource.StartActivity($"JSON-RPC {method}", kind);
		activity?.SetTag("rpc.system", "jsonrpc");
		activity?.SetTag("rpc.method", method);
		return activity;
	}
}
