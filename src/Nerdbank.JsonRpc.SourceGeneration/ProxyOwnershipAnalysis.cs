// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Nerdbank.JsonRpc.SourceGeneration;

/// <summary>Tracks a single acquisition through local aliases, structured control flow and ownership transfers.</summary>
/// <param name="compilation">The compilation containing the acquisition.</param>
/// <param name="acquisition">The acquisition being checked.</param>
/// <param name="cancellationToken">Cancels analysis.</param>
internal sealed class ProxyOwnershipAnalysis(Compilation compilation, IInvocationOperation acquisition, CancellationToken cancellationToken)
{
	private readonly ISymbol? storage = GetStorage(acquisition);
	private readonly HashSet<ISymbol> checkingOwners = new(SymbolEqualityComparer.Default);

	private enum ExitKind
	{
		None,
		Return,
		Throw,
		Branch,
	}

	/// <summary>Checks whether the acquisition is released on all analyzed paths.</summary>
	/// <param name="root">The enclosing executable body.</param>
	/// <returns>Whether ownership is released or transferred safely.</returns>
	internal bool IsReleased(IOperation root)
	{
		State initial = new();
		IMethodSymbol? method = compilation.GetSemanticModel(acquisition.Syntax.SyntaxTree).GetEnclosingSymbol(acquisition.Syntax.SpanStart, cancellationToken) as IMethodSymbol;
		initial.IsConstructor = method?.MethodKind == MethodKind.Constructor;
		if (this.storage is IFieldSymbol or IPropertySymbol)
		{
			if (!this.OwnerDisposes(this.storage))
			{
				return false;
			}

			initial.Aliases.Add(this.storage);
			initial.Live = method is not null && method.MethodKind is not MethodKind.Constructor and not MethodKind.StaticConstructor;
		}

		return this.Run(root, [initial]).All(static state => !state.Seen || (!state.Live && !state.Leaked));
	}

	private static ISymbol? GetStorage(IOperation operation)
	{
		for (IOperation? current = operation.Parent; current is not null; current = current.Parent)
		{
			if (current is IVariableDeclaratorOperation variable)
			{
				return variable.Symbol;
			}

			if (current is IFieldInitializerOperation field)
			{
				return field.InitializedFields.FirstOrDefault();
			}

			if (current is ISimpleAssignmentOperation assignment)
			{
				return ReferencedSymbol(assignment.Target);
			}

			if (current is ICoalesceAssignmentOperation coalesce)
			{
				return ReferencedSymbol(coalesce.Target);
			}

			if (current is IExpressionStatementOperation or IReturnOperation or IArgumentOperation)
			{
				break;
			}
		}

		return null;
	}

	private static ISymbol? ReferencedSymbol(IOperation? operation) => operation switch
	{
		ILocalReferenceOperation local => local.Local,
		IFieldReferenceOperation field when field.Instance is null or IInstanceReferenceOperation => field.Field,
		IPropertyReferenceOperation property when property.Instance is null or IInstanceReferenceOperation => property.Property,
		IParameterReferenceOperation parameter => parameter.Parameter,
		IConversionOperation conversion => ReferencedSymbol(conversion.Operand),
		_ => null,
	};

	private List<State> Run(IOperation? operation, List<State> states)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (operation is null)
		{
			return states;
		}

		List<State> finished = states.Where(static state => state.Exit != ExitKind.None).ToList();
		states = states.Where(static state => state.Exit == ExitKind.None).ToList();
		if (states.Count == 0)
		{
			return finished;
		}

