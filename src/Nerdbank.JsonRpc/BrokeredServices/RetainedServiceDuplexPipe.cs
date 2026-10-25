// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;

namespace Nerdbank.JsonRpc.BrokeredServices;

/// <summary>Keeps a generation alive until both returned pipe ends are completed.</summary>
internal sealed class RetainedServiceDuplexPipe : IDuplexPipe
{
	/// <summary>Initializes a new instance of the <see cref="RetainedServiceDuplexPipe"/> class.</summary>
	/// <param name="pipe">The service's duplex pipe.</param>
	/// <param name="lease">The active invocation lease.</param>
	internal RetainedServiceDuplexPipe(IDuplexPipe pipe, ServiceProxyLease lease)
	{
		PipeReader input = pipe.Input;
		PipeWriter output = pipe.Output;
		this.Input = new RetainedServicePipeReader(input, lease.Retain());
		this.Output = new RetainedServicePipeWriter(output, lease.Retain());
	}

	/// <inheritdoc/>
	public PipeReader Input { get; }

	/// <inheritdoc/>
	public PipeWriter Output { get; }
}
