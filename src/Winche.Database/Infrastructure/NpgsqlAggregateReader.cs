using Npgsql;
using System.Text.Json.Nodes;
using Winche.Database.Models;

namespace Winche.Database.Infrastructure
{
    internal static class NpgsqlAggregateReader
    {
        private static Dictionary<string, JsonNode?> ReadRow(NpgsqlDataReader reader)
        {
            var fields = new Dictionary<string, JsonNode?>(
                reader.FieldCount,
                StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (reader.IsDBNull(i))
                {
                    fields[reader.GetName(i)] = null;
                    continue;
                }

                var raw = reader.GetValue(i)?.ToString() ?? string.Empty;

                JsonNode? node;
                try { node = JsonNode.Parse(raw); }
                catch { node = JsonValue.Create(raw); }

                fields[reader.GetName(i)] = node;
            }

            return fields;
        }

        internal static async Task<AggregateResult> ReadAsync(
            NpgsqlDataReader reader, CancellationToken ct)
        {
            var rows = new List<IReadOnlyDictionary<string, JsonNode?>>();

            while (await reader.ReadAsync(ct))
                rows.Add(ReadRow(reader));

            return new AggregateResult(rows);
        }
    }
}
