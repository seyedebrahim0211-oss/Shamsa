using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shamsa.Desktop;

// A studio form can stay open while a note is typed, an icon is dropped or a window moves.
// Commit only edited fields, merging collection entries by their persistent Id.
internal static class DesktopStudioEdits
{
    internal static DesktopSettings Merge(DesktopSettings baseline, DesktopSettings edited, DesktopSettings live)
    {
        if (ReferenceEquals(baseline, live)) return edited.Clone();
        var options = DesktopSettingsStore.Json;
        var merged = MergeNode(JsonSerializer.SerializeToNode(baseline, options), JsonSerializer.SerializeToNode(edited, options),
            JsonSerializer.SerializeToNode(live, options));
        return merged!.Deserialize<DesktopSettings>(options)!;
    }

    private static JsonNode? MergeNode(JsonNode? before, JsonNode? edit, JsonNode? live)
    {
        if (JsonNode.DeepEquals(before, edit)) return live?.DeepClone();
        if (before is JsonObject old && edit is JsonObject changes && live?.DeepClone() is JsonObject output)
        {
            foreach (var key in old.Select(p => p.Key).Except(changes.Select(p => p.Key))) output.Remove(key);
            foreach (var (key, value) in changes)
                if (!old.ContainsKey(key) || !JsonNode.DeepEquals(old[key], value))
                    output[key] = MergeNode(old[key], value, output[key]);
            return output;
        }
        static bool HasUniqueIds(JsonArray values) => values.All(n => n is JsonObject o && o["Id"] is JsonValue v && v.TryGetValue<string>(out _)) &&
            values.Select(n => (string?)n!["Id"]).Distinct().Count() == values.Count;
        if (before is JsonArray a && edit is JsonArray b && live is JsonArray c && HasUniqueIds(a) && HasUniqueIds(b) && HasUniqueIds(c))
        {
            static string Id(JsonNode? n) => (string)n!["Id"]!;
            var oldItems = a.ToDictionary(Id);
            var liveItems = c.ToDictionary(Id);
            var seen = new HashSet<string>();
            var outputItems = new JsonArray();
            foreach (var node in b)
            {
                var id = Id(node);
                seen.Add(id);
                if (!oldItems.TryGetValue(id, out var original)) outputItems.Add(node?.DeepClone());
                else if (liveItems.TryGetValue(id, out var current)) outputItems.Add(MergeNode(original, node, current));
            }
            foreach (var node in c.Where(n => !seen.Contains(Id(n)) && !oldItems.ContainsKey(Id(n))))
                outputItems.Add(node?.DeepClone());
            return outputItems;
        }
        return edit?.DeepClone();
    }
}
