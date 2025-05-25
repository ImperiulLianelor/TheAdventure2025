using System.Text.Json.Serialization;

namespace TheAdventure.Models.Data;

public class Tile
{
    [JsonPropertyName("id")]
    public int? Id { get; set; } // This is the local ID within the tileset (0-indexed)

    [JsonPropertyName("image")]
    public string Image { get; set; } = "";

    [JsonPropertyName("imageheight")]
    public int? ImageHeight { get; set; }

    [JsonPropertyName("imagewidth")]
    public int? ImageWidth { get; set; }

    // ADDED: Property to indicate if a tile is solid for collision
    // Ensure your tileset JSON has a custom property named "isSolid" (boolean) for tiles.
    [JsonPropertyName("isSolid")]
    public bool IsSolid { get; set; } = false; // Default to false (non-solid)

    [JsonIgnore]
    public int TextureId { get; set; } = -1;
}