// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.IO.Pipelines;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Retains a generation until its returned reader is completed.</summary>
/// <param name="reader">The service's reader.</param>
/// <param name="lease">The retained generation lease.</param>
internal sealed class RetainedServicePipeReader(PipeReader reader, ServiceProxyLease lease) : PipeReader
{
	private int completed;

	/// <inheritdoc/>
	public override void AdvanceTo(SequencePosition consumed) => reader.AdvanceTo(consumed);

	/// <inheritdoc/>
	public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) => reader.AdvanceTo(consumed, examined);

	/// <inheritdoc/>
	public override void CancelPendingRead() => reader.CancelPendingRead();

	/// <inheritdoc/>
	public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) => reader.ReadAsync(cancellationToken);

	/// <inheritdoc/>
	public override bool TryRead(out ReadResult result) => reader.TryRead(out result);

	/// <inheritdoc/>
	public override void Complete(Exception? exception = null)
	{
		if (Interlocked.Exchange(ref this.completed, 1) == 0)
		{
			try
			{
				reader.Complete(exception);
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
				await reader.CompleteAsync(exception).ConfigureAwait(false);
			}
			finally
			{
				lease.Dispose();
			}
		}
	}
}