		switch (operation)
		{
			case IMethodBodyOperation method:
				states = this.Run(method.BlockBody ?? method.ExpressionBody, states);
				break;
			case IConstructorBodyOperation constructor:
				states = this.Run(constructor.BlockBody ?? constructor.ExpressionBody, states);
				break;
			case ILockOperation @lock:
				foreach (State state in states)
				{
					this.Evaluate(@lock.LockedValue, state);
				}

				states = this.Run(@lock.Body, states);
				break;
			case IBlockOperation block:
				List<State> scope = states;
				foreach (IOperation child in block.Operations)
				{
					scope = this.Run(child, scope);
				}

				foreach (State state in scope)
				{
					if (state.UsingScopes.Remove(block))
					{
						state.Live = false;
					}
				}

				states = scope;
				break;
			case IConditionalOperation conditional:
				List<State> branches = [];
				foreach (State state in states)
				{
					this.Evaluate(conditional.Condition, state);
					State other = state.Clone();
					this.RefineNull(conditional.Condition, state, whenTrue: true);
					this.RefineNull(conditional.Condition, other, whenTrue: false);
					branches.AddRange(this.Run(conditional.WhenTrue, [state]));
					branches.AddRange(this.Run(conditional.WhenFalse, [other]));
				}

				states = branches;
				break;
			case IUsingOperation @using:
				List<State> used = [];
				foreach (State state in states)
				{
					bool owns = this.Evaluate(@using.Resources, state);
					List<State> body = this.Run(@using.Body, [state]);
					if (owns)
					{
						foreach (State result in body)
						{
							result.Live = false;
						}
					}

					used.AddRange(body);
				}

				states = used;
				break;
			case IUsingDeclarationOperation declaration:
				foreach (State state in states)
				{
					if (this.Evaluate(declaration.DeclarationGroup, state) && declaration.Parent is IBlockOperation owner)
					{
						state.UsingScopes.Add(owner);
					}
				}

				break;
			case ISwitchOperation @switch:
				List<State> choices = [];
				foreach (State state in states)
				{
					this.Evaluate(@switch.Value, state);
					foreach (ISwitchCaseOperation choice in @switch.Cases)
					{
						List<State> branch = [state.Clone()];
						foreach (IOperation statement in choice.Body)
						{
							branch = this.Run(statement, branch);
						}

						foreach (State result in branch)
						{
							if (result.Exit == ExitKind.Branch && SymbolEqualityComparer.Default.Equals(result.BranchTarget, @switch.ExitLabel))
							{
								result.Exit = ExitKind.None;
							}
						}

						choices.AddRange(branch);
					}

					if (!@switch.Cases.Any(static choice => choice.Clauses.Any(static clause => clause.CaseKind == CaseKind.Default)))
					{
						choices.Add(state);
					}
				}

				states = choices;
				break;
			case ITryOperation @try:
				List<State> tried = this.Run(@try.Body, states);
				foreach (ICatchClauseOperation handler in @try.Catches)
				{
					List<State> thrown = tried.Where(static state => state.Exit == ExitKind.Throw).Select(static state => state.Clone()).ToList();
					foreach (State state in thrown)
					{
						state.Exit = ExitKind.None;
					}

					tried.AddRange(this.Run(handler.Handler, thrown));
				}

				if (@try.Finally is not null)
				{
					List<State> cleaned = [];
					foreach (State state in tried)
					{
						ExitKind exit = state.Exit;
						state.Exit = ExitKind.None;
						List<State> results = this.Run(@try.Finally, [state]);
						foreach (State result in results)
						{
							if (result.Exit == ExitKind.None)
							{
								result.Exit = exit;
							}
						}

						cleaned.AddRange(results);
					}

					tried = cleaned;
				}

				states = tried;
				break;
			case ILoopOperation loop:
				if (loop is IForLoopOperation @for)
				{
					foreach (IOperation before in @for.Before)
					{
						states = this.Run(before, states);
					}
				}

				foreach (State state in states)
				{
					this.Evaluate(
						loop switch
						{
							IForLoopOperation item => item.Condition,
							IWhileLoopOperation item => item.Condition,
							IForEachLoopOperation item => item.Collection,
							_ => null,
						},
						state);
				}

				// Include zero iterations and a second iteration to detect replacement without disposal.
				List<State> skipped = states.Select(static state => state.Clone()).ToList();
				List<State> completed = [];
				for (int iteration = 0; iteration < 2; iteration++)
				{
					states = this.Run(loop.Body, states);
					List<State> continuing = [];
					foreach (State state in states)
					{
						if (state.Exit == ExitKind.Branch && SymbolEqualityComparer.Default.Equals(state.BranchTarget, loop.ContinueLabel))
						{
							state.Exit = ExitKind.None;
						}

						if (state.Exit == ExitKind.None)
						{
							continuing.Add(state);
						}
						else
						{
							if (state.Exit == ExitKind.Branch && SymbolEqualityComparer.Default.Equals(state.BranchTarget, loop.ExitLabel))
							{
								state.Exit = ExitKind.None;
							}

							completed.Add(state);
						}
					}

					states = continuing;
					if (loop is IForLoopOperation bottom)
					{
						foreach (IOperation after in bottom.AtLoopBottom)
						{
							states = this.Run(after, states);
						}
					}

					completed.AddRange(states.Select(static state => state.Clone()));
				}

				states = completed;
				states.AddRange(skipped);
				break;
			case IReturnOperation @return:
				foreach (State state in states)
				{
					if (this.Evaluate(@return.ReturnedValue, state))
					{
						state.Live = false;
					}

					state.Exit = ExitKind.Return;
				}

				break;
			case IThrowOperation @throw:
				foreach (State state in states)
				{
					this.Evaluate(@throw.Exception, state);
					state.Exit = ExitKind.Throw;
				}

				break;
			case IBranchOperation branch:
				foreach (State state in states)
				{
					state.Exit = ExitKind.Branch;
					state.BranchTarget = branch.Target;
				}

				break;
			default:
				foreach (State state in states)
				{
					this.Evaluate(operation, state);
				}

				break;
		}

