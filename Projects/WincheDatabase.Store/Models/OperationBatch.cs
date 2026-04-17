using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace WincheDatabase.Store.Models
{
    public sealed record OperationBatch
    {
        [Required]
        [JsonPropertyName("operations")]
        public required List<BatchOperation> Operations { get; init; } = [];
    }
}
