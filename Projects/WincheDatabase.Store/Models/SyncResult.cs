using System.Text.Json.Serialization;
using WincheDatabase.Core.Models;

namespace WincheDatabase.Store.Models
{
    public sealed record SyncResult
    {
        [JsonPropertyName("path")]
        public required string Path { get; init; }

        [JsonPropertyName("document")]
        public Document? Document { get; init; }

        [JsonPropertyName("appliedCount")]
        public required int AppliedCount { get; init; }

        [JsonPropertyName("hasConflict")]
        public required bool HasConflict { get; init; }
    }
}
