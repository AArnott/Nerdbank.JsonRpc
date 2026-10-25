// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

public class AvoidStoringServiceRentalTests
{
	[Test]
	[Arguments("[|field|] = await client.GetProxyAsync(descriptor);")]
	[Arguments("[|field|] ??= await client.GetProxyAsync(descriptor);")]
	[Arguments("var rental = await client.GetProxyAsync(descriptor); [|field|] = rental;")]
	public Task ReportsFieldStorage(string body) => VerifyAsync("""
		class C
		{
			ServiceBrokerClient.Rental<IFoo> field;
			async Task M(ServiceBrokerClient client, IJsonRpcDescriptor<IFoo> descriptor)
			{
		""" + body.Replace("[|", "{|NBJSONRPC005:").Replace("|]", "|}") + """

			}
		}
		""");

	[Test]
	public Task ReportsPropertyStorage() => VerifyAsync("""
		class C
		{
			ServiceBrokerClient.Rental<IFoo> Rental { get; set; }
			async Task M(ServiceBrokerClient client, IJsonRpcDescriptor<IFoo> descriptor)
			{
				{|NBJSONRPC005:Rental|} = await client.GetProxyAsync(descriptor);
			}
		}
		""");

	[Test]
	public Task AcceptsLocalRental() => VerifyAsync("""
		class C
		{
			async Task M(ServiceBrokerClient client, IJsonRpcDescriptor<IFoo> descriptor)
			{
				using var rental = await client.GetProxyAsync(descriptor);
				IFoo proxy = rental.Proxy;
			}
		}
		""");

	[Test]
	public Task ReportsConversionToObject() => VerifyAsync("""
		class C
		{
			object field;
			async Task M(ServiceBrokerClient client, IJsonRpcDescriptor<IFoo> descriptor)
			{
				{|NBJSONRPC005:field|} = (object)await client.GetProxyAsync(descriptor);
			}
		}
		""");

	[Test]
	public Task ReportsFieldInitializer() => VerifyAsync("""
		class C
		{
			static ServiceBrokerClient.Rental<IFoo> rental;
			object field {|NBJSONRPC005:= rental|};
		}
		""");

	[Test]
	public Task ReportsPropertyInitializer() => VerifyAsync("""
		class C
		{
			static ServiceBrokerClient.Rental<IFoo> rental;
			object Property { get; } {|NBJSONRPC005:= rental|};
		}
		""");

	[Test]
	public Task IgnoresUnrelatedRentalType() => VerifyAsync("""
		class Rental { }
		class C { Rental field = new Rental(); }
		""");

	private static Task VerifyAsync([System.Diagnostics.CodeAnalysis.StringSyntax("c#-test")] string source)
		=> CSharpBrokeredServiceAnalyzerVerifier.VerifyAsync(source, "NBJSONRPC005");
}
