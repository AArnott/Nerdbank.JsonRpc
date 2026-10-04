// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Nerdbank.JsonRpc;
using PolyType;

namespace ErrorHandling;

public static class Example
{
    #region catch-remote-exception
    public static async Task HandleRemoteFailureAsync(JsonRpc rpc, JsonRpcValue arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        try
        {
            await rpc.RequestAsync("DoWork", arguments, cancellationToken);
        }
        catch (RemoteInvocationException ex) when (ex.InnerException is QuotaExceededException quota)
        {
            // This locally available proprietary type is reconstructed because it was allowlisted in JsonRpcOptions.
            string quotaMessage = quota.Message;
            int quotaAmount = quota.Quota;
        }
        catch (RemoteInvocationException ex)
        {
            // The complete diagnostic type name and HRESULT remain available even when the type is not reconstructed.
            string? remoteType = ex.RemoteException?.TypeName;
            int? remoteHResult = ex.RemoteException?.HResult;

            if (ex.InnerException is InvalidOperationException)
            {
                // A built-in exception that Nerdbank.JsonRpc knows how to reconstruct.
            }
            else if (remoteType == typeof(QuotaExceededException).FullName)
            {
                // Fall back to the assembly-independent name when this type is unavailable or not allowlisted locally.
            }
        }
    }
    #endregion

    #region allow-exception-type
    public static JsonRpcOptions CreateOptions()
    {
        RemoteExceptionTypeMapping allowed = new();
        allowed.Add<QuotaExceededException>();

        return new JsonRpcOptions
        {
            AdditionalExceptionTypes = allowed,
        };
    }
    #endregion

    #region disable-details
    public static JsonRpc CreateConnectionWithoutPeerVisibleDetails(JsonRpcPipeChannel channel)
    {
        JsonRpcOptions options = new() { IncludeExceptionDetails = false };
        return new JsonRpc(channel, options);
    }
    #endregion
}

[GenerateShape(Marshaler = typeof(Marshaler))]
public sealed partial class QuotaExceededException : Exception
{
    [ConstructorShape]
    public QuotaExceededException(string message, int quota)
        : base(message)
    {
        this.Quota = quota;
    }

    public int Quota { get; }

    public sealed record ExceptionData(string Message, int Quota);

    public sealed class Marshaler : PolyType.IMarshaler<QuotaExceededException, ExceptionData?>
    {
        public QuotaExceededException? Unmarshal(ExceptionData? value) => value is null ? null : new(value.Message, value.Quota);

        public ExceptionData? Marshal(QuotaExceededException? value) => value is null ? null : new(value.Message, value.Quota);
    }
}
