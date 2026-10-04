// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

/// <summary>Represents an error response received from a remote JSON-RPC peer.</summary>
/// <remarks>This is the consistent outer exception for remote error responses and derives from <see cref="JsonRpcException"/> for compatibility.</remarks>
public sealed class RemoteInvocationException : JsonRpcException
{
	/// <summary>Initializes a new instance of the <see cref="RemoteInvocationException"/> class.</summary>
	/// <param name="errorDetails">The received JSON-RPC error details.</param>
	/// <param name="remoteException">The decoded remote snapshot, if available.</param>
	/// <param name="reconstructedException">The safe local exception representation, if available.</param>
	internal RemoteInvocationException(JsonRpcErrorDetails errorDetails, RemoteExceptionData? remoteException, Exception? reconstructedException)
		: base(errorDetails, reconstructedException)
	{
		this.RemoteException = remoteException;
	}

	/// <summary>Gets the decoded, untrusted diagnostic snapshot, or <see langword="null"/> when none was supplied.</summary>
	public RemoteExceptionData? RemoteException { get; }

	/// <inheritdoc/>
	public override string ToString()
	{
		System.Text.StringBuilder result = new();
		result.Append(this.GetType().FullName).Append(": ").AppendLine(this.Message);
		if (this.RemoteException is RemoteExceptionData remote)
		{
			AppendRemote(result, remote, 0, "remote exception");
		}

		if (this.StackTrace is string localStack)
		{
			result.AppendLine("--- local rethrow stack ---").AppendLine(localStack);
		}

		return result.ToString().TrimEnd();
	}

	/// <summary>Creates the stable remote wrapper and reconstructs only allowlisted exception types.</summary>
	/// <param name="errorDetails">The received JSON-RPC error details.</param>
	/// <param name="serializer">The serializer selected by the connection.</param>
	/// <param name="allowedTypes">The configured exception allowlist.</param>
	/// <param name="logFailure">The callback that records malformed diagnostics or failed typed exception reconstruction.</param>
	/// <returns>The remote exception wrapper.</returns>
	internal static RemoteInvocationException Create(JsonRpcErrorDetails errorDetails, JsonRpcSerializer serializer, RemoteExceptionTypeMapping allowedTypes, Action<Exception>? logFailure)
	{
		RemoteExceptionData? snapshot = errorDetails.Data is JsonRpcValue data ? RemoteExceptionData.TryRead(data, logFailure) : null;
		Exception? reconstructedException = null;
		if (snapshot is not null)
		{
			try
			{
				reconstructedException = RemoteExceptionData.Reconstruct(snapshot, serializer, allowedTypes);
			}
			catch (Exception ex)
			{
				logFailure?.Invoke(ex);
			}
		}

		return new RemoteInvocationException(errorDetails, snapshot, reconstructedException);
	}

	private static void AppendRemote(System.Text.StringBuilder builder, RemoteExceptionData data, int depth, string header)
	{
		if (depth >= 32)
		{
			builder.AppendLine("[remote exception details truncated]");
			return;
		}

		builder.Append("--- ").Append(header).Append(": ").Append(data.TypeName).Append(" (HResult 0x").Append(data.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)).AppendLine(") ---");
		builder.AppendLine(data.Message);
		if (data.StackTrace is not null)
		{
			builder.AppendLine("--- remote throw stack ---").AppendLine(data.StackTrace);
		}

		if (data.Inner is not null)
		{
			AppendRemote(builder, data.Inner, depth + 1, "remote inner exception");
		}

		foreach (RemoteExceptionData child in data.Children)
		{
			AppendRemote(builder, child, depth + 1, "remote aggregate child");
		}
	}
}
