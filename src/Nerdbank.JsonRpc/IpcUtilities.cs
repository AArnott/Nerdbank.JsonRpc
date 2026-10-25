// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Nerdbank.JsonRpc;

/// <summary>Shares endpoint validation and secure pipe creation between IPC clients and servers.</summary>
internal static class IpcUtilities
{
	/// <summary>The runtime flag for same-account enforcement, including on netstandard2.0.</summary>
	internal const PipeOptions SecureOptions = PipeOptions.Asynchronous | (PipeOptions)0x20000000;

	/// <summary>Creates a pipe before publishing its name.</summary>
	/// <param name="name">The validated pipe name.</param>
	/// <returns>The unconnected secure server instance.</returns>
	internal static NamedPipeServerStream CreatePipe(string name)
	{
#if NET
		if (!OperatingSystem.IsWindows())
#else
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
#endif
		{
			return new(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, SecureOptions);
		}

		return CreateWindowsPipe(name);
	}

	/// <summary>Validates a local endpoint without silently redirecting it to another machine.</summary>
	/// <param name="name">The supplied name.</param>
	/// <returns>The runtime pipe name.</returns>
	internal static string ValidateName(string name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			throw new ArgumentException("An IPC endpoint is required.", nameof(name));
		}

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			const string prefix = @"\\.\pipe\";
			string leaf = name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name.Substring(prefix.Length) : name;
			if (leaf.IndexOfAny(['\\', '/', ':']) >= 0 || leaf == "." || leaf.Length > 200)
			{
				throw new ArgumentException("A local pipe leaf name is required.", nameof(name));
			}

			return leaf;
		}

		string path = Path.IsPathRooted(name) ? name : Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + name);
		int maximum = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 103 : 107;
		if (Encoding.UTF8.GetByteCount(path) > maximum || name.IndexOf('\0') >= 0)
		{
			throw new ArgumentException("The Unix socket endpoint is invalid or too long.", nameof(name));
		}

		return name;
	}

	/// <summary>Creates the account-restricted Windows pipe with network logons explicitly denied.</summary>
	/// <param name="name">The local pipe name.</param>
	/// <returns>The unconnected server.</returns>
#if NET
	[System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
	private static NamedPipeServerStream CreateWindowsPipe(string name)
	{
		using WindowsIdentity identity = WindowsIdentity.GetCurrent();
		SecurityIdentifier user = identity.User ?? throw new UnauthorizedAccessException("The current account has no SID.");
		PipeSecurity security = new();
		security.SetOwner(user);
		security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
		security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
		security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
#if NETFRAMEWORK
		return new(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#elif NETSTANDARD2_0
		return WindowsPipeInterop.Create(name, security);
#else
		return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
#endif
	}
}
