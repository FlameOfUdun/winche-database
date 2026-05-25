using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Winche.Database.Models
{
    public sealed record OperationBatch
    {
        [Required]
        [JsonPropertyName("operations")]
        public required List<BatchOperation> Operations { get; init; } = [];
    }
}
