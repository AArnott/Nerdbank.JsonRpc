// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using VerifyCS = CSharpSourceGeneratorVerifier<Nerdbank.JsonRpc.Analyzers.ProxyGenerator>;

public class ProxyGeneratorTests
{
	[Fact]
	public async Task Test1()
	{
		await VerifyCS.RunDefaultAsync("""
			[JsonRpcContract]
			public partial interface IMyRpc
			{
				Task JustCancellationAsync(CancellationToken cancellationToken);
				ValueTask AnArgAndCancellationAsync(int arg, CancellationToken cancellationToken);
				Task<int> AddAsync(int a, int b, CancellationToken cancellationToken);
				Task<int> MultiplyAsync(int a, int b);
				void Start(string bah);
				void StartCancelable(string bah, CancellationToken token);
				IAsyncEnumerable<int> CountAsync(int start, int count, CancellationToken cancellationToken);
			}
			""");
	}
}
