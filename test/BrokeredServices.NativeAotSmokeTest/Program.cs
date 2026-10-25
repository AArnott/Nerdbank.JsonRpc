// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nerdbank.JsonRpc;
using Nerdbank.JsonRpc.BrokeredServices;
using Nerdbank.Streams;

using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
CancellationToken cancellationToken = timeout.Token;
if (args.Length != 0)
{
	if (args is not ["--broker-host", string endpoint, string requestedEncoding])
	{
		throw new ArgumentException("Expected --broker-host <endpoint> <encoding>.");
	}

	JsonRpcEncoding encoding = Enum.Parse<JsonRpcEncoding>(requestedEncoding);
	using SmokeLoggerFactory hostLogs = new(writeToConsole: true);
	using BrokeredServiceContainer container = new() { LoggerFactory = hostLogs };
	JsonRpcDescriptor<IAotCalculator> descriptor = CreateDescriptor(encoding);
	container.Register(new(descriptor.Moniker, additionalInterfaceIds: descriptor.AdditionalInterfaces.Select(static contract => contract.Id).ToArray()));
	using IDisposable registration = container.Proffer(descriptor, (_, _, _, _) => new(new AotCalculator()));
	await using IpcRelayServiceBroker relay = new((IServiceBrokerWithMetadata)container.GetServiceBroker()) { Logger = hostLogs.CreateLogger<IpcRelayServiceBroker>() };
	JsonRpcDescriptor<IRemoteServiceBroker> control = CreateControlDescriptor(encoding);
	await using IIpcServer server = IpcServer.Create(
		async (stream, token) =>
		{
			using JsonRpc rpc = control.CreateConnection(stream.UsePipe(cancellationToken: token));
			control.AddRpcTarget(rpc, relay, []);
			rpc.Start();
			await rpc.Completion.WaitAsync(token);
		},
		new() { Name = endpoint });
	await server.Completion.WaitAsync(cancellationToken);
	return;
}

foreach (JsonRpcEncoding encoding in new[] { JsonRpcEncoding.Json, JsonRpcEncoding.MessagePack })
{
	JsonRpcDescriptor<IAotCalculator> descriptor = CreateDescriptor(encoding);
	IJsonRpcDescriptor<IAotCalculator> publicContract = descriptor;
	using (BrokeredServiceContainer container = new())
	{
		container.Register(new(descriptor.Moniker, additionalInterfaceIds: descriptor.AdditionalInterfaces.Select(static contract => contract.Id).ToArray()));
		using IDisposable registration = container.Proffer(descriptor, (_, _, _, _) => new(new AotCalculator()));
		IServiceBroker broker = container.GetServiceBroker();
		IAotCalculator proxy = await broker.GetProxyAsync(publicContract, cancellationToken: cancellationToken)
			?? throw new InvalidOperationException("The local service was unavailable.");
		await using IAsyncDisposable lifetime = (IAsyncDisposable)proxy;
		await ValidateAsync(proxy, cancellationToken);
		await using ResilientServiceProxy<IAotCalculator> resilient = await broker.GetResilientProxyAsync(publicContract, cancellationToken: cancellationToken);
		await ValidateAsync(resilient.Proxy, cancellationToken);
		using ServiceBrokerClient cache = new(broker);
		using ServiceBrokerClient.Rental<IAotCalculator> rental = await cache.GetProxyAsync(publicContract, cancellationToken: cancellationToken);
		await ValidateAsync(rental.Proxy ?? throw new InvalidOperationException("The cached service was unavailable."), cancellationToken);
	}

	string endpoint = Guid.NewGuid().ToString("N");
	ProcessStartInfo startInfo = new(Environment.ProcessPath ?? throw new InvalidOperationException("Run the apphost executable."))
	{
		UseShellExecute = false,
		RedirectStandardError = true,
		RedirectStandardOutput = true,
	};
	startInfo.ArgumentList.Add("--broker-host");
	startInfo.ArgumentList.Add(endpoint);
	startInfo.ArgumentList.Add(encoding.ToString());
	using Process child = Process.Start(startInfo) ?? throw new InvalidOperationException("The broker host did not start.");
	Task<string> errors = child.StandardError.ReadToEndAsync(cancellationToken);
	Task<string> hostOutput = child.StandardOutput.ReadToEndAsync(cancellationToken);
	using SmokeLoggerFactory clientLogs = new();
	using Activity trace = new Activity("Cross-process broker acquisition").SetIdFormat(ActivityIdFormat.W3C).Start();
	trace.TraceStateString = "smoke=broker";
	try
	{
		using (Stream stream = await IpcClient.ConnectAsync(endpoint, cancellationToken))
		using (RemoteServiceBroker broker = await RemoteServiceBroker.ConnectAsync(
			stream.UsePipe(), CreateControlDescriptor(encoding), new() { LoggerFactory = clientLogs }, cancellationToken))
		{
			IAotCalculator proxy = await broker.GetProxyAsync(publicContract, cancellationToken: cancellationToken)
				?? throw new InvalidOperationException("The remote service was unavailable.");
			await using IAsyncDisposable lifetime = (IAsyncDisposable)proxy;
			await ValidateAsync(proxy, cancellationToken);
			if (await broker.GetCapabilitiesAsync(new("aot.missing"), cancellationToken) is not null)
			{
				throw new InvalidOperationException("An unregistered service was reported as available.");
			}
		}

		await child.WaitForExitAsync(cancellationToken);
		if (child.ExitCode != 0)
		{
			throw new InvalidOperationException($"The broker host exited with {child.ExitCode}: {await errors}");
		}

		ValidateLogs(clientLogs.Entries, (await hostOutput).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries), trace.TraceId.ToString());
	}
	finally
	{
		try
		{
			if (!child.HasExited)
			{
				child.Kill(entireProcessTree: true);
				await child.WaitForExitAsync();
			}
		}
		catch (InvalidOperationException) when (child.HasExited)
		{
		}
	}
}

