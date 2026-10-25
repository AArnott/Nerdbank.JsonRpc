// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using Nerdbank.JsonRpc.BrokeredServiceContracts;
using Nerdbank.JsonRpc.BrokeredServices;
using Nerdbank.Streams;

public class BrokeredServiceDiagnosticsTests : TestBase
{
	[Test]
	[Arguments("NoRegistration", false)]
	[Arguments("NoProfferingFactory", false)]
	[Arguments("AudienceMismatch", false)]
	[Arguments("FactoryReturnedNull", false)]
	[Arguments("FactoryException", false)]
	[Arguments("Succeeded", false)]
	[Arguments("Canceled", false)]
	[Arguments("NoRegistration", true)]
	[Arguments("NoProfferingFactory", true)]
	[Arguments("AudienceMismatch", true)]
	[Arguments("FactoryReturnedNull", true)]
	[Arguments("FactoryException", true)]
	[Arguments("Succeeded", true)]
	[Arguments("Canceled", true)]
	public async Task ContainerLogsExactOutcomeAndException(string outcome, bool connection)
	{
		using CapturingLoggerFactory logs = new();
		using BrokeredServiceContainer container = new() { LoggerFactory = logs };
		IJsonRpcDescriptor<IBrokeredCalculator> descriptor = BrokeredTestServices.Calculator;
		if (outcome != "NoRegistration")
		{
			container.Register(new(descriptor.Moniker, audience: outcome == "AudienceMismatch" ? ServiceAudience.Process : ServiceAudience.Local));
		}

		InvalidOperationException failure = new("Factory details", new ArgumentException("Inner details"));
		using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(this.TimeoutToken);
		using IDisposable? proffer = outcome is "FactoryReturnedNull" or "FactoryException" or "Succeeded" or "Canceled"
			? container.Proffer<IBrokeredCalculator>(descriptor, (_, _, _, _) =>
			{
				if (outcome == "FactoryException")
				{
					throw failure;
				}

				if (outcome == "Canceled")
				{
					cancellation.Cancel();
					cancellation.Token.ThrowIfCancellationRequested();
				}

				return new(outcome == "Succeeded" ? new Calculator() : null);
			})
			: null;
		IServiceBrokerWithMetadata broker = (IServiceBrokerWithMetadata)container.GetServiceBroker();
		async Task AcquireAsync()
		{
			if (connection)
			{
				await using BrokeredServiceConnection? acquired = await broker.GetConnectionAsync(descriptor.Moniker, cancellationToken: cancellation.Token);
				Assert.Equal(outcome == "Succeeded", acquired is not null);
				return;
			}

			IBrokeredCalculator? proxy = await broker.GetProxyAsync(descriptor, cancellationToken: cancellation.Token);
			await using System.IAsyncDisposable? lifetime = (System.IAsyncDisposable?)proxy;
			Assert.Equal(outcome == "Succeeded", proxy is not null);
		}

		Activity? previous = Activity.Current;
		if (outcome == "FactoryException")
		{
			Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(AcquireAsync));
		}
		else if (outcome == "Canceled")
		{
			await Assert.ThrowsAnyAsync<OperationCanceledException>(AcquireAsync);
		}
		else
		{
			await AcquireAsync();
		}

		Assert.Same(previous, Activity.Current);
		LogEntry[] entries = logs.Entries.ToArray();
		string operation = connection ? "GetConnection" : "GetProxy";
		LogEntry start = Assert.Single(entries, entry => Equals(entry.Values["Operation"], operation) && entry.EventId == 1000);
		LogEntry end = Assert.Single(entries, entry => Equals(entry.Values["Operation"], operation) && entry.EventId != 1000);
		Assert.Equal(outcome == "FactoryException" ? "ActivationFailed" : outcome, end.Values["Outcome"]);
		Assert.Equal(start.Values["TraceId"], end.Values["TraceId"]);
		Assert.Equal(start.Values["SpanId"], end.Values["SpanId"]);
		Assert.Equal(32, Assert.IsType<string>(start.Values["TraceId"]).Length);
		Assert.Equal(16, Assert.IsType<string>(start.Values["SpanId"]).Length);
		if (outcome == "FactoryException")
		{
			LogEntry activation = Assert.Single(entries, entry => entry.Values.TryGetValue("Outcome", out object? value) && Equals(value, "FactoryException"));
			Assert.Same(failure, activation.Exception);
			Assert.Contains("Inner details", activation.Exception!.ToString());
			Assert.Equal(LogLevel.Error, activation.Level);
		}

