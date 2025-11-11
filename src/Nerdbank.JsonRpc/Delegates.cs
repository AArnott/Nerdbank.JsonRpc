// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable SA1649 // File name should match first type name

namespace Nerdbank.JsonRpc;

internal delegate ValueTask<DispatchResponse> MethodInvoker(DispatchRequest request);
