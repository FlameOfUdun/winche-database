using Winche.Values;

namespace Winche.Database.IntegrationTests;

/// <summary>Short typed reads for assertions: each throws if the value is another type.</summary>
internal static class ValueAccess
{
    extension(Value v)
    {
        public IReadOnlyDictionary<string, Value> AsMap => ((MapValue)v).Fields;
        public long AsInt => ((IntegerValue)v).Value;
        public string AsString => ((StringValue)v).Value;
        public string AsPath => ((ReferenceValue)v).Path;
    }
}
