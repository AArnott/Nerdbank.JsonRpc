// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.VisualStudio.Threading;
using Nerdbank.Streams;

/// <summary>Tests distributed activity tracing over the supported wire encodings.</summary>
public partial class JsonRpcTracingTests : TestBase
{
	[Test]
	[Arguments(JsonRpcEncoding.Json)]
	[Arguments(JsonRpcEncoding.MessagePack)]
	public async Task ActivitiesPropagateW3CTraceContext(JsonRpcEncoding encoding)
	{
		ConcurrentQueue<Activity> stoppedActivities = new();
		TaskCompletionSource<bool> firstNotificationActivityStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<bool> secondNotificationActivityStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int notificationServerActivityCount = 0;
		string sourceName = JsonRpc.ActivitySource.Name;
		string? expectedTraceId = null;
		using ActivityListener listener = new()
		{
			ShouldListenTo = source => source.Name == sourceName,
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
			ActivityStopped = activity =>
			{
				if (activity.TraceId.ToString() == expectedTraceId)
				{
					stoppedActivities.Enqueue(activity);
					if (activity.Kind == ActivityKind.Server && Equals(activity.GetTagItem("rpc.method"), "notify"))
					{
						switch (Interlocked.Increment(ref notificationServerActivityCount))
						{
							case 1:
								firstNotificationActivityStopped.TrySetResult(true);
								break;
							case 2:
								secondNotificationActivityStopped.TrySetResult(true);
								break;
						}
					}
				}
			},
		};
		ActivitySource.AddActivityListener(listener);

		(IDuplexPipe clientPipe, IDuplexPipe serverPipe) = FullDuplexStream.CreatePipePair();
		await using JsonRpcPipeChannel clientChannel = CreateChannel(clientPipe, encoding);
		await using JsonRpcPipeChannel serverChannel = CreateChannel(serverPipe, encoding);
		using JsonRpc clientRpc = new(clientChannel);
		using JsonRpc serverRpc = new(serverChannel);
		serverRpc.AddRpcTarget<IEchoService>(new EchoService());
		serverRpc.Start();
		clientRpc.Start();
		IEchoService client = clientRpc.Attach<IEchoService>();

		Activity parent = new Activity("test parent").SetIdFormat(ActivityIdFormat.W3C);
		parent.TraceStateString = "vendor=value";
		parent.Start();
		expectedTraceId = parent.TraceId.ToString();
		Assert.Equal("direct", await client.EchoAsync("direct", this.TimeoutToken));
		await Assert.ThrowsAsync<JsonRpcException>(() => client.EchoAsync("fail", this.TimeoutToken));

		using JsonRpcBatch batch = clientRpc.CreateBatch();
		Task<string> batchedCall = batch.Attach<IEchoService>().EchoAsync("batch", this.TimeoutToken);
		await batch.SendAsync(this.TimeoutToken);
		Assert.Equal("batch", await batchedCall.WithCancellation(this.TimeoutToken));
		client.Notify("notify", this.TimeoutToken);
		await firstNotificationActivityStopped.Task.WithCancellation(this.TimeoutToken);
		client.Notify("fail-notification", this.TimeoutToken);
		await secondNotificationActivityStopped.Task.WithCancellation(this.TimeoutToken);
		parent.Stop();

		Activity[] activities = stoppedActivities.ToArray();
		Assert.Equal(10, activities.Length);
		Activity[] clientActivities = activities.Where(activity => activity.Kind == ActivityKind.Client).ToArray();
		Activity[] serverActivities = activities.Where(activity => activity.Kind == ActivityKind.Server).ToArray();
		Assert.Equal(5, clientActivities.Length);
		Assert.Equal(5, serverActivities.Length);
		foreach (Activity activity in clientActivities)
		{
			Assert.Equal(parent.TraceId, activity.TraceId);
			Assert.Equal(parent.SpanId, activity.ParentSpanId);
			Assert.Equal("jsonrpc", activity.GetTagItem("rpc.system"));
			Assert.NotNull(activity.GetTagItem("rpc.method"));
		}

		foreach (Activity activity in serverActivities)
		{
			Activity clientActivity = Assert.Single(clientActivities, client => client.TraceId == activity.TraceId && client.SpanId == activity.ParentSpanId);
			Assert.Equal("vendor=value", activity.TraceStateString);
			Assert.Equal(clientActivity.GetTagItem("rpc.method"), activity.GetTagItem("rpc.method"));
		}

		Assert.Equal(1, clientActivities.Count(activity => activity.Status == ActivityStatusCode.Error));
		Assert.Equal(2, serverActivities.Count(activity => activity.Status == ActivityStatusCode.Error));
	}

	private static JsonRpcPipeChannel CreateChannel(IDuplexPipe pipe, JsonRpcEncoding encoding) => encoding switch
	{
		JsonRpcEncoding.Json => new JsonRpcJsonChannel(pipe, new Nerdbank.Json.JsonSerializer(), JsonRpcJsonFraming.NewlineDelimited),
		_ => new JsonRpcMessagePackChannel(pipe),
	};

	private sealed class EchoService : IEchoService
	{
		public Task<string> EchoAsync(string value, CancellationToken cancellationToken) => value == "fail"
			? Task.FromException<string>(new InvalidOperationException("Expected test failure."))
			: Task.FromResult(value);

		public void Notify(string value, CancellationToken cancellationToken)
		{
			if (value == "fail-notification")
			{
				throw new InvalidOperationException("Expected notification failure.");
			}
		}

		public Task<int> DoubleAsync(int value, CancellationToken cancellationToken) => Task.FromResult(value * 2);
	}
}
