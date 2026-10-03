// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Extensions.Logging;
using Nerdbank.JsonRpc;

namespace ConnectionLifecycle;

#pragma warning disable SA1649 // The sample file name matches its documentation topic rather than its type.

public static class Examples
{
    public static async Task ObserveCompletionAsync(JsonRpc rpc, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        ArgumentNullException.ThrowIfNull(logger);

#pragma warning disable VSTHRD003 // Completion is a task started by the transport, not this caller.
        #region observing-completion
        try
        {
            await rpc.Completion.ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            logger.LogWarning(ex, "Connection ended with calls still pending, or a frame was truncated.");
        }
        catch (System.Net.ProtocolViolationException ex)
        {
            logger.LogError(ex, "Connection rejected an invalid protocol message.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RPC connection failed.");
        }
        finally
        {
            // Also records an idle EOF, for which Completion succeeds.
            logger.LogInformation(
                "Original RPC state: {State}; cause: {Cause}",
                rpc.State,
                rpc.TerminationException);
            rpc.Dispose();
        }
        #endregion
#pragma warning restore VSTHRD003
    }

    public static async Task ConfigureLoggingAsync(JsonRpcPipeChannel channel, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(logger);

        #region connection-logging
        using IDisposable? scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["RpcConnection"] = "worker-42",
        });
        using JsonRpc rpc = new JsonRpc(channel) { Logger = logger };
        rpc.Start();
        await rpc.Completion.ConfigureAwait(false);
        #endregion
    }
}
#pragma warning restore SA1649
