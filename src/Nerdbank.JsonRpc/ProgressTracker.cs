// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.MessagePack;

namespace Nerdbank.JsonRpc;

internal sealed class ProgressTracker
{
	private readonly JsonRpc rpc;
	private readonly object syncObject = new();
	private readonly Dictionary<long, Action<RawMessagePack>> progressHandlers = [];
	private readonly Dictionary<RequestId, List<long>> requestProgressTokens = [];
	private long nextToken;

	internal ProgressTracker(JsonRpc rpc) => this.rpc = rpc;

	internal long Register<T>(IProgress<T> progress, ITypeShape<T> valueShape)
	{
		if (progress is null)
		{
			throw new ArgumentNullException(nameof(progress));
		}

		if (valueShape is null)
		{
			throw new ArgumentNullException(nameof(valueShape));
		}

		lock (this.syncObject)
		{
			long token = this.nextToken++;
			this.progressHandlers.Add(token, value => progress.Report(this.rpc.Serializer.Deserialize(value, valueShape, this.rpc.DisposalToken)!));
			return token;
		}
	}

	internal void AssociateWithRequest(RequestId requestId, IReadOnlyList<long>? tokens)
	{
		if (tokens is null || tokens.Count == 0)
		{
			return;
		}

		lock (this.syncObject)
		{
			this.requestProgressTokens.Add(requestId, [.. tokens]);
		}
	}

	internal void CompleteRequest(RequestId requestId)
	{
		lock (this.syncObject)
		{
			if (this.requestProgressTokens.TryGetValue(requestId, out List<long>? tokens))
			{
				this.requestProgressTokens.Remove(requestId);
				foreach (long token in tokens)
				{
					this.progressHandlers.Remove(token);
				}
			}
		}
	}

	internal void Report(long token, RawMessagePack value)
	{
		Action<RawMessagePack>? handler;
		lock (this.syncObject)
		{
			this.progressHandlers.TryGetValue(token, out handler);
		}

		handler?.Invoke(value);
	}
}
