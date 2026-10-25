// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NETSTANDARD2_0
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32.Security;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Pipes;

namespace Nerdbank.JsonRpc;

/// <summary>Supplies atomic Windows pipe ACL creation on targets without NamedPipeServerStreamAcl.</summary>
internal static class WindowsPipeInterop
{
	/// <summary>Creates an overlapped byte pipe with its final access rules applied at creation.</summary>
	/// <param name="name">The validated local pipe name.</param>
	/// <param name="security">The mandatory same-account, network-denying ACL.</param>
	/// <returns>The unconnected server stream.</returns>
	internal static unsafe NamedPipeServerStream Create(string name, PipeSecurity security)
	{
		byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
		fixed (byte* address = descriptor)
		{
			SECURITY_ATTRIBUTES attributes = new() { nLength = (uint)sizeof(SECURITY_ATTRIBUTES), lpSecurityDescriptor = address };

			// PIPE_REJECT_REMOTE_CLIENTS also enforces locality independently of the DACL.
			SafePipeHandle handle = CreateNamedPipe(
				@"\\.\pipe\" + name,
				FILE_FLAGS_AND_ATTRIBUTES.PIPE_ACCESS_DUPLEX | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OVERLAPPED,
				NAMED_PIPE_MODE.PIPE_REJECT_REMOTE_CLIENTS,
				255,
				0,
				0,
				0,
				ref attributes);
			if (handle.IsInvalid)
			{
				int error = Marshal.GetLastWin32Error();
				handle.Dispose();
				throw new Win32Exception(error);
			}

			try
			{
				return new(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
			}
			catch
			{
				handle.Dispose();
				throw;
			}
		}
	}

	// CsWin32's projection omits SetLastError for this API, which would discard native failure diagnostics.
	[DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
	private static extern SafePipeHandle CreateNamedPipe(string name, FILE_FLAGS_AND_ATTRIBUTES openMode, NAMED_PIPE_MODE pipeMode, uint maxInstances, uint outBufferSize, uint inBufferSize, uint timeout, ref SECURITY_ATTRIBUTES attributes);
}
#endif
