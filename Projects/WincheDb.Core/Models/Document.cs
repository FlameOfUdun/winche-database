using System.Text.Json.Nodes;

namespace WincheDb.Core.Models
{
    public sealed record Document
    {
        public required string Id { get; init; }
        public required string Collection { get; init; }
        public required string Path { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required DateTime UpdatedAt { get; init; }
        public required JsonObject Data { get; init; } = [];
        public required long Version { get; init; }
    }
}
