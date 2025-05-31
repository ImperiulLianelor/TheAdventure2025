using System.Text.Json.Serialization;
using System.Linq; // Required for Linq
using System.Text.Json;

namespace TheAdventure.Models.Data;

public class TiledProperty
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = ""; // "bool", "int", "string", etc.

    [JsonPropertyName("value")]
    public JsonElement Value { get; set; } // Use JsonElement for flexibility
}

public class Tile
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("image")]
    public string Image { get; set; } = "";

    [JsonPropertyName("imageheight")]
    public int? ImageHeight { get; set; }

    [JsonPropertyName("imagewidth")]
    public int? ImageWidth { get; set; }

    // This will capture the array from Tiled's JSON
    [JsonPropertyName("properties")]
    public List<TiledProperty>? Properties { get; set; }

    // Helper properties to easily access IsSolid and IsDestructible
    [JsonIgnore] // Don't try to serialize these back if you ever save
    public bool IsSolid
    {
        get => GetBoolPropertyValue("isSolid");
        // No setter needed if only reading from Tiled JSON
    }

    [JsonIgnore]
    public bool IsDestructible
    {
        get => GetBoolPropertyValue("isDestructible");
    }

    private bool GetBoolPropertyValue(string propertyName, bool defaultValue = false)
    {
        if (Properties == null) return defaultValue;
        var prop = Properties.FirstOrDefault(p => p.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) && p.Type == "bool");
        if (prop != null && prop.Value.ValueKind == JsonValueKind.True) return true;
        if (prop != null && prop.Value.ValueKind == JsonValueKind.False) return false;
        return defaultValue;
    }

    [JsonIgnore]
    public int TextureId { get; set; } = -1;
}