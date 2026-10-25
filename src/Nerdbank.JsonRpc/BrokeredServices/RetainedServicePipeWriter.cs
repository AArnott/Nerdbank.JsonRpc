// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Retains a generation until its returned writer is completed.</summary>
/// <param name="writer">The service's writer.</param>
/// <param name="lease">The retained generation lease.</param>
internal sealed class RetainedServicePipeWriter(PipeWriter writer, ServiceProxyLease lease) : PipeWriter
{
	private int completed;

	/// <inheritdoc/>
	public override bool CanGetUnflushedBytes => writer.CanGetUnflushedBytes;

	/// <inheritdoc/>
	public override long UnflushedBytes => writer.UnflushedBytes;

	/// <inheritdoc/>
	public override void Advance(int bytes) => writer.Advance(bytes);

	/// <inheritdoc/>
	public override Memory<byte> GetMemory(int sizeHint = 0) => writer.GetMemory(sizeHint);

	/// <inheritdoc/>
	public override Span<byte> GetSpan(int sizeHint = 0) => writer.GetSpan(sizeHint);

	/// <inheritdoc/>
	public override void CancelPendingFlush() => writer.CancelPendingFlush();

	/// <inheritdoc/>
	public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) => writer.FlushAsync(cancellationToken);

	/// <inheritdoc/>
	public override void Complete(Exception? exception = null)
	{
		if (Interlocked.Exchange(ref this.completed, 1) == 0)
		{
			try
			{
				writer.Complete(exception);
			}
			finally
			{
				lease.Dispose();
			}
		}
	}

	/// <inheritdoc/>
	public override async ValueTask CompleteAsync(Exception? exception = null)
	{
		if (Interlocked.Exchange(ref this.completed, 1) == 0)
		{
			try
			{
				await writer.CompleteAsync(exception).ConfigureAwait(false);
			}
			finally
			{
				lease.Dispose();
			}
		}
	}
}
