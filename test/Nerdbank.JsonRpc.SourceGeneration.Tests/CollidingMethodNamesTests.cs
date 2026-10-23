// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using TUnit.Core;
using Verify = CSharpContractAnalyzerVerifier;

public class CollidingMethodNamesTests
{
	[Test]
	[Arguments("Async")]
	[Arguments("URLAsync")]
	[Arguments("GetURLAsync")]
	[Arguments("_FooAsync")]
	[Arguments("fooasync")]
	[Arguments("AsyncAsync")]
	public async Task NamingConventionMatchesRuntimeTransform(string clrName)
	{
		string wireName = Nerdbank.JsonRpc.CommonMethodNameTransforms.Default(clrName);
		string source = /* lang=c#-test */ $$"""
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;
			using PolyType;
			[JsonRpcContract(GenerateProxy = false)]
			interface IFoo
			{
				Task {{clrName}}();
				[MethodShape(Name = "{{wireName}}")]
				Task {|#0:Other|}();
			}
			""";

		await Verify.VerifyAsync(source, Verify.Diagnostic("NBJSONRPC002", "IFoo.Other()", $"IFoo.{clrName}()", "IFoo", wireName).WithLocation(0));
	}

	[Test]
	[Arguments("")]
	[Arguments("(GenerateProxy = false)")]
	public async Task ExplicitAndDefaultNamesCollideRegardlessOfProxyGeneration(string options)
	{
		string source = /* lang=c#-test */ $$"""
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;
			using PolyType;

			[JsonRpcContract{{options}}]
			[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
			internal partial interface IBackchannel
			{
				[MethodShape(Name = "displayMessage")]
				Task DisplayMessageWithActionsAsync(string token, string emojiName, string message, string[] actions, CancellationToken cancellationToken);
				Task {|#0:DisplayMessageAsync|}(string token, string emojiName, string message, CancellationToken cancellationToken);
			}
			""";

		await Verify.VerifyAsync(source, Verify.Diagnostic(
			"NBJSONRPC002",
			"IBackchannel.DisplayMessageAsync(string, string, string, System.Threading.CancellationToken)",
			"IBackchannel.DisplayMessageWithActionsAsync(string, string, string, string[], System.Threading.CancellationToken)",
			"IBackchannel",
			"displayMessage").WithLocation(0));
	}

	[Test]
	[Arguments("Task FooAsync(int value);", "Task Foo(string value);", "IFoo.Foo(string)", "IFoo.FooAsync(int)", "foo")]
	[Arguments("Task FooAsync();", "Task FooAsync(int value);", "IFoo.FooAsync(int)", "IFoo.FooAsync()", "foo")]
	[Arguments("Task FooAsync();", "Task FooAsync(CancellationToken token);", "IFoo.FooAsync(System.Threading.CancellationToken)", "IFoo.FooAsync()", "foo")]
	[Arguments("[MethodShape(Name = \"same\")] Task One();", "[MethodShape(Name = \"same\")] Task Two();", "IFoo.Two()", "IFoo.One()", "same")]
	public async Task ArgumentCountsAndTypesDoNotDisambiguate(string first, string second, string method, string other, string wireName)
	{
		int position = second.IndexOf('(', second.LastIndexOf("Task ", StringComparison.Ordinal));
		int start = second.LastIndexOf(' ', position) + 1;
		string markedSecond = second.Substring(0, start) + "{|#0:" + second.Substring(start, position - start) + "|}" + second.Substring(position);
		string source = /* lang=c#-test */ $$"""
			using System.Threading;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;
			using PolyType;

			[JsonRpcContract(GenerateProxy = false)]
			interface IFoo
			{
				{{first}}
				{{markedSecond}}
			}
			""";

		await Verify.VerifyAsync(source, Verify.Diagnostic("NBJSONRPC002", method, other, "IFoo", wireName).WithLocation(0));
	}

