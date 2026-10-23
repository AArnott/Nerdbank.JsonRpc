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

	/// <summary>Tests an exact advertised optional-interface identifier for source-generated proxy routing.</summary>
	/// <param name="client">The RPC client used by the generated proxy.</param>
	/// <param name="interfaceId">The optional-interface wire identifier.</param>
	/// <returns>Whether the marshaled-object client received this exact identifier in the peer's advertisement.</returns>
	/// <remarks>This source-generator support API does not perform inheritance-aware capability queries.</remarks>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="client"/> is null.</exception>
	[EditorBrowsable(EditorBrowsableState.Never)]
	public static bool IsOptionalInterfaceAdvertised(IJsonRpcClient client, int interfaceId)
		=> Requires.NotNull(client) is MarshaledObjectProxyClient marshaled && marshaled.IsOptionalInterfaceAdvertised(interfaceId);

	/// <summary>Returns a proxy as an interface only when that interface is enabled on the remote object.</summary>
	/// <typeparam name="T">The contract interface type.</typeparam>
	/// <param name="proxy">The proxy to query, or <see langword="null"/>.</param>
	/// <returns>The same proxy typed as <typeparamref name="T"/>, or <see langword="null"/> if it is null or the interface is not enabled.</returns>
	public static T? As<T>(this IClientProxy? proxy)
		where T : class => proxy?.Is(typeof(T)) is true ? (T)(object)proxy : null;

	/// <summary>Adds an exception type by resolving its source-generated shape dynamically.</summary>
	/// <typeparam name="TException">The allowed exception type.</typeparam>
	/// <param name="self">The mapping to update.</param>
	/// <remarks>Use the statically-resolved instance overload for NativeAOT-safe registration.</remarks>
#if NET8_0
	[RequiresDynamicCode(ResolveDynamicMessage)]
#endif
#if NET
	[EditorBrowsable(EditorBrowsableState.Never)]
	[Obsolete("Use the instance Add<TException> method for a source-generated exception type, or Add<TException, TProvider> for one generated on a witness type.", error: true)]
#endif
	public static void Add<TException>(this RemoteExceptionTypeMapping self)
		where TException : Exception
		=> Requires.NotNull(self).AddResolvedShape(TypeShapeResolver.ResolveDynamicOrThrow<TException>());

	/// <summary>Adds an exception type using a source-generated shape from a witness type.</summary>
	/// <typeparam name="TException">The allowed exception type.</typeparam>
	/// <typeparam name="TProvider">The witness type that provides the source-generated shape.</typeparam>
	/// <param name="self">The mapping to update.</param>
	/// <remarks>Use the statically-resolved instance overload for NativeAOT-safe registration.</remarks>
#if NET8_0
	[RequiresDynamicCode(ResolveDynamicMessage)]
#endif
#if NET
	[EditorBrowsable(EditorBrowsableState.Never)]
	[Obsolete("Use the instance Add<TException, TProvider> method for a source-generated witness type.", error: true)]
#endif
	public static void Add<TException, TProvider>(this RemoteExceptionTypeMapping self)
		where TException : Exception
		=> Requires.NotNull(self).AddResolvedShape(TypeShapeResolver.ResolveDynamicOrThrow<TException, TProvider>());

#if NET8_0
	[RequiresDynamicCode(ResolveDynamicMessage)]
#endif
#if NET
	[EditorBrowsable(EditorBrowsableState.Never)]
	[Obsolete("Use the instance method instead. If using the extension method syntax, check that your type argument actually has a [GenerateShape] attribute or otherwise implements IShapeable<T> to avoid a runtime failure.", error: true)]
#endif
	public static void AddRpcTarget<T>(this JsonRpc self, T target, JsonRpcTargetOptions? options = null) => Requires.NotNull(self).AddRpcTarget(target, PolyType.Abstractions.TypeShapeResolver.ResolveDynamicOrThrow<T>(), options);

#if NET8_0
	[RequiresDynamicCode(ResolveDynamicMessage)]
#endif
#if NET
	[EditorBrowsable(EditorBrowsableState.Never)]
	[Obsolete("Use the instance method instead. If using the extension method syntax, check that your type argument actually has a [GenerateShape] attribute or otherwise implements IShapeable<T> to avoid a runtime failure.", error: true)]
#endif
	public static ValueTask NotifyAsync<TArg>(this JsonRpc self, string method, in TArg arguments, CancellationToken cancellationToken)
		=> Requires.NotNull(self).NotifyAsync(method, arguments, TypeShapeResolver.ResolveDynamicOrThrow<TArg>(), cancellationToken);

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
