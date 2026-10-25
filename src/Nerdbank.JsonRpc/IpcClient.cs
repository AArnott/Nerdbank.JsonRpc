// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Nerdbank.JsonRpc;

/// <summary>Connects to same-machine, same-account named pipes (Unix-domain sockets on Unix).</summary>
/// <remarks>Access checks are mandatory. This does not isolate mutually hostile processes running as the same account.</remarks>
public static class IpcClient
{
	/// <summary>Connects only to a local server owned by the current account.</summary>
	/// <param name="name">The endpoint returned by the server.</param>
	/// <param name="cancellationToken">Cancels waiting for the server.</param>
	/// <returns>The caller-owned duplex stream.</returns>
	public static async Task<Stream> ConnectAsync(string name, CancellationToken cancellationToken = default)
	{
		string pipeName = IpcUtilities.ValidateName(name);
#if NETFRAMEWORK || NETSTANDARD2_0
		bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
		NamedPipeClientStream client = new(".", pipeName, PipeDirection.InOut, windows ? PipeOptions.Asynchronous : IpcUtilities.SecureOptions);
#else
		NamedPipeClientStream client = new(".", pipeName, PipeDirection.InOut, IpcUtilities.SecureOptions);
#endif
		try
		{
			await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
#if NETFRAMEWORK || NETSTANDARD2_0
			if (windows)
			{
				using WindowsIdentity identity = WindowsIdentity.GetCurrent();
				if (!Equals(client.GetAccessControl().GetOwner(typeof(SecurityIdentifier)), identity.User))
				{
					throw new UnauthorizedAccessException("The pipe server is not owned by the current account.");
				}
			}
#endif
			return client;
		}
		catch
		{
#if NET
			await client.DisposeAsync().ConfigureAwait(false);
#else
#pragma warning disable VSTHRD103 // Legacy pipe streams have no asynchronous disposal.
			client.Dispose();
#pragma warning restore VSTHRD103
#endif
			throw;
		}
	}
}