		finished.AddRange(states);
		return this.Merge(finished);
	}

	private bool Evaluate(IOperation? operation, State state)
	{
		cancellationToken.ThrowIfCancellationRequested();
		switch (operation)
		{
			case null:
			case IAnonymousFunctionOperation:
			case ILocalFunctionOperation:
				return false;
			case IInvocationOperation invocation when ReferenceEquals(invocation, acquisition):
				state.Leaked |= state.Live || state.Stored;
				state.Live = true;
				state.Seen = true;
				return true;
			case IInvocationOperation invocation:
				bool instance = this.Evaluate(invocation.Instance, state);
				bool argument = false;
				foreach (IArgumentOperation item in invocation.Arguments)
				{
					argument |= this.Evaluate(item.Value, state);
				}

				if (this.IsDispose(invocation))
				{
					ISymbol? disposedMember = invocation.Instance is IConditionalAccessInstanceOperation ? state.ConditionalMember : ReferencedSymbol(invocation.Instance);
					if (disposedMember is IFieldSymbol or IPropertySymbol)
					{
						state.VacantMembers.Add(disposedMember);
					}

					if (instance)
					{
						state.Live = false;
						state.Stored = false;
					}
				}

				// Await configuration preserves the identity of the eventual proxy, not ownership.
				return invocation.TargetMethod.Name is "ConfigureAwait" or "AsTask" && (instance || argument);
			case IConditionalAccessOperation access:
				bool receiver = this.Evaluate(access.Operation, state);
				bool previous = state.ConditionalReceiver;
				ISymbol? previousMember = state.ConditionalMember;
				state.ConditionalReceiver = receiver;
				state.ConditionalMember = ReferencedSymbol(access.Operation);
				bool result = this.Evaluate(access.WhenNotNull, state);
				state.ConditionalReceiver = previous;
				state.ConditionalMember = previousMember;
				return result;
			case IConditionalAccessInstanceOperation:
				return state.ConditionalReceiver;
			case IVariableDeclaratorOperation variable:
				bool initialized = this.Evaluate(variable.Initializer?.Value, state);
				this.SetAlias(variable.Symbol, initialized, state);
				return initialized;
			case ISimpleAssignmentOperation assignment:
				bool assigned = this.Evaluate(assignment.Value, state);
				ISymbol? target = ReferencedSymbol(assignment.Target);
				if (assigned && target is IFieldSymbol or IPropertySymbol)
				{
					bool previousReleased = SymbolEqualityComparer.Default.Equals(target, this.storage)
						|| state.VacantMembers.Contains(target)
						|| (state.IsConstructor && !state.AssignedMembers.Contains(target));
					if (!previousReleased || !this.OwnerDisposes(target))
					{
						state.Leaked = true;
					}

					state.Live = false;
					state.Stored = true;
				}
				else if (!assigned && target is not null && state.Stored && state.Aliases.Contains(target))
				{
					state.Leaked = true;
				}

				this.SetAlias(target, assigned, state);
				if (target is IFieldSymbol or IPropertySymbol)
				{
					state.VacantMembers.Remove(target);
					state.AssignedMembers.Add(target);
				}

				return assigned;
			case ICoalesceAssignmentOperation coalesce:
				ISymbol? member = ReferencedSymbol(coalesce.Target);
				if (member is not null && SymbolEqualityComparer.Default.Equals(member, this.storage))
				{
					state.Live = false;
					state.Stored = false;
				}

				bool acquired = this.Evaluate(coalesce.Value, state);
				this.SetAlias(member, acquired, state);
				if (acquired && member is IFieldSymbol or IPropertySymbol)
				{
					state.Leaked |= !this.OwnerDisposes(member);
					state.Live = false;
					state.Stored = true;
				}

				if (member is IFieldSymbol or IPropertySymbol)
				{
					state.VacantMembers.Remove(member);
					state.AssignedMembers.Add(member);
				}

				return acquired;
			case IConversionOperation conversion:
				return this.Evaluate(conversion.Operand, state);
			case IAwaitOperation awaited:
				return this.Evaluate(awaited.Operation, state);
			case ICoalesceOperation coalesce:
				bool value = this.Evaluate(coalesce.Value, state);
				State missing = state.Clone();
				if (value)
				{
					missing.Live = false;
				}

				bool fallback = this.Evaluate(coalesce.WhenNull, missing);
				state.Live |= missing.Live;
				state.Leaked |= missing.Leaked;
				state.Seen |= missing.Seen;
				state.Stored |= missing.Stored;
				return value || fallback;
			case IConditionalOperation conditional:
				this.Evaluate(conditional.Condition, state);
				State alternative = state.Clone();
				this.RefineNull(conditional.Condition, state, whenTrue: true);
				this.RefineNull(conditional.Condition, alternative, whenTrue: false);
				bool whenTrue = this.Evaluate(conditional.WhenTrue, state);
				bool whenFalse = this.Evaluate(conditional.WhenFalse, alternative);
				state.Live |= alternative.Live;
				state.Leaked |= alternative.Leaked;
				state.Seen |= alternative.Seen;
				state.Stored |= alternative.Stored;
				state.Aliases.IntersectWith(alternative.Aliases);
				state.UsingScopes.IntersectWith(alternative.UsingScopes);
				state.VacantMembers.IntersectWith(alternative.VacantMembers);
				state.AssignedMembers.UnionWith(alternative.AssignedMembers);
				return whenTrue && whenFalse;
			case IIsPatternOperation pattern:
				state.PatternValue = this.Evaluate(pattern.Value, state);
				return false;
			case ISwitchExpressionOperation expression:
				this.Evaluate(expression.Value, state);
				State before = state.Clone();
				state.Live = false;
				state.Stored = false;
				bool ownsEveryArm = !expression.Arms.IsEmpty;
				foreach (ISwitchExpressionArmOperation arm in expression.Arms)
				{
					State choice = before.Clone();
					this.Evaluate(arm.Guard, choice);
					ownsEveryArm &= this.Evaluate(arm.Value, choice);
					state.Live |= choice.Live;
					state.Seen |= choice.Seen;
					state.Stored |= choice.Stored;
					state.Leaked |= choice.Leaked;
					state.Aliases.IntersectWith(choice.Aliases);
				}

				return ownsEveryArm;
			case IBinaryOperation binary when binary.OperatorKind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr:
				this.Evaluate(binary.LeftOperand, state);
				State skipped = state.Clone();
				this.Evaluate(binary.RightOperand, state);
				state.Live |= skipped.Live;
				state.Leaked |= skipped.Leaked;
				state.Seen |= skipped.Seen;
				state.Stored |= skipped.Stored;
				state.Aliases.IntersectWith(skipped.Aliases);
				state.VacantMembers.IntersectWith(skipped.VacantMembers);
				state.AssignedMembers.UnionWith(skipped.AssignedMembers);
				return false;
			case IPropertyReferenceOperation property:
				bool propertyReceiver = this.Evaluate(property.Instance, state);
				return (property.Property.Name == "Result" && propertyReceiver)
					|| (ReferencedSymbol(operation) is { } propertySymbol && state.Aliases.Contains(propertySymbol));
			case ILocalReferenceOperation or IFieldReferenceOperation or IParameterReferenceOperation:
				return ReferencedSymbol(operation) is { } symbol && state.Aliases.Contains(symbol);
			case IVariableDeclarationGroupOperation or IVariableDeclarationOperation or IVariableInitializerOperation or IParenthesizedOperation:
				bool childResult = false;
				foreach (IOperation child in operation.ChildOperations)
				{
					childResult |= this.Evaluate(child, state);
				}

				return childResult;
			default:
				foreach (IOperation child in operation.ChildOperations)
				{
					this.Evaluate(child, state);
				}

				return false;
		}
	}

	private bool OwnerDisposes(ISymbol member)
	{
		if (member.IsStatic || !this.checkingOwners.Add(member))
		{
			return false;
		}

		try
		{
			INamedTypeSymbol owner = member.ContainingType;
			bool foundDisposal = false;
			foreach (INamedTypeSymbol contract in owner.AllInterfaces.Where(static type => type.ToDisplayString() is KnownApis.IDisposable or KnownApis.IAsyncDisposable))
			{
				foreach (IMethodSymbol method in contract.GetMembers().OfType<IMethodSymbol>())
				{
					if (owner.FindImplementationForInterfaceMember(method) is not IMethodSymbol implementation)
					{
						return false;
					}

					bool releases = false;
					foreach (SyntaxReference syntax in implementation.DeclaringSyntaxReferences)
					{
						IOperation? body = compilation.GetSemanticModel(syntax.SyntaxTree).GetOperation(syntax.GetSyntax(cancellationToken), cancellationToken);
						if (body is null)
						{
							continue;
						}

						State state = new() { Live = true };
						state.Aliases.Add(member);
						releases |= this.Run(body, [state]).All(static result => !result.Live && !result.Leaked);
					}

					if (!releases)
					{
						return false;
					}

					foundDisposal = true;
				}
			}

			return foundDisposal;
		}
		finally
		{
			this.checkingOwners.Remove(member);
		}
	}

	private bool IsDispose(IInvocationOperation invocation)
	{
		IMethodSymbol method = invocation.TargetMethod;
		if (method.Parameters.Length != 0)
		{
			return false;
		}

		bool asynchronous = method.Name == KnownApis.DisposeAsync;
		if (!asynchronous && method.Name != KnownApis.Dispose)
		{
			return false;
		}

		string contractName = asynchronous ? KnownApis.IAsyncDisposable : KnownApis.IDisposable;
		INamedTypeSymbol type = method.ContainingType;
		INamedTypeSymbol? contract = compilation.GetTypeByMetadataName(contractName);
		IMethodSymbol? disposal = contract?.GetMembers(method.Name).OfType<IMethodSymbol>().FirstOrDefault();
		bool implements = disposal is not null && (SymbolEqualityComparer.Default.Equals(method, disposal)
			|| SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(disposal), method));
		if (!implements || !asynchronous)
		{
			return implements;
		}

		for (IOperation? parent = invocation.Parent; parent is not null; parent = parent.Parent)
		{
			if (parent is IAwaitOperation or IReturnOperation)
			{
				return true;
			}

			if (parent is IExpressionStatementOperation)
			{
				break;
			}

			if (parent is IInvocationOperation wrapper && wrapper.TargetMethod.Name is not "ConfigureAwait" and not "AsTask")
			{
				break;
			}
		}

		return false;
	}

	private void SetAlias(ISymbol? symbol, bool value, State state)
	{
		if (symbol is not null)
		{
			if (value)
			{
				state.Aliases.Add(symbol);
			}
			else
			{
				state.Aliases.Remove(symbol);
			}
		}
	}

	private void RefineNull(IOperation condition, State state, bool whenTrue)
	{
		IOperation? operand = null;
		bool nullWhenTrue = true;
		if (condition is IBinaryOperation binary && binary.OperatorKind is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals)
		{
			operand = binary.LeftOperand.ConstantValue is { HasValue: true, Value: null }
				? binary.RightOperand
				: binary.RightOperand.ConstantValue is { HasValue: true, Value: null }
					? binary.LeftOperand
					: null;
			nullWhenTrue = binary.OperatorKind == BinaryOperatorKind.Equals;
		}
		else if (condition is IIsPatternOperation pattern)
		{
			IPatternOperation test = pattern.Pattern;
			if (test is INegatedPatternOperation negated)
			{
				test = negated.Pattern;
				nullWhenTrue = false;
			}

			if (test is IConstantPatternOperation { Value.ConstantValue: { HasValue: true, Value: null } })
			{
				operand = pattern.Value;
			}
			else if (state.PatternValue)
			{
				ISymbol? declared = test switch
				{
					IDeclarationPatternOperation declaration => declaration.DeclaredSymbol,
					IRecursivePatternOperation recursive => recursive.DeclaredSymbol,
					_ => null,
				};
				if (whenTrue && declared is not null)
				{
					state.Aliases.Add(declared);
				}

				if (!whenTrue && (test is IRecursivePatternOperation { PropertySubpatterns.IsEmpty: true, DeconstructionSubpatterns.IsEmpty: true }
					|| (test is IDeclarationPatternOperation { MatchedType: { } type } && type.ToDisplayString() is KnownApis.IDisposable or KnownApis.IAsyncDisposable or "object")))
				{
					state.Live = false;
				}
			}
		}

		if (whenTrue == nullWhenTrue && operand is not null)
		{
			ISymbol? symbol = ReferencedSymbol(operand);
			if (symbol is IFieldSymbol or IPropertySymbol)
			{
				state.VacantMembers.Add(symbol);
			}

			if ((symbol is not null && state.Aliases.Contains(symbol)) || (condition is IIsPatternOperation && state.PatternValue))
			{
				state.Live = false;
				state.Stored = false;
			}
		}
	}

	private List<State> Merge(List<State> states)
	{
		if (states.Count <= 32)
		{
			return states;
		}

		List<State> result = [];
		foreach (IGrouping<ExitKind, State> group in states.GroupBy(static state => state.Exit))
		{
			State merged = group.First().Clone();
			foreach (State state in group.Skip(1))
			{
				merged.Live |= state.Live;
				merged.Seen |= state.Seen;
				merged.Stored |= state.Stored;
				merged.Leaked |= state.Leaked;
				merged.Aliases.IntersectWith(state.Aliases);
				merged.UsingScopes.IntersectWith(state.UsingScopes);
				merged.VacantMembers.IntersectWith(state.VacantMembers);
				merged.AssignedMembers.UnionWith(state.AssignedMembers);
			}

			result.Add(merged);
		}

		return result;
	}

	private sealed class State
	{
		internal HashSet<ISymbol> Aliases { get; } = new(SymbolEqualityComparer.Default);

		internal HashSet<IOperation> UsingScopes { get; } = [];

		internal HashSet<ISymbol> VacantMembers { get; } = new(SymbolEqualityComparer.Default);

		internal HashSet<ISymbol> AssignedMembers { get; } = new(SymbolEqualityComparer.Default);

		internal bool IsConstructor { get; set; }

		internal bool Live { get; set; }

		internal bool Seen { get; set; }

		internal bool Stored { get; set; }

		internal bool Leaked { get; set; }

		internal bool ConditionalReceiver { get; set; }

		internal ISymbol? ConditionalMember { get; set; }

		internal bool PatternValue { get; set; }

		internal ExitKind Exit { get; set; }

		internal ILabelSymbol? BranchTarget { get; set; }

		internal State Clone()
		{
			State result = new() { Live = this.Live, Seen = this.Seen, Stored = this.Stored, Leaked = this.Leaked, ConditionalReceiver = this.ConditionalReceiver, ConditionalMember = this.ConditionalMember, PatternValue = this.PatternValue, Exit = this.Exit, BranchTarget = this.BranchTarget, IsConstructor = this.IsConstructor };
			result.Aliases.UnionWith(this.Aliases);
			result.UsingScopes.UnionWith(this.UsingScopes);
			result.VacantMembers.UnionWith(this.VacantMembers);
			result.AssignedMembers.UnionWith(this.AssignedMembers);
			return result;
		}
	}
}