		Assert.All(entries.Where(entry => entry.EventId == 1000), started =>
			Assert.Single(entries, finished => finished.EventId is 1001 or 1002 && Equals(started.Values["SpanId"], finished.Values["SpanId"])));
	}

	[Test]
	[Arguments("RegistrationDiscoveryFailed")]
	[Arguments("FactoryLoadingFailed")]
	[Arguments("OfferingDiscoveryFailed")]
	[Arguments("RegistrationChanged")]
	public async Task CapabilityQueriesLogDiscoveryFailures(string outcome)
	{
		using CapturingLoggerFactory logs = new();
		using FailingDiscoveryContainer container = new(outcome) { LoggerFactory = logs };
		ServiceMoniker moniker = BrokeredTestServices.Calculator.Moniker;
		if (outcome != "RegistrationDiscoveryFailed")
		{
			container.Register(new(moniker, (_, host) =>
			{
				if (outcome == "FactoryLoadingFailed")
				{
					throw container.Failure;
				}

				if (outcome == "RegistrationChanged")
				{
					Assert.True(host.Unregister(moniker));
				}

				return default;
			}));
		}

		IServiceBrokerWithMetadata broker = (IServiceBrokerWithMetadata)container.GetServiceBroker();
		if (outcome == "RegistrationChanged")
		{
			Assert.Null(await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken));
		}
		else
		{
			InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
				async () => await broker.GetCapabilitiesAsync(moniker, this.TimeoutToken));
			Assert.Same(container.Failure, failure);
		}

		LogEntry terminal = Assert.Single(logs.Entries, entry => entry.EventId != 1000);
		Assert.Equal(outcome, terminal.Values["Outcome"]);
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task DeferredActivationRetainsAcquisitionCorrelation(bool throwException)
	{
		using CapturingLoggerFactory logs = new();
		using BrokeredServiceContainer container = new() { LoggerFactory = logs };
		JsonRpcDescriptor<IBrokeredCalculator> descriptor = BrokeredTestServices.Descriptor with
		{
			CallbackInterface = BrokeredTestServices.Descriptor.CreateInterface<IBrokeredCalculator>("diagnostics.callback"),
		};
		container.Register(new(descriptor.Moniker));
		InvalidOperationException failure = new("Deferred factory details");
		using IDisposable proffer = container.Proffer<IBrokeredCalculator>(descriptor, (_, _, _, _) =>
			throwException ? throw failure : new((IBrokeredCalculator?)null));
		IServiceBrokerWithMetadata broker = (IServiceBrokerWithMetadata)container.GetServiceBroker();
		await using BrokeredServiceConnection? connection = await broker.GetConnectionAsync(descriptor.Moniker, cancellationToken: this.TimeoutToken);
		Assert.NotNull(connection);
		LogEntry activation = await logs.ActivationCompleted.Task.WithCancellation(this.TimeoutToken);
		LogEntry acquisition = Assert.Single(logs.Entries, entry => entry.EventId == 1000 && Equals(entry.Values["Operation"], "GetConnection"));
		Assert.Equal(acquisition.Values["TraceId"], activation.Values["TraceId"]);
		Assert.Equal(throwException ? "FactoryException" : "FactoryReturnedNull", activation.Values["Outcome"]);
		Assert.Equal(throwException ? failure : null, activation.Exception);
	}

	[Test]
	[Arguments(JsonRpcEncoding.Json, false, false)]
	[Arguments(JsonRpcEncoding.MessagePack, false, false)]
	[Arguments(JsonRpcEncoding.Json, true, false)]
	[Arguments(JsonRpcEncoding.MessagePack, true, false)]
	[Arguments(JsonRpcEncoding.Json, false, true)]
	[Arguments(JsonRpcEncoding.MessagePack, false, true)]
	[Arguments(JsonRpcEncoding.Json, true, true)]
	[Arguments(JsonRpcEncoding.MessagePack, true, true)]
	public async Task RemoteBrokerLogsJoinClientAndHostTraces(JsonRpcEncoding encoding, bool sampled, bool multiplexed)
	{
		ConcurrentQueue<Activity> activities = new();
		string sourceName = JsonRpc.ActivitySource.Name;
		using ActivityListener listener = new()
		{
			ShouldListenTo = source => source.Name == sourceName,
			Sample = (ref ActivityCreationOptions<ActivityContext> _) => sampled ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
			ActivityStopped = activities.Enqueue,
		};
		ActivitySource.AddActivityListener(listener);
		using CapturingLoggerFactory hostLogs = new();
		using CapturingLoggerFactory clientLogs = new();
		(IDuplexPipe clientTransport, IDuplexPipe serverTransport) = FullDuplexStream.CreatePipePair();
		Task<MultiplexingStream> clientMuxTask = MultiplexingStream.CreateAsync(clientTransport.AsStream());
		Task<MultiplexingStream> serverMuxTask = MultiplexingStream.CreateAsync(serverTransport.AsStream());
		await Task.WhenAll(clientMuxTask, serverMuxTask);
		using MultiplexingStream clientMux = await clientMuxTask;
		using MultiplexingStream serverMux = await serverMuxTask;
		using BrokeredServiceContainer container = new() { LoggerFactory = hostLogs };
		container.Register(new(BrokeredTestServices.Calculator.Moniker));
		using IDisposable proffer = container.Proffer(BrokeredTestServices.Calculator, () => new Calculator());
		await using IpcRelayServiceBroker relay = new((IServiceBrokerWithMetadata)container.GetServiceBroker()) { Logger = hostLogs.CreateLogger<IpcRelayServiceBroker>() };
		using MultiplexingRelayServiceBroker muxRelay = new((IServiceBrokerWithMetadata)container.GetServiceBroker(), serverMux) { Logger = hostLogs.CreateLogger<MultiplexingRelayServiceBroker>() };
		(IDuplexPipe client, IDuplexPipe server) = FullDuplexStream.CreatePipePair();
		JsonRpcDescriptor<IRemoteServiceBroker> control = encoding == JsonRpcEncoding.Json
			? JsonRpcUtf8Descriptor.Create<IRemoteServiceBroker>(FrameworkServices.RemoteServiceBroker.Moniker)
			: FrameworkServices.RemoteServiceBroker;
		using JsonRpc serverRpc = control.CreateConnection(server);
		control.AddRpcTarget(serverRpc, multiplexed ? muxRelay : relay, []);
		serverRpc.Start();
		using RemoteServiceBroker broker = multiplexed
			? await RemoteServiceBroker.ConnectAsync(client, clientMux, control, new() { LoggerFactory = clientLogs }, this.TimeoutToken)
			: await RemoteServiceBroker.ConnectAsync(client, control, new() { LoggerFactory = clientLogs }, this.TimeoutToken);
		using Activity parent = new Activity("broker test").SetIdFormat(ActivityIdFormat.W3C).Start();
		parent.TraceStateString = "vendor=broker-test";
		IBrokeredCalculator proxy = (await broker.GetProxyAsync(BrokeredTestServices.Calculator, cancellationToken: this.TimeoutToken))!;
		await using System.IAsyncDisposable lifetime = (System.IAsyncDisposable)proxy;
		Assert.Equal(7, await proxy.AddAsync(3, 4, this.TimeoutToken));
		Assert.Null(await broker.GetCapabilitiesAsync(new("not-registered"), this.TimeoutToken));

		LogEntry clientStart = Assert.Single(clientLogs.Entries, entry => entry.EventId == 1000 && Equals(entry.Values["Operation"], "RemoteGetProxy"));
		LogEntry hostStart = Assert.Single(hostLogs.Entries, entry => entry.EventId == 1000 && Equals(entry.Values["Operation"], "GetConnection"));
		Assert.Equal(parent.TraceId.ToString(), clientStart.Values["TraceId"]);
		Assert.Equal(clientStart.Values["TraceId"], hostStart.Values["TraceId"]);
		Assert.NotEqual(clientStart.Values["SpanId"], hostStart.Values["SpanId"]);
		Assert.Contains(hostLogs.Entries, entry => entry.Values.TryGetValue("Outcome", out object? value) && Equals(value, "NoRegistration"));
		Assert.Contains(clientLogs.Entries, entry => entry.Values.TryGetValue("Outcome", out object? value) && Equals(value, "RemoteUnavailable"));
		Assert.All(hostLogs.Entries, entry => Assert.Equal(parent.TraceId.ToString(), entry.Values["TraceId"]));
		Assert.All(hostLogs.Entries, entry => Assert.Equal("vendor=broker-test", entry.Values["AmbientTraceState"]));
		if (sampled)
		{
			Activity hostActivity = Assert.Single(activities, activity => activity.SpanId.ToString() == (string)hostStart.Values["SpanId"]!);
			Assert.Equal("vendor=broker-test", hostActivity.TraceStateString);
			Activity relayActivity = Assert.Single(activities, activity => activity.SpanId == hostActivity.ParentSpanId);
			Activity serverActivity = Assert.Single(activities, activity => activity.SpanId == relayActivity.ParentSpanId);
			Activity rpcClient = Assert.Single(activities, activity => activity.SpanId == serverActivity.ParentSpanId);
			Assert.Equal((string)clientStart.Values["SpanId"]!, rpcClient.ParentSpanId.ToString());
		}
	}

	private sealed record LogEntry(int EventId, LogLevel Level, Dictionary<string, object?> Values, Exception? Exception);

	private sealed class FailingDiscoveryContainer(string outcome) : BrokeredServiceContainer
	{
		internal InvalidOperationException Failure { get; } = new("Catalog details");

		protected override ValueTask OnMissingServiceRegistrationAsync(ServiceMoniker moniker, CancellationToken cancellationToken)
			=> outcome == "RegistrationDiscoveryFailed" ? throw this.Failure : default;

		protected override ValueTask<ServiceOffering?> TryGetServiceOfferingAsync(ServiceMoniker moniker, CancellationToken cancellationToken)
			=> throw this.Failure;
	}

	private sealed class Calculator : IBrokeredCalculator
	{
		public event EventHandler<int>? Changed;

		public ValueTask<int> AddAsync(int left, int right, CancellationToken cancellationToken = default)
		{
			this.Changed?.Invoke(this, left + right);
			return new(left + right);
		}

		public Task FailAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
	}

	private sealed class CapturingLoggerFactory : ILoggerFactory
	{
		internal ConcurrentQueue<LogEntry> Entries { get; } = new();

		internal TaskCompletionSource<LogEntry> ActivationCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

		public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();

		public void Dispose()
		{
		}

		private sealed class CapturingLogger(CapturingLoggerFactory owner) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state)
				where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => true;

			public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
			{
				if (eventId.Id is >= 1000 and <= 1002)
				{
					Dictionary<string, object?> values = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state).ToDictionary(pair => pair.Key, pair => pair.Value);
					Assert.Equal(Activity.Current?.TraceId.ToString(), values["TraceId"]);
					Assert.Equal(Activity.Current?.SpanId.ToString(), values["SpanId"]);
					values["AmbientTraceState"] = Activity.Current?.TraceStateString;
					LogEntry entry = new(eventId.Id, logLevel, values, exception);
					owner.Entries.Enqueue(entry);
					if (eventId.Id != 1000 && Equals(values["Operation"], "Activate"))
					{
						owner.ActivationCompleted.TrySetResult(entry);
					}
				}
			}
		}
	}
}
