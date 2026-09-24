// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Nerdbank.JsonRpc;

namespace Encodings;

#pragma warning disable SA1649 // The sample file name matches its documentation topic rather than its type.

public static class Example
{
    public static JsonRpc Start(IDuplexPipe pipe, ILogger logger)
    {
        #region json-encoding
        var configured = new Nerdbank.Json.JsonSerializer();
        var channel = new JsonRpcJsonChannel(pipe, configured, JsonRpcJsonFraming.ContentLength, logger);
        var rpc = new JsonRpc(channel) { Serializer = configured };
        rpc.Start();
        #endregion

        return rpc;
    }
}
