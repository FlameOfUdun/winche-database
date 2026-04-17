using System.Text.Json.Serialization;

namespace WincheDatabase.Store.Models
{
    public record QuerySubscription
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("result")]
        public QueryResult Result { get; init; } = new();
    }
}
