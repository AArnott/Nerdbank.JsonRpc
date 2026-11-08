// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;
using PolyType;

public partial class JsonRpcDiagnosticsTests : TestBase
{
	[Test]
	[Arguments(JsonRpcEncoding.Json, false)]
	[Arguments(JsonRpcEncoding.Json, true)]
	[Arguments(JsonRpcEncoding.MessagePack, false)]
	[Arguments(JsonRpcEncoding.MessagePack, true)]
	public async Task CallsExposeStructuredMetadataAndOutcomes(JsonRpcEncoding encoding, bool batched)
	{
		using Fixture fixture = new(encoding);
		using Activity parent = new Activity("diagnostic test").SetIdFormat(ActivityIdFormat.W3C).Start();
		using JsonRpcBatch batch = fixture.Client.CreateBatch();
		IEchoService proxy = batched ? batch.Attach<IEchoService>() : fixture.Client.Attach<IEchoService>();
		Task<string> successful = proxy.EchoAsync("private-payload", this.TimeoutToken);
		Task<string> failed = proxy.EchoAsync("fail", this.TimeoutToken);
		proxy.Notify("notify", this.TimeoutToken);
		if (batched)
		{
			await batch.SendAsync(this.TimeoutToken);
		}

		Assert.Equal("private-payload", await successful);
		await Assert.ThrowsAsync<RemoteInvocationException>(() => failed);
		await fixture.ClientLogs.WaitForAsync(entries => entries.Count(entry => entry.Id == 101) == 3, this.TimeoutToken);
		await fixture.ServerLogs.WaitForAsync(entries => entries.Count(entry => entry.Id == 101) == 3, this.TimeoutToken);
		await fixture.ClientLogs.WaitForAsync(entries => entries.Any(entry => entry.Id == 2 && Equals(entry.Values["MessageKind"], "Error")), this.TimeoutToken);

		foreach (RecordingLogger logger in new[] { fixture.ClientLogs, fixture.ServerLogs })
		{
			Entry[] entries = logger.Entries.ToArray();
			Entry[] starts = entries.Where(entry => entry.Id == 100).ToArray();
			Assert.Equal(3, starts.Length);
			foreach (Entry start in starts)
			{
				Entry end = Assert.Single(entries, entry => entry.Id == 101 && Equals(entry.Values["OperationId"], start.Values["OperationId"]));
				Assert.Equal(start.Values["ConnectionId"], end.Values["ConnectionId"]);
				Assert.Equal(start.Values["Method"], end.Values["Method"]);
				Assert.Equal(start.Values["RequestId"], end.Values["RequestId"]);
				Assert.True(Assert.IsType<double>(end.Values["ElapsedMilliseconds"]) >= 0);
				Assert.Equal(parent.TraceId.ToString(), end.Values["TraceId"]);
			}

			Assert.All(entries.Where(entry => entry.Id is 1 or 2 or 100 or 101), entry => Assert.DoesNotContain("private-payload", entry.Text));
			Assert.DoesNotContain(entries.SelectMany(entry => entry.Values.Keys), key => key is "Arguments" or "Result" or "ErrorMessage" or "ErrorData");
		}

		Entry[] clientEnds = fixture.ClientLogs.Entries.Where(entry => entry.Id == 101).ToArray();
		Assert.Contains(clientEnds, entry => Equals(entry.Values["Outcome"], "Succeeded"));
		Entry remoteError = Assert.Single(clientEnds, entry => Equals(entry.Values["Outcome"], "RemoteError"));
		Assert.Equal((long)JsonRpcErrorCode.InternalError, remoteError.Values["ErrorCode"]);
		Entry notification = Assert.Single(clientEnds, entry => Equals(entry.Values["MessageKind"], "Notification"));
		Assert.Equal("Submitted", notification.Values["Outcome"]);
		Assert.Null(notification.Values["RequestId"]);
		Assert.Contains(fixture.ServerLogs.Entries, entry => entry.Id == 101 && Equals(entry.Values["Outcome"], "HandlerError"));
		Assert.Contains(fixture.ServerLogs.Entries, entry => entry.Exception is InvalidOperationException && Equals(entry.Values["Method"], "echo") && entry.Values.ContainsKey("OperationId"));

		Entry sent = Assert.Single(fixture.ClientLogs.Entries, entry => entry.Id == 1 && Equals(entry.Values["MessageKind"], "Request") && Equals(entry.Values["RequestId"], remoteError.Values["RequestId"]));
		Entry response = Assert.Single(fixture.ClientLogs.Entries, entry => entry.Id == 2 && Equals(entry.Values["MessageKind"], "Error"));
		Assert.Equal(sent.Values["ConnectionId"], response.Values["ConnectionId"]);
		Assert.Equal(sent.Values["RequestId"], response.Values["RequestId"]);
		Assert.Equal("Number", response.Values["RequestIdKind"]);
		Assert.Equal(parent.TraceId.ToString(), sent.Values["TraceId"]);
		Assert.NotEqual(remoteError.Values["ConnectionId"], fixture.ServerLogs.Entries.First(entry => entry.Id == 100).Values["ConnectionId"]);
		if (batched)
		{
			Entry envelope = Assert.Single(fixture.ClientLogs.Entries, entry => entry.Id == 1 && Equals(entry.Values["MessageKind"], "Batch"));
			Assert.Equal(3, envelope.Values["BatchCount"]);
			Assert.Equal(envelope.Values["BatchId"], sent.Values["BatchId"]);
		}
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task MissingMethodsAndFailedNotificationsHaveTerminalEvents(JsonRpcEncoding encoding)
	{
		using Fixture fixture = new(encoding);
		await Assert.ThrowsAsync<RemoteInvocationException>(() => fixture.Client.RequestAsync("missing", default(JsonRpcValue), this.TimeoutToken).AsTask());
		fixture.Client.Attach<IEchoService>().Notify("fail", this.TimeoutToken);
		await fixture.ServerLogs.WaitForAsync(entries => entries.Count(entry => entry.Id == 101) == 2, this.TimeoutToken);
		Entry missing = Assert.Single(fixture.ServerLogs.Entries, entry => entry.Id == 101 && Equals(entry.Values["Method"], "missing"));
		Assert.Equal((long)JsonRpcErrorCode.MethodNotFound, missing.Values["ErrorCode"]);
		Assert.Equal("HandlerError", missing.Values["Outcome"]);
		Entry failedNotification = Assert.Single(fixture.ServerLogs.Entries, entry => entry.Id == 101 && Equals(entry.Values["Method"], "notify"));
		Assert.Equal("HandlerError", failedNotification.Values["Outcome"]);
		Assert.Equal((long)JsonRpcErrorCode.InternalError, failedNotification.Values["ErrorCode"]);
		Assert.Contains(fixture.ClientLogs.Entries, entry => entry.Id == 101 && Equals(entry.Values["Method"], "notify") && Equals(entry.Values["Outcome"], "Submitted"));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json, false)]
	[Arguments(JsonRpcEncoding.Json, true)]
	[Arguments(JsonRpcEncoding.MessagePack, false)]
	[Arguments(JsonRpcEncoding.MessagePack, true)]
	public async Task ApplicationErrorCodesArePreservedForRequestsAndNotifications(JsonRpcEncoding encoding, bool notification)
	{
		using Fixture fixture = new(encoding, LogLevel.Warning);
		if (notification)
		{
			fixture.Client.Attach<IEchoService>().Notify("custom", this.TimeoutToken);
		}
		else
		{
			RemoteInvocationException exception = await Assert.ThrowsAsync<RemoteInvocationException>(() => fixture.Client.Attach<IEchoService>().EchoAsync("custom", this.TimeoutToken));
			Assert.Equal((JsonRpcErrorCode)12345, exception.ErrorDetails.Code);
		}

		await fixture.ServerLogs.WaitForAsync(entries => entries.Any(entry => entry.Id == 101), this.TimeoutToken);
		Entry failure = Assert.Single(fixture.ServerLogs.Entries, entry => entry.Id == 101);
		Assert.Equal("HandlerError", failure.Values["Outcome"]);
		Assert.Equal(12345L, failure.Values["ErrorCode"]);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json, "onNext")]
	[Arguments(JsonRpcEncoding.Json, "onError")]
	[Arguments(JsonRpcEncoding.Json, "onCompleted")]
	[Arguments(JsonRpcEncoding.MessagePack, "onNext")]
	[Arguments(JsonRpcEncoding.MessagePack, "onError")]
	[Arguments(JsonRpcEncoding.MessagePack, "onCompleted")]
	public async Task ObserverCallbackFailuresHaveCorrelatedTerminalEvents(JsonRpcEncoding encoding, string method)
	{
		using Fixture fixture = new(encoding, LogLevel.Warning);
		ObserverService service = new();
		fixture.Server.AddRpcTarget<IObserverService>(service);
		InvalidOperationException exception = new("Expected observer failure");
		using IDisposable subscription = await fixture.Client.Attach<IObserverService>().SubscribeAsync(new ThrowingObserver(exception), this.TimeoutToken);
		IObserver<int> observer = Assert.IsAssignableFrom<IObserver<int>>(service.Subscriber);
		switch (method)
		{
			case "onNext":
				observer.OnNext(1);
				break;
			case "onError":
				observer.OnError(new Exception("remote error"));
				break;
			case "onCompleted":
				observer.OnCompleted();
				break;
		}

		await fixture.ClientLogs.WaitForAsync(entries => entries.Any(entry => entry.Id == 101 && entry.Values["Method"] is string name && name.EndsWith("/" + method, StringComparison.Ordinal)), this.TimeoutToken);
		Entry failure = Assert.Single(fixture.ClientLogs.Entries, entry => entry.Id == 101);
		Assert.Equal("HandlerError", failure.Values["Outcome"]);
		Assert.Equal((long)JsonRpcErrorCode.InternalError, failure.Values["ErrorCode"]);
		Assert.Contains(fixture.ClientLogs.Entries, entry => ReferenceEquals(entry.Exception, exception) && Equals(entry.Values["OperationId"], failure.Values["OperationId"]));
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json, false)]
	[Arguments(JsonRpcEncoding.Json, true)]
	[Arguments(JsonRpcEncoding.MessagePack, false)]
	[Arguments(JsonRpcEncoding.MessagePack, true)]
	public async Task EnumeratorAbortFailuresAreLoggedWithoutChangingAcknowledgment(JsonRpcEncoding encoding, bool notification)
	{
		using Fixture fixture = new(encoding);
		if (notification)
		{
			await fixture.Client.NotifyAsync("$/enumerator/abort", default(JsonRpcValue), this.TimeoutToken);
		}
		else
		{
			await fixture.Client.RequestAsync("$/enumerator/abort", default(JsonRpcValue), this.TimeoutToken);
			Assert.Equal("Succeeded", Assert.Single(fixture.ClientLogs.Entries, entry => entry.Id == 101).Values["Outcome"]);
		}

		await fixture.ServerLogs.WaitForAsync(entries => entries.Any(entry => entry.Id == 101), this.TimeoutToken);
		Entry failure = Assert.Single(fixture.ServerLogs.Entries, entry => entry.Id == 101);
		Assert.Equal("HandlerError", failure.Values["Outcome"]);
		Assert.Equal((long)JsonRpcErrorCode.InternalError, failure.Values["ErrorCode"]);
		Assert.Contains(fixture.ServerLogs.Entries, entry => entry.Exception is InvalidOperationException && Equals(entry.Values["OperationId"], failure.Values["OperationId"]));
	}

	[Test]
	[Arguments(false, false)]
	[Arguments(false, true)]
	[Arguments(true, false)]
	[Arguments(true, true)]
	public async Task CancellationAndTimeoutAreDistinct(bool batched, bool timeout)
	{
		using Activity parent = new Activity("cancellation diagnostics").SetIdFormat(ActivityIdFormat.W3C).Start();
		TaskCompletionSource<Activity> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
		string sourceName = JsonRpc.ActivitySource.Name;
		using ActivityListener listener = new()
		{
			ShouldListenTo = source => source.Name == sourceName,
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
			ActivityStopped = activity =>
			{
				if (activity.TraceId == parent.TraceId && activity.Kind == ActivityKind.Client && Equals(activity.GetTagItem("rpc.method"), "pending"))
				{
					stopped.TrySetResult(activity);
				}
			},
		};
		ActivitySource.AddActivityListener(listener);
		(Channel<JsonRpcMessage> peer, Channel<JsonRpcMessage> local) = MockChannel<JsonRpcMessage>.CreatePair();
		RecordingLogger logger = new();
		using JsonRpc rpc = new(new MockJsonRpcPipeChannel(local), new JsonRpcOptions { OutboundRequestTimeout = timeout ? TimeSpan.FromSeconds(1) : Timeout.InfiniteTimeSpan }) { Logger = logger };
		rpc.Start();
		using CancellationTokenSource cancellation = new();
		using JsonRpcBatch batch = rpc.CreateBatch();
		Task request = (batched ? batch.RequestAsync("pending", EmptyParamsMsgPack, cancellation.Token) : rpc.RequestAsync("pending", EmptyParamsMsgPack, cancellation.Token)).AsTask();
		if (batched)
		{
			await batch.SendAsync(this.TimeoutToken);
		}

		JsonRpcMessage message = await peer.Reader.ReadAsync(this.TimeoutToken);
		RequestId id = (message is JsonRpcMessageBatch sentBatch ? sentBatch.Messages[0] : message).Id!.Value;
		if (timeout)
		{
			await Assert.ThrowsAsync<TimeoutException>(() => request);
		}
		else
		{
			cancellation.Cancel();
			await peer.Writer.WriteAsync(new JsonRpcError { Id = id, Error = new() { Code = JsonRpcErrorCode.RequestCancelled, Message = "Canceled" } }, this.TimeoutToken);
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
		}

		Entry end = Assert.Single(logger.Entries, entry => entry.Id == 101 && Equals(entry.Values["Method"], "pending"));
		Assert.Equal(timeout ? "TimedOut" : "Canceled", end.Values["Outcome"]);
		Activity activity = await stopped.Task.WithCancellation(this.TimeoutToken);
		Assert.Equal(end.Values["Outcome"], activity.GetTagItem("rpc.outcome"));
		Assert.Equal(timeout ? ActivityStatusCode.Error : ActivityStatusCode.Unset, activity.Status);
	}

	[Test]
	[Arguments("Dispose")]
	[Arguments("PreCanceled")]
	[Arguments("Canceled")]
	public void UnsentBatchedNotificationIsCanceledExactlyOnce(string mode)
	{
		using Fixture fixture = new(JsonRpcEncoding.MessagePack);
		using CancellationTokenSource cancellation = new();
		if (mode == "PreCanceled")
		{
			cancellation.Cancel();
		}

		JsonRpcBatch batch = fixture.Client.CreateBatch();
		if (mode == "PreCanceled")
		{
			Assert.True(batch.NotifyAsync("notify", default(JsonRpcValue), cancellation.Token).IsCompletedSuccessfully);
		}
		else
		{
			batch.Attach<IEchoService>().Notify("private-payload", cancellation.Token);
		}

		if (mode == "Canceled")
		{
			cancellation.Cancel();
		}

		batch.Dispose();
		batch.Dispose();
		Entry end = Assert.Single(fixture.ClientLogs.Entries, entry => entry.Id == 101);
		Assert.Equal("Canceled", end.Values["Outcome"]);
		Assert.DoesNotContain(fixture.ClientLogs.Entries, entry => entry.Id == 1);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json, false)]
	[Arguments(JsonRpcEncoding.Json, true)]
	[Arguments(JsonRpcEncoding.MessagePack, false)]
	[Arguments(JsonRpcEncoding.MessagePack, true)]
	public void DisposingUnsentBatchPreservesCurrentActivity(JsonRpcEncoding encoding, bool noCurrentActivity)
	{
		string sourceName = JsonRpc.ActivitySource.Name;
		using Activity parent = new Activity("notification parent").SetIdFormat(ActivityIdFormat.W3C).Start();
		ConcurrentQueue<Activity> stopped = new();
		using ActivityListener listener = new()
		{
			ShouldListenTo = source => source.Name == sourceName,
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
			ActivityStopped = activity =>
			{
				if (activity.TraceId == parent.TraceId && activity.Kind == ActivityKind.Client)
				{
					stopped.Enqueue(activity);
				}
			},
		};
		ActivitySource.AddActivityListener(listener);
		using Fixture fixture = new(encoding);
		using JsonRpcBatch batch = fixture.Client.CreateBatch();
		batch.Attach<IEchoService>().Notify("notification", this.TimeoutToken);
		Assert.Same(parent, Activity.Current);
		using Activity? completionActivity = noCurrentActivity ? null : new Activity("batch disposal").SetIdFormat(ActivityIdFormat.W3C).Start();
		if (noCurrentActivity)
		{
			Activity.Current = null;
		}

		try
		{
			batch.Dispose();
			batch.Dispose();
			Assert.Same(completionActivity, Activity.Current);
			Assert.Equal("Canceled", Assert.Single(stopped).GetTagItem("rpc.outcome"));
			Assert.Single(fixture.ClientLogs.Entries, entry => entry.Id == 101);
		}
		finally
		{
			Activity.Current = parent;
		}
	}

	[Test]
	public async Task ResultConversionFailureIsSeparateFromProtocolSuccess()
	{
		(Channel<JsonRpcMessage> peer, Channel<JsonRpcMessage> local) = MockChannel<JsonRpcMessage>.CreatePair();
		RecordingLogger logger = new();
		using JsonRpc rpc = new(new MockJsonRpcPipeChannel(local)) { Logger = logger };
		rpc.Start();
		Task<int> result = rpc.Attach<IEchoService>().DoubleAsync(1, this.TimeoutToken);
		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await peer.Reader.ReadAsync(this.TimeoutToken));
		await peer.Writer.WriteAsync(new JsonRpcResult { Id = request.Id!.Value, Result = (RawMessagePack)((MessagePackSerializerPlugin)((IJsonRpcClient)rpc).Serializer).Serializer.Serialize<string, Witness>("not-an-integer", this.TimeoutToken) }, this.TimeoutToken);
		await Assert.ThrowsAnyAsync<Exception>(() => result);
		Assert.Equal("Succeeded", Assert.Single(logger.Entries, entry => entry.Id == 101).Values["Outcome"]);
		Entry conversion = Assert.Single(logger.Entries, entry => entry.Id == 102);
		Assert.NotNull(conversion.Exception);
		Assert.Equal("double", conversion.Values["Method"]);
	}

	[Test]
	public async Task TransportLoggingDistinguishesIdTokenKinds()
	{
		(Channel<JsonRpcMessage> peer, Channel<JsonRpcMessage> local) = MockChannel<JsonRpcMessage>.CreatePair();
		RecordingLogger logger = new();
		using JsonRpc rpc = new(new MockJsonRpcPipeChannel(local)) { Logger = logger };
		rpc.Start();
		foreach (RequestId id in new RequestId[] { new(1L), new("1"), default })
		{
			await peer.Writer.WriteAsync(new JsonRpcRequest { Id = id, Method = "missing" }, this.TimeoutToken);
		}

		await logger.WaitForAsync(entries => entries.Count(entry => entry.Id == 2) == 3, this.TimeoutToken);
		Assert.Equal(new[] { "Number", "String", "Null" }, logger.Entries.Where(entry => entry.Id == 2).Select(entry => entry.Values["RequestIdKind"]));
		Assert.All(logger.Entries.Where(entry => entry.Id == 2), entry => Assert.Equal("Request", entry.Values["MessageKind"]));
	}

	[Test]
	public async Task InformationLoggingDoesNotEmitTransportTraffic()
	{
		using Fixture fixture = new(JsonRpcEncoding.Json, LogLevel.Information);
		Assert.Equal("hello", await fixture.Client.Attach<IEchoService>().EchoAsync("hello", this.TimeoutToken));
		Assert.DoesNotContain(fixture.ClientLogs.Entries, entry => entry.Id is 1 or 2);
		Assert.Single(fixture.ClientLogs.Entries, entry => entry.Id == 101);
	}

	[Test]
	public async Task WarningOnlyLoggingRetainsMethodAndFailureCorrelation()
	{
		using Fixture fixture = new(JsonRpcEncoding.MessagePack, LogLevel.Warning);
		await Assert.ThrowsAsync<RemoteInvocationException>(() => fixture.Client.Attach<IEchoService>().EchoAsync("fail", this.TimeoutToken));
		Assert.DoesNotContain(fixture.ClientLogs.Entries, entry => entry.Id is 1 or 2 or 100);
		Entry failure = Assert.Single(fixture.ClientLogs.Entries, entry => entry.Id == 101);
		Assert.Equal("echo", failure.Values["Method"]);
		Assert.Equal("RemoteError", failure.Values["Outcome"]);
		Assert.Contains(fixture.ServerLogs.Entries, entry => entry.Exception is InvalidOperationException && Equals(entry.Values["Method"], "echo") && entry.Values.ContainsKey("OperationId"));
		Entry serverFailure = Assert.Single(fixture.ServerLogs.Entries, entry => entry.Id == 101);
		Assert.Contains(fixture.ServerLogs.Entries, entry => entry.Exception is InvalidOperationException && Equals(entry.Values["OperationId"], serverFailure.Values["OperationId"]));
		Assert.Equal(0, fixture.ClientLogs.ScopeCount);
		Assert.Equal(0, fixture.ServerLogs.ScopeCount);
	}

	[Test]
	public async Task WarningOnlySuccessfulCallsDoNotCreateScopes()
	{
		using Fixture fixture = new(JsonRpcEncoding.MessagePack, LogLevel.Warning);
		using JsonRpcBatch batch = fixture.Client.CreateBatch();
		Assert.Equal("direct", await fixture.Client.Attach<IEchoService>().EchoAsync("direct", this.TimeoutToken));
		Task<string> pending = batch.Attach<IEchoService>().EchoAsync("batch", this.TimeoutToken);
		batch.Attach<IEchoService>().Notify("notification", this.TimeoutToken);
		await batch.SendAsync(this.TimeoutToken);
		Assert.Equal("batch", await pending);
		Assert.Equal(0, fixture.ClientLogs.ScopeCount);
		Assert.Equal(0, fixture.ServerLogs.ScopeCount);
		Assert.Empty(fixture.ClientLogs.Entries);
		Assert.Empty(fixture.ServerLogs.Entries);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json, "missing", JsonRpcErrorCode.MethodNotFound)]
	[Arguments(JsonRpcEncoding.Json, "echo", JsonRpcErrorCode.InvalidParams)]
	[Arguments(JsonRpcEncoding.MessagePack, "missing", JsonRpcErrorCode.MethodNotFound)]
	[Arguments(JsonRpcEncoding.MessagePack, "echo", JsonRpcErrorCode.InvalidParams)]
	public async Task RejectedNotificationsExposeErrorCodeWithoutAResponse(JsonRpcEncoding encoding, string method, JsonRpcErrorCode code)
	{
		using Fixture fixture = new(encoding);
		await fixture.Client.NotifyAsync(method, default(JsonRpcValue), this.TimeoutToken);
		await fixture.ServerLogs.WaitForAsync(entries => entries.Any(entry => entry.Id == 101), this.TimeoutToken);
		Entry failure = Assert.Single(fixture.ServerLogs.Entries, entry => entry.Id == 101);
		Assert.Equal((long)code, failure.Values["ErrorCode"]);
		Assert.Equal("HandlerError", failure.Values["Outcome"]);
		Assert.DoesNotContain(fixture.ServerLogs.Entries, entry => entry.Id == 1);
	}

	[Test]
	public async Task RemoteErrorsPreserveCustomCodesWithoutLoggingPeerErrorText()
	{
		(Channel<JsonRpcMessage> peer, Channel<JsonRpcMessage> local) = MockChannel<JsonRpcMessage>.CreatePair();
		RecordingLogger logger = new();
		using JsonRpc rpc = new(new MockJsonRpcPipeChannel(local)) { Logger = logger };
		rpc.Start();
		Task pending = rpc.RequestAsync("custom", EmptyParamsMsgPack, this.TimeoutToken).AsTask();
		JsonRpcRequest request = Assert.IsType<JsonRpcRequest>(await peer.Reader.ReadAsync(this.TimeoutToken));
		JsonRpcError error = new()
		{
			Id = request.Id!.Value,
			Error = new()
			{
				Code = (JsonRpcErrorCode)12345,
				Message = "private-peer-message",
				Data = (RawMessagePack)((MessagePackSerializerPlugin)((IJsonRpcClient)rpc).Serializer).Serializer.Serialize<string, Witness>("private-error-data", this.TimeoutToken),
			},
		};
		await peer.Writer.WriteAsync(error, this.TimeoutToken);
		await Assert.ThrowsAsync<RemoteInvocationException>(() => pending);
		Entry end = Assert.Single(logger.Entries, entry => entry.Id == 101);
		Assert.Equal(12345L, end.Values["ErrorCode"]);
		Assert.Equal("RemoteError", end.Values["Outcome"]);
		Assert.All(logger.Entries.Where(entry => entry.Id is 1 or 2 or 100 or 101), entry =>
		{
			Assert.DoesNotContain("private-peer-message", entry.Text);
			Assert.DoesNotContain("private-error-data", entry.Text);
			Assert.Null(entry.Exception);
		});
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task ConnectionLossCompletesPendingOperations(bool batched)
	{
		(Channel<JsonRpcMessage> peer, Channel<JsonRpcMessage> local) = MockChannel<JsonRpcMessage>.CreatePair();
		RecordingLogger logger = new();
		using JsonRpc rpc = new(new MockJsonRpcPipeChannel(local)) { Logger = logger };
		rpc.Start();
		using JsonRpcBatch batch = rpc.CreateBatch();
		Task pending = (batched ? batch.RequestAsync("pending", EmptyParamsMsgPack, this.TimeoutToken) : rpc.RequestAsync("pending", EmptyParamsMsgPack, this.TimeoutToken)).AsTask();
		if (batched)
		{
			await batch.SendAsync(this.TimeoutToken);
		}

		await peer.Reader.ReadAsync(this.TimeoutToken);
		peer.Writer.TryComplete();
		await Assert.ThrowsAsync<EndOfStreamException>(() => pending);
		Entry end = Assert.Single(logger.Entries, entry => entry.Id == 101 && Equals(entry.Values["Method"], "pending"));
		Assert.Equal("ConnectionError", end.Values["Outcome"]);
		Assert.IsType<EndOfStreamException>(end.Exception);
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task NotificationSubmissionFailureHasOneTerminalEvent(bool batched)
	{
		Channel<JsonRpcMessage> input = Channel.CreateUnbounded<JsonRpcMessage>();
		Channel<JsonRpcMessage> output = Channel.CreateUnbounded<JsonRpcMessage>();
		InvalidOperationException failure = new("Expected writer failure");
		output.Writer.TryComplete(failure);
		RecordingLogger logger = new();
		using JsonRpc rpc = new(new MockJsonRpcPipeChannel(new MockChannel<JsonRpcMessage>(input.Reader, output.Writer), writeDirectly: true)) { Logger = logger };
		rpc.Start();
		using JsonRpcBatch batch = rpc.CreateBatch();
		if (batched)
		{
			await batch.NotifyAsync("notify", EmptyParamsMsgPack, this.TimeoutToken);
		}

		InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(() => batched
			? batch.SendAsync(this.TimeoutToken).AsTask()
			: rpc.NotifyAsync("notify", EmptyParamsMsgPack, this.TimeoutToken).AsTask());
		Assert.Same(failure, actual);
		Assert.Equal("ConnectionError", Assert.Single(logger.Entries, entry => entry.Id == 101).Values["Outcome"]);
	}

	[Test]
	public async Task BidirectionalCallsWithSameIdHaveDistinctRoles()
	{
		using Fixture fixture = new(JsonRpcEncoding.Json);
		fixture.Client.AddRpcTarget<IEchoService>(new EchoService());
		Task<int> outbound = fixture.Client.Attach<IEchoService>().DoubleAsync(1, this.TimeoutToken);
		Task<int> inbound = fixture.Server.Attach<IEchoService>().DoubleAsync(2, this.TimeoutToken);
		Assert.Equal(2, await outbound);
		Assert.Equal(4, await inbound);
		await fixture.ClientLogs.WaitForAsync(entries => entries.Count(entry => entry.Id == 101) == 2, this.TimeoutToken);
		Entry[] ends = fixture.ClientLogs.Entries.Where(entry => entry.Id == 101).ToArray();
		Assert.Equal(ends[0].Values["ConnectionId"], ends[1].Values["ConnectionId"]);
		Assert.Equal(ends[0].Values["RequestId"], ends[1].Values["RequestId"]);
		Assert.NotEqual(ends[0].Values["Role"], ends[1].Values["Role"]);
		Assert.NotEqual(ends[0].Values["OperationId"], ends[1].Values["OperationId"]);
	}

	private sealed class Fixture : IDisposable
	{
		internal Fixture(JsonRpcEncoding encoding, LogLevel minimum = LogLevel.Debug)
		{
			this.ClientLogs = new(minimum);
			this.ServerLogs = new(minimum);
			(IDuplexPipe client, IDuplexPipe server) = FullDuplexStream.CreatePipePair();
			this.Client = new(CreateChannel(client, encoding)) { Logger = this.ClientLogs };
			this.Server = new(CreateChannel(server, encoding)) { Logger = this.ServerLogs };
			this.Server.AddRpcTarget<IEchoService>(new EchoService());
			this.Client.Start();
			this.Server.Start();
		}

		internal RecordingLogger ClientLogs { get; }

		internal RecordingLogger ServerLogs { get; }

		internal JsonRpc Client { get; }

		internal JsonRpc Server { get; }

		public void Dispose()
		{
			this.Client.Dispose();
			this.Server.Dispose();
		}

		private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding) => encoding == JsonRpcEncoding.Json
			? new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited)
			: new JsonRpcMessagePackChannel(pipe);
	}

	private sealed class EchoService : IEchoService
	{
		public Task<string> EchoAsync(string value, CancellationToken cancellationToken) => value switch
		{
			"fail" => Task.FromException<string>(new InvalidOperationException("Expected failure")),
			"custom" => Task.FromException<string>(new JsonRpcException(new() { Code = (JsonRpcErrorCode)12345, Message = "Expected custom failure" })),
			_ => Task.FromResult(value),
		};

		public void Notify(string value, CancellationToken cancellationToken)
		{
			if (value == "fail")
			{
				throw new InvalidOperationException("Expected notification failure");
			}

			if (value == "custom")
			{
				throw new JsonRpcException(new() { Code = (JsonRpcErrorCode)12345, Message = "Expected custom failure" });
			}
		}

		public Task<int> DoubleAsync(int value, CancellationToken cancellationToken) => Task.FromResult(value * 2);
	}

	private sealed class ThrowingObserver(Exception exception) : IObserver<int>
	{
		public void OnNext(int value) => throw exception;

		public void OnError(Exception error) => throw exception;

		public void OnCompleted() => throw exception;
	}

	private sealed class RecordingLogger(LogLevel minimum = LogLevel.Debug) : ILogger
	{
		private readonly SemaphoreSlim changed = new(0);
		private readonly LoggerExternalScopeProvider scopes = new();
		private int scopeCount;

		internal ConcurrentQueue<Entry> Entries { get; } = new();

		internal int ScopeCount => Volatile.Read(ref this.scopeCount);

		public IDisposable? BeginScope<TState>(TState state)
			where TState : notnull
		{
			Interlocked.Increment(ref this.scopeCount);
			return this.scopes.Push(state);
		}

		public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (!this.IsEnabled(logLevel))
			{
				return;
			}

			Dictionary<string, object?> values = new();
			this.scopes.ForEachScope((scope, dictionary) => Copy(scope, dictionary), values);
			Copy(state, values);
			this.Entries.Enqueue(new(eventId.Id, values, formatter(state, exception), exception));
			this.changed.Release();
		}

		internal async Task WaitForAsync(Func<Entry[], bool> predicate, CancellationToken cancellationToken)
		{
			while (!predicate(this.Entries.ToArray()))
			{
				await this.changed.WaitAsync(cancellationToken);
			}
		}

		private static void Copy(object? state, Dictionary<string, object?> values)
		{
			if (state is IEnumerable<KeyValuePair<string, object?>> properties)
			{
				foreach (KeyValuePair<string, object?> property in properties)
				{
					values[property.Key] = property.Value;
				}
			}
		}
	}

	private sealed record Entry(int Id, Dictionary<string, object?> Values, string Text, Exception? Exception);

	[GenerateShapeFor<string>]
	private partial class Witness;
}
