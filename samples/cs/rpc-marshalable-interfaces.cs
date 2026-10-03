// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using Nerdbank.JsonRpc;
using Nerdbank.Streams;
using PolyType;

namespace RpcMarshalableInterfaces;

#pragma warning disable SA1649 // The sample file name matches its documentation topic rather than its type.

#region explicit-lifetime-counter
[RpcMarshalable]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ICounter : IDisposable
{
    Task<int> IncrementAsync(CancellationToken cancellationToken);
}
#endregion

#region optional-interfaces
[RpcMarshalable]
[RpcMarshalableOptionalInterface(1, typeof(IResettableCounter))]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ICounterWithOptionalReset : IDisposable
{
    Task<int> IncrementAsync(CancellationToken cancellationToken);
}

[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IResettableCounter
{
    Task ResetAsync(CancellationToken cancellationToken);
}
#endregion

#region observer-subscription-contract
[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface ISubscriptionService
{
    Task<IDisposable> SubscribeAsync(IObserver<int> observer, CancellationToken cancellationToken);
}
#endregion

#region progress-contract
[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IWorkService
{
    Task RunAsync(IProgress<int>? progress, CancellationToken cancellationToken);
}
#endregion

#region async-enumerable-contract
[GenerateJsonRpcProxy]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
internal partial interface IFileService
{
    IAsyncEnumerable<string> ReadLinesAsync(string path, CancellationToken cancellationToken);

    Task<int> CountAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken);
}
#endregion

internal static class OutOfBandStreamExamples
{
    public static void ConfigureClient(JsonRpcPipeChannel rpcChannel, MultiplexingStream multiplexingStream)
    {
        #region out-of-band-stream-connection
        using JsonRpc client = new(rpcChannel) { MultiplexingStream = multiplexingStream };
        #endregion
    }
}

internal sealed class SequenceService
{
    #region tuned-sequence
    public IAsyncEnumerable<int> ProduceAsync(CancellationToken cancellationToken)
        => this.GenerateAsync(cancellationToken).WithJsonRpcSettings(new()
        {
            MinBatchSize = 10,
            MaxReadAhead = 50,
            Prefetch = 10,
        });
    #endregion

    #region prefetch-argument
    internal async Task<int> CountAsync(IFileService client, IAsyncEnumerable<int> source, CancellationToken cancellationToken)
    {
        int count = await client.CountAsync(
            await source.WithPrefetchAsync(10, cancellationToken),
            cancellationToken);
        return count;
    }
    #endregion

    private async IAsyncEnumerable<int> GenerateAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int i = 0; i < 100; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return i;
            await Task.Yield();
        }
    }
}
#pragma warning restore SA1649
