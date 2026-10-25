# NBJSONRPC050: Use IClientProxy for marshalable interface checks

| Property | Value |
| - | - |
| ID | `NBJSONRPC050` |
| Category | Usage |
| Severity | Warning |

Use the generated interface-level `Is(Type)` or `As<T>` extensions when checking or converting
between two `RpcMarshalableAttribute`-annotated interfaces. These APIs query the capabilities
advertised by a generated proxy, while retaining ordinary type-check behavior for user-defined objects.

```csharp
// NBJSONRPC050
if (value is IOptionalFeature)
{
}
```

Use `value.As<IOptionalFeature>()` to convert a proxy, or
`value.Is(typeof(IOptionalFeature))` to test its advertised capabilities.
