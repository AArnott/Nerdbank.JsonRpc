// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Pairs an activated service channel with that activation's actual capabilities.</summary>
public sealed class BrokeredServiceConnection : IAsyncDisposable
{
	/// <summary>Initializes a new instance of the <see cref="BrokeredServiceConnection"/> class.</summary>
	/// <param name="pipe">The caller-owned service channel.</param>
	/// <param name="capabilities">The stable identifiers from the winning registration.</param>
	public BrokeredServiceConnection(IDuplexPipe pipe, IReadOnlyList<string> capabilities)
	{
		this.Pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
		this.Capabilities = Array.AsReadOnly((capabilities ?? throw new ArgumentNullException(nameof(capabilities))).ToArray());
	}

	/// <summary>Gets the caller-owned pipe.</summary>
	public IDuplexPipe Pipe { get; }

	/// <summary>Gets the actual activation's additional contract identifiers.</summary>
	public IReadOnlyList<string> Capabilities { get; }

	/// <inheritdoc/>
	public async ValueTask DisposeAsync()
	{
		try
		{
			await this.Pipe.Output.CompleteAsync().ConfigureAwait(false);
		}
		finally
		{
			await this.Pipe.Input.CompleteAsync().ConfigureAwait(false);
		}
	}
}
