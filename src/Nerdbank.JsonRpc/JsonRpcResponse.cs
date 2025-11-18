// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Nerdbank.JsonRpc;

public abstract partial class JsonRpcResponse : JsonRpcMessage
{
	public new required RequestId Id
	{
		get => base.Id!.Value;
		init => base.Id = value;
	}
}