Console.WriteLine("Brokered service smoke tests passed: local, cached, resilient and cross-process JSON/MessagePack.");

static void ValidateLogs(IEnumerable<string> clientLogs, IEnumerable<string> hostLogs, string traceId)
{
	static Dictionary<string, string>[] Parse(IEnumerable<string> logs) => logs.Select(line =>
	{
		using JsonDocument document = JsonDocument.Parse(line);
		return document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.ToString());
	}).ToArray();

	Dictionary<string, string>[] client = Parse(clientLogs);
	Dictionary<string, string>[] host = Parse(hostLogs);
	Dictionary<string, string> clientRequest = client.Single(entry => entry["EventId"] == "1000" && entry["Operation"] == "RemoteGetProxy");
	Dictionary<string, string> hostRequest = host.Single(entry => entry["EventId"] == "1000" && entry["Operation"] == "GetConnection");
	if (clientRequest["TraceId"] != traceId || hostRequest["TraceId"] != traceId || clientRequest["SpanId"] == hostRequest["SpanId"]
		|| hostRequest["AmbientTraceId"] != traceId || hostRequest["AmbientTraceState"] != "smoke=broker"
		|| !host.Any(entry => entry.TryGetValue("Outcome", out string? outcome) && outcome == "NoRegistration"))
	{
		throw new InvalidOperationException("Client and host structured logs could not be correlated or did not record the rejection.");
	}
}

static JsonRpcDescriptor<IAotCalculator> CreateDescriptor(JsonRpcEncoding encoding)
	=> CreateEncodedDescriptor<IAotCalculator>(new("aot.calculator"), encoding) with
	{
		AdditionalInterfaces = [ServiceInterface.Create<IAotMultiply>("aot.multiply")],
	};

static JsonRpcDescriptor<IRemoteServiceBroker> CreateControlDescriptor(JsonRpcEncoding encoding)
	=> CreateEncodedDescriptor<IRemoteServiceBroker>(FrameworkServices.RemoteServiceBroker.Moniker, encoding);

static JsonRpcDescriptor<T> CreateEncodedDescriptor<T>(ServiceMoniker moniker, JsonRpcEncoding encoding)
	where T : class, PolyType.IShapeable<T> => encoding switch
	{
		JsonRpcEncoding.Json => JsonRpcUtf8Descriptor.Create<T>(moniker),
		JsonRpcEncoding.MessagePack => JsonRpcMessagePackDescriptor.Create<T>(moniker),
		_ => throw new ArgumentOutOfRangeException(nameof(encoding)),
	};

static async Task ValidateAsync(IAotCalculator proxy, CancellationToken cancellationToken)
{
	TaskCompletionSource<int> changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
	EventHandler<int> handler = (sender, value) =>
	{
		if (!ReferenceEquals(sender, proxy))
		{
			changed.TrySetException(new InvalidOperationException("The event sender was not the generated proxy."));
		}
		else
		{
			changed.TrySetResult(value);
		}
	};
	proxy.Changed += handler;
	try
	{
		if (await proxy.AddAsync(19, 23, cancellationToken) != 42 || await changed.Task.WaitAsync(cancellationToken) != 42)
		{
			throw new InvalidOperationException("An invocation or event returned the wrong result.");
		}
	}
	finally
	{
		proxy.Changed -= handler;
	}

	IAotMultiply optional = ((IClientProxy)proxy).As<IAotMultiply>() ?? throw new InvalidOperationException("An advertised capability was not available.");
	if (await optional.MultiplyAsync(6, 7, cancellationToken) != 42)
	{
		throw new InvalidOperationException("The optional interface returned the wrong result.");
	}

	AotCalculator observer = new();
	using IDisposable? subscription = await proxy.ObserveAsync(observer, 42, cancellationToken);
	if (subscription is not null || await observer.Observed.WaitAsync(cancellationToken) != 42)
	{
		throw new InvalidOperationException("The observer or nullable subscription was not preserved.");
	}
}
