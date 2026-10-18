// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Text.Json.Nodes;

namespace Nerdbank.JsonRpc.Tests;

internal static class OpenRpcSchemaAssertions
{
	internal static void AssertNullable(JsonNode schema)
	{
		Assert.NotNull(schema["$id"]);
		Assert.True(Assert.IsType<JsonArray>(schema["anyOf"]).Any(static item => item?["type"] is JsonValue type && type.TryGetValue<string>(out string? value) && value == "null"));
	}

	internal static void AssertLocalDefinitionReferencesResolve(JsonNode schema)
	{
		bool foundReference = false;
		foreach (JsonObject resource in DescendantsAndSelf(schema).Where(static node => node["$id"] is not null && node["definitions"] is JsonObject))
		{
			JsonObject definitions = Assert.IsType<JsonObject>(resource["definitions"]);
			foreach (JsonObject candidate in DescendantsAndSelf(resource))
			{
				if (!ReferenceEquals(candidate, resource) && candidate["$id"] is not null)
				{
					continue;
				}

				if (candidate["$ref"]?.GetValue<string>() is string reference && reference.StartsWith("#/definitions/", StringComparison.Ordinal))
				{
					string definitionName = Uri.UnescapeDataString(reference["#/definitions/".Length..].Split('/')[0]).Replace("~1", "/").Replace("~0", "~");
					Assert.True(definitions.ContainsKey(definitionName), $"Reference '{reference}' does not resolve within schema resource '{resource["$id"]}'.");
					foundReference = true;
				}
			}
		}

		Assert.True(foundReference, "Expected a local definition reference in the recursive schema.");
	}

	private static IEnumerable<JsonObject> DescendantsAndSelf(JsonNode node)
	{
		if (node is JsonObject jsonObject)
		{
			yield return jsonObject;
			foreach (JsonNode? child in jsonObject.Select(static property => property.Value))
			{
				if (child is not null)
				{
					foreach (JsonObject descendant in DescendantsAndSelf(child))
					{
						yield return descendant;
				}
				}
			}
		}
		else if (node is JsonArray array)
		{
			foreach (JsonNode? child in array)
			{
				if (child is not null)
				{
					foreach (JsonObject descendant in DescendantsAndSelf(child))
					{
						yield return descendant;
					}
				}
			}
		}
	}
}