	[Test]
	public async Task InheritedNamesCollideOnDerivedContract()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			interface IBase { Task FooAsync(); }
			[JsonRpcContract(GenerateProxy = false)]
			interface {|#0:IDerived|} : IBase { Task Foo(int value); }
			""";

		await Verify.VerifyAsync(Source, Verify.Diagnostic("NBJSONRPC002", "IBase.FooAsync()", "IDerived.Foo(int)", "IDerived", "foo").WithLocation(0));
	}

	[Test]
	public async Task ConstructedGenericBaseMethodsAreAnalyzed()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;
			interface IBase<T> { Task FooAsync(T value); }
			[JsonRpcContract(GenerateProxy = false)]
			interface {|#0:IDerived|} : IBase<int> { Task Foo(string value); }
			""";

		await Verify.VerifyAsync(Source, Verify.Diagnostic("NBJSONRPC002", "IBase<int>.FooAsync(int)", "IDerived.Foo(string)", "IDerived", "foo").WithLocation(0));
	}

	[Test]
	public async Task DiamondInheritanceDoesNotDuplicateMethods()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			interface IBase { Task FooAsync(); }
			interface ILeft : IBase { }
			interface IRight : IBase { }
			[JsonRpcContract(GenerateProxy = false)]
			interface IDerived : ILeft, IRight { }
			""";

		await Verify.VerifyAsync(Source);
	}

	[Test]
	public async Task DistinctExplicitNamesAreVerbatimAndCaseSensitive()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;
			using PolyType;

			[JsonRpcContract(GenerateProxy = false)]
			interface IFoo
			{
				[MethodShape(Name = "FooAsync")]
				Task One();
				[MethodShape(Name = "foo")]
				Task Two();
				[MethodShape(Name = "Foo")]
				Task Three();
				Task FourAsync();
				private Task Four() => Task.CompletedTask;
			}
			""";

		await Verify.VerifyAsync(Source);
	}

	[Test]
	public async Task MarshalableContractsAreAlsoAnalyzed()
	{
		const string Source = /* lang=c#-test */ """
			using System;
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;

			[RpcMarshalable]
			partial interface IFoo : IDisposable
			{
				Task FooAsync();
				Task {|#0:Foo|}(int value);
			}
			""";

		await Verify.VerifyAsync(Source, Verify.Diagnostic("NBJSONRPC002", "IFoo.Foo(int)", "IFoo.FooAsync()", "IFoo", "foo").WithLocation(0));
	}

	[Test]
	public async Task UnattributedInterfacesAreNotAnalyzed()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading.Tasks;
			interface IFoo { Task FooAsync(); Task Foo(); }
			""";

		await Verify.VerifyAsync(Source);
	}

	[Test]
	public async Task MissingLibraryReferenceIsIgnored()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading.Tasks;
			interface IFoo { Task FooAsync(); Task Foo(); }
			""";

		await Verify.VerifyWithoutLibraryAsync(Source);
	}

	[Test]
	public async Task QualifiedAndAliasedContractAttributesAreRecognized()
	{
		const string Source = /* lang=c#-test */ """
			using System.Threading.Tasks;
			using Contract = Nerdbank.JsonRpc.JsonRpcContractAttribute;
			[Contract(GenerateProxy = false)]
			interface IFoo { Task FooAsync(); Task {|#0:Foo|}(); }
			[Nerdbank.JsonRpc.JsonRpcContract(GenerateProxy = false)]
			interface IBar { Task BarAsync(); Task {|#1:Bar|}(); }
			""";

		await Verify.VerifyAsync(
			Source,
			Verify.Diagnostic("NBJSONRPC002", "IFoo.Foo()", "IFoo.FooAsync()", "IFoo", "foo").WithLocation(0),
			Verify.Diagnostic("NBJSONRPC002", "IBar.Bar()", "IBar.BarAsync()", "IBar", "bar").WithLocation(1));
	}

	[Test]
	public async Task GeneratedCodeIsNotAnalyzed()
	{
		const string Source = /* lang=c#-test */ """
			// <auto-generated/>
			using System.Threading.Tasks;
			using Nerdbank.JsonRpc;
			[JsonRpcContract(GenerateProxy = false)]
			interface IFoo { Task FooAsync(); Task Foo(); }
			""";

		await Verify.VerifyAsync(Source);
	}
}
