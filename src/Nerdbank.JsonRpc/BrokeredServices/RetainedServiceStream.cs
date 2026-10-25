// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Keeps a service generation alive until its returned stream is disposed.</summary>
internal sealed class RetainedServiceStream : Stream, IAsyncDisposable
{
	private readonly ServiceProxyLease lease;
	private int disposed;

	/// <summary>Initializes a new instance of the <see cref="RetainedServiceStream"/> class.</summary>
	/// <param name="stream">The returned stream.</param>
	/// <param name="lease">The retained generation lease.</param>
	internal RetainedServiceStream(Stream stream, ServiceProxyLease lease)
	{
		this.Inner = stream;
		this.lease = lease;
	}

	/// <inheritdoc/>
	public override bool CanRead => Volatile.Read(ref this.disposed) == 0 && this.Inner.CanRead;

	/// <inheritdoc/>
	public override bool CanSeek => Volatile.Read(ref this.disposed) == 0 && this.Inner.CanSeek;

	/// <inheritdoc/>
	public override bool CanWrite => Volatile.Read(ref this.disposed) == 0 && this.Inner.CanWrite;

	/// <inheritdoc/>
	public override bool CanTimeout => this.Inner.CanTimeout;

	/// <inheritdoc/>
	public override long Length => this.GetStream().Length;

	/// <inheritdoc/>
	public override long Position
	{
		get => this.GetStream().Position;
		set => this.GetStream().Position = value;
	}

	/// <inheritdoc/>
	public override int ReadTimeout
	{
		get => this.GetStream().ReadTimeout;
		set => this.GetStream().ReadTimeout = value;
	}

	/// <inheritdoc/>
	public override int WriteTimeout
	{
		get => this.GetStream().WriteTimeout;
		set => this.GetStream().WriteTimeout = value;
	}

	/// <summary>Gets the service's original stream.</summary>
	internal Stream Inner { get; }

	/// <inheritdoc/>
	public override void Flush() => this.GetStream().Flush();

	/// <inheritdoc/>
	public override Task FlushAsync(CancellationToken cancellationToken) => this.GetStream().FlushAsync(cancellationToken);

	/// <inheritdoc/>
	public override int Read(byte[] buffer, int offset, int count) => this.GetStream().Read(buffer, offset, count);

	/// <inheritdoc/>
	public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => this.GetStream().ReadAsync(buffer, offset, count, cancellationToken);

	/// <inheritdoc/>
	public override void Write(byte[] buffer, int offset, int count) => this.GetStream().Write(buffer, offset, count);

	/// <inheritdoc/>
	public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => this.GetStream().WriteAsync(buffer, offset, count, cancellationToken);

	/// <inheritdoc/>
	public override long Seek(long offset, SeekOrigin origin) => this.GetStream().Seek(offset, origin);

	/// <inheritdoc/>
	public override void SetLength(long value) => this.GetStream().SetLength(value);

	/// <inheritdoc/>
	public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) => this.GetStream().CopyToAsync(destination, bufferSize, cancellationToken);

#if NET
	/// <inheritdoc/>
	public override int Read(Span<byte> buffer) => this.GetStream().Read(buffer);

	/// <inheritdoc/>
	public override void Write(ReadOnlySpan<byte> buffer) => this.GetStream().Write(buffer);

	/// <inheritdoc/>
	public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => this.GetStream().ReadAsync(buffer, cancellationToken);

	/// <inheritdoc/>
	public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => this.GetStream().WriteAsync(buffer, cancellationToken);
#endif

	/// <inheritdoc/>
#if NET
	public override async ValueTask DisposeAsync()
#else
	public async ValueTask DisposeAsync()
#endif
	{
		if (Interlocked.Exchange(ref this.disposed, 1) != 0)
		{
			return;
		}

		try
		{
			if (!ReferenceEquals(this.Inner, this.lease.Target))
			{
				await ServiceLifetime.DisposeObjectAsync(this.Inner).ConfigureAwait(false);
			}
		}
		finally
		{
			this.lease.Dispose();
			GC.SuppressFinalize(this);
		}
	}

	/// <inheritdoc/>
	protected override void Dispose(bool disposing)
	{
		if (disposing && Interlocked.Exchange(ref this.disposed, 1) == 0)
		{
			try
			{
				if (!ReferenceEquals(this.Inner, this.lease.Target))
				{
					this.Inner.Dispose();
				}
			}
			finally
			{
				this.lease.Dispose();
			}
		}

		base.Dispose(disposing);
	}

	private Stream GetStream() => Volatile.Read(ref this.disposed) == 0 ? this.Inner : throw new ObjectDisposedException(nameof(RetainedServiceStream));
}
