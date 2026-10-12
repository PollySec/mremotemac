namespace MRemoteMac.Services;

/// <summary>Enum members for a runtime <see cref="Type"/> without <c>Enum.GetValues(Type)</c>, which needs dynamic code under NativeAOT.</summary>
public static class EnumHelper
{
    public static IEnumerable<object> Values(Type enumType)
    {
        ArgumentNullException.ThrowIfNull(enumType);
        foreach (var raw in Enum.GetValuesAsUnderlyingType(enumType))
            yield return Enum.ToObject(enumType, raw!);
    }
}
