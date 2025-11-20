// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft;
using PolyType.Abstractions;

namespace Nerdbank.JsonRpc;

public static class JsonRpcExtensions
{
#if NET8_0
	/// <summary>
	/// A message to use as the argument to <see cref="RequiresDynamicCodeAttribute"/>
	/// for methods that call into <see cref="TypeShapeResolver.ResolveDynamicOrThrow{T}"/>.
	/// </summary>
	/// <seealso href="https://github.com/dotnet/runtime/issues/119440#issuecomment-3269894751"/>
	internal const string ResolveDynamicMessage =
		"Dynamic resolution of IShapeable<T> interface may require dynamic code generation in .NET 8 Native AOT. " +
		"It is recommended to switch to statically resolved IShapeable<T> APIs or upgrade your app to .NET 9 or later.";
#endif

#if NET8_0
	[RequiresDynamicCode(ResolveDynamicMessage)]
#endif
#if NET
	[EditorBrowsable(EditorBrowsableState.Never)]
	[Obsolete("Use the instance method instead. If using the extension method syntax, check that your type argument actually has a [GenerateShape] attribute or otherwise implements IShapeable<T> to avoid a runtime failure.", error: true)]
#endif
	public static void AddRpcTarget<T>(this JsonRpc self, T target) => Requires.NotNull(self).AddRpcTarget(target, PolyType.Abstractions.TypeShapeResolver.ResolveDynamicOrThrow<T>());

#if NET8_0
	[RequiresDynamicCode(ResolveDynamicMessage)]
#endif
#if NET
	[EditorBrowsable(EditorBrowsableState.Never)]
	[Obsolete("Use the instance method instead. If using the extension method syntax, check that your type argument actually has a [GenerateShape] attribute or otherwise implements IShapeable<T> to avoid a runtime failure.", error: true)]
#endif
	public static void Notify<TArg>(this JsonRpc self, string method, in TArg arguments, CancellationToken cancellationToken)
		=> Requires.NotNull(self).Notify(method, arguments, TypeShapeResolver.ResolveDynamicOrThrow<TArg>(), cancellationToken);

#if NET8_0
	[RequiresDynamicCode(ResolveDynamicMessage)]
#endif
#if NET
	[EditorBrowsable(EditorBrowsableState.Never)]
	[Obsolete("Use the instance method instead. If using the extension method syntax, check that your type argument actually has a [GenerateShape] attribute or otherwise implements IShapeable<T> to avoid a runtime failure.", error: true)]
#endif
	public static ValueTask RequestAsync<TArg>(this JsonRpc self, string method, in TArg arguments, CancellationToken cancellationToken)
		=> Requires.NotNull(self).RequestAsync(method, arguments, TypeShapeResolver.ResolveDynamicOrThrow<TArg>(), cancellationToken);

#if NET8_0
	[RequiresDynamicCode(ResolveDynamicMessage)]
#endif
#if NET
	[EditorBrowsable(EditorBrowsableState.Never)]
	[Obsolete("Use the instance method instead. If using the extension method syntax, check that your type argument actually has a [GenerateShape] attribute or otherwise implements IShapeable<T> to avoid a runtime failure.", error: true)]
#endif
	public static ValueTask<TResult> RequestAsync<TArg, TResult>(this JsonRpc self, string method, in TArg arguments, CancellationToken cancellationToken)
		=> Requires.NotNull(self).RequestAsync(method, arguments, TypeShapeResolver.ResolveDynamicOrThrow<TArg>(), TypeShapeResolver.ResolveDynamicOrThrow<TResult>(), cancellationToken);

#if NET8_0
	[RequiresDynamicCode(ResolveDynamicMessage)]
#endif
#if NET
	[EditorBrowsable(EditorBrowsableState.Never)]
	[Obsolete("Use the instance method instead. If using the extension method syntax, check that your type argument actually has a [GenerateShape] attribute or otherwise implements IShapeable<T> to avoid a runtime failure.", error: true)]
#endif
	public static ValueTask<TResult> RequestAsync<TArg, TResult, TResultProvider>(this JsonRpc self, string method, in TArg arguments, CancellationToken cancellationToken)
		=> Requires.NotNull(self).RequestAsync(method, arguments, TypeShapeResolver.ResolveDynamicOrThrow<TArg>(), TypeShapeResolver.ResolveDynamicOrThrow<TResult, TResultProvider>(), cancellationToken);
}
