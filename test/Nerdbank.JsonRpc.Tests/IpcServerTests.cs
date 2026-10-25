// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.VisualStudio.Threading;

public class IpcServerTests : TestBase
{
	[Test]
	public async Task RejectsEmbeddedNullEndpoint()
	{
		string endpoint = "service\0suffix";
		Assert.Throws<ArgumentException>(() => IpcServer.Create((_, _) => Task.CompletedTask, new() { Name = endpoint }));
		await Assert.ThrowsAsync<ArgumentException>(() => IpcClient.ConnectAsync(endpoint, this.TimeoutToken));
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public void WindowsRejectsPreExistingPermissivePipe(bool differentOwner)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			Skip.Test("Windows pipe ACLs are not available on this platform.");
			return;
		}

		using WindowsIdentity identity = WindowsIdentity.GetCurrent();
		if (differentOwner && Equals(identity.Owner, identity.User))
		{
			Skip.Test("An elevated token with a group default owner is required.");
			return;
		}

		PipeSecurity security = new();
		security.SetOwner(differentOwner ? identity.Owner! : identity.User!);
		security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
		string name = Guid.NewGuid().ToString("N");
#if NETFRAMEWORK
		using NamedPipeServerStream existing = new(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#else
		using NamedPipeServerStream existing = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#endif
		Assert.Throws<UnauthorizedAccessException>(() => IpcServer.Create((_, _) => throw new InvalidOperationException("An insecure listener must never invoke callbacks."), new() { Name = name }));
		using NamedPipeClientStream client = new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
		client.Connect(1000);
	}

	[Test]
	public async Task WindowsAccessRulesDenyNetworkLogons()
	{
#if NET
		if (!OperatingSystem.IsWindows())
#else
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
#endif
		{
			Skip.Test("Windows pipe ACLs are not available on this platform.");
			return;
		}

		await using IpcServer server = IpcServer.Create((_, token) => Task.Delay(Timeout.Infinite, token));
		using Stream stream = await IpcClient.ConnectAsync(server.Name, this.TimeoutToken);
		PipeSecurity security = Assert.IsAssignableFrom<NamedPipeClientStream>(stream).GetAccessControl();
		using WindowsIdentity identity = WindowsIdentity.GetCurrent();
		Assert.Equal(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
		Assert.True(security.AreAccessRulesProtected);
		SecurityIdentifier network = new(WellKnownSidType.NetworkSid, null);
		AuthorizationRuleCollection rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));
		Assert.Equal(2, rules.Count);
		bool deniedNetwork = false;
		bool allowedUser = false;
		foreach (PipeAccessRule rule in rules)
		{
			Assert.False(rule.IsInherited);
			Assert.Equal(PipeAccessRights.FullControl, rule.PipeAccessRights);
			deniedNetwork |= rule.IdentityReference.Equals(network) && rule.AccessControlType == AccessControlType.Deny;
			allowedUser |= rule.IdentityReference.Equals(identity.User) && rule.AccessControlType == AccessControlType.Allow;
		}

		Assert.True(deniedNetwork);
		Assert.True(allowedUser);
	}

	[Test]
	public async Task WindowsRejectsGroupOwnedPipe()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			Skip.Test("Windows pipe ownership is not available on this platform.");
			return;
		}

		using WindowsIdentity identity = WindowsIdentity.GetCurrent();
		if (Equals(identity.Owner, identity.User))
		{
			Skip.Test("An elevated token with a group default owner is required.");
			return;
		}

		PipeSecurity security = new();
		security.SetOwner(identity.Owner!);
		security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
		string name = Guid.NewGuid().ToString("N");
#if NETFRAMEWORK
		using NamedPipeServerStream server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#else
		using NamedPipeServerStream server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#endif
		await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
		{
			using Stream client = await IpcClient.ConnectAsync(name, this.TimeoutToken);
		});
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task ConnectsAndExchangesData(bool multiple)
	{
		await using IpcServer server = IpcServer.Create(
			async (stream, token) =>
			{
				byte[] buffer = new byte[1];
				Assert.Equal(1, await stream.ReadAsync(buffer, 0, 1, token));
				buffer[0]++;
				await stream.WriteAsync(buffer, 0, 1, token);
			},
			new() { AllowMultipleClients = multiple });
		for (int i = 0; i < (multiple ? 3 : 1); i++)
		{
			using Stream client = await IpcClient.ConnectAsync(server.Name, this.TimeoutToken);
			await client.WriteAsync(new byte[] { 41 }, 0, 1, this.TimeoutToken);
			byte[] response = new byte[1];
			Assert.Equal(1, await client.ReadAsync(response, 0, 1, this.TimeoutToken));
			Assert.Equal(42, response[0]);
		}
	}

	[Test]
	public async Task DisposalCancelsTheListenerAndAcceptedCallbacks()
	{
		TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		IpcServer server = IpcServer.Create(async (stream, token) =>
		{
			entered.SetResult(true);
			await Task.Delay(Timeout.Infinite, token);
		});
		using Stream client = await IpcClient.ConnectAsync(server.Name, this.TimeoutToken);
		await entered.Task.WithCancellation(this.TimeoutToken);
		await server.DisposeAsync();
		Assert.True(server.Completion.IsCompleted);
		await server.DisposeAsync();
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task CancellationCallbackCanDisposeFromAnotherThread(bool multiple)
	{
		TaskCompletionSource<bool> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Task? reentrantDisposal = null;
		IpcServer? serverInstance = null;
		await using IpcServer server = IpcServer.Create(
			async (_, token) =>
			{
				TaskCompletionSource<bool> canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
				using CancellationTokenRegistration registration = token.Register(() =>
				{
					try
					{
						reentrantDisposal = Task.Run(() => serverInstance!.Dispose());
#pragma warning disable VSTHRD002 // Bound the synchronous cancellation callback so a locking regression fails instead of hanging.
						Assert.True(reentrantDisposal.Wait(TimeSpan.FromSeconds(1)));
#pragma warning restore VSTHRD002
					}
					finally
					{
						canceled.TrySetResult(true);
					}
				});
				entered.SetResult(true);
				await canceled.Task.WithCancellation(this.TimeoutToken);
			},
			new() { AllowMultipleClients = multiple });
		serverInstance = server;
		using Stream client = await IpcClient.ConnectAsync(server.Name, this.TimeoutToken);
		await entered.Task.WithCancellation(this.TimeoutToken);
		await Task.Run(server.Dispose).WithCancellation(this.TimeoutToken);
		await server.Completion.WithCancellation(this.TimeoutToken);
		Assert.NotNull(reentrantDisposal);
		await reentrantDisposal.WithCancellation(this.TimeoutToken);
	}

	[Test]
	public async Task ConnectionCancellationIsObserved()
	{
		using CancellationTokenSource cancellation = new();
		Task<Stream> connecting = IpcClient.ConnectAsync(Guid.NewGuid().ToString("N"), cancellation.Token);
		cancellation.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
	}

	[Test]
	[Arguments(false)]
	[Arguments(true)]
	public async Task CallbackFailureFaultsCompletion(bool multiple)
	{
		IpcServer server = IpcServer.Create((_, _) => Task.FromException(new InvalidOperationException("callback")), new IpcServerOptions { AllowMultipleClients = multiple });
		using Stream client = await IpcClient.ConnectAsync(server.Name, this.TimeoutToken);
		await Assert.ThrowsAsync<InvalidOperationException>(() => server.Completion);
		await Assert.ThrowsAsync<InvalidOperationException>(async () => await server.DisposeAsync());
	}
}
