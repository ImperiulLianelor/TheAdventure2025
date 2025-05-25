using System.Reflection;
using System.Text.Json;
using Silk.NET.Maths;
using TheAdventure.Models;
using TheAdventure.Models.Data;
using TheAdventure.Scripting;

namespace TheAdventure;

public class Engine
{
    private readonly GameRenderer _renderer;
    private readonly Input _input;
    private readonly ScriptEngine _scriptEngine = new();

    private readonly Dictionary<int, GameObject> _gameObjects = new();
    private readonly Dictionary<string, TileSet> _loadedTileSets = new();
    // MODIFIED: _tileIdMap now stores Tile objects (which include IsSolid) keyed by GID
    private readonly Dictionary<int, Tile> _tileIdMap = new();

    private Level _currentLevel = new();
    private PlayerObject? _player;

    private DateTimeOffset _lastUpdate = DateTimeOffset.Now;

    // ADDED: Index of the layer used for collision detection.
    private int _collisionLayerIndex = -1;

    public Engine(GameRenderer renderer, Input input)
    {
        _renderer = renderer;
        _input = input;

        _input.OnMouseClick += (_, coords) => AddBomb(coords.x, coords.y);
    }

    public void SetupWorld()
    {
        _player = new(SpriteSheet.Load(_renderer, "Player.json", "Assets"), 100, 100);

        var levelContent = File.ReadAllText(Path.Combine("Assets", "terrain.tmj"));
        var level = JsonSerializer.Deserialize<Level>(levelContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (level == null)
        {
            throw new Exception("Failed to load level");
        }
        _currentLevel = level; // Assign _currentLevel earlier

        // MODIFIED: Clear _tileIdMap for potential re-setups
        _tileIdMap.Clear(); 
        _loadedTileSets.Clear();

        foreach (var tileSetRef in level.TileSets)
        {
            var tileSetContent = File.ReadAllText(Path.Combine("Assets", tileSetRef.Source));
            var tileSet = JsonSerializer.Deserialize<TileSet>(tileSetContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (tileSet == null)
            {
                throw new Exception($"Failed to load tile set from {tileSetRef.Source}");
            }

            if (tileSetRef.FirstGID == null)
            {
                Console.WriteLine($"Warning: TileSet {tileSetRef.Source} has no FirstGID. Skipping.");
                continue;
            }

            foreach (var tile in tileSet.Tiles)
            {
                if (tile.Id == null) continue; // Tile.Id is the local ID within the tileset

                tile.TextureId = _renderer.LoadTexture(Path.Combine("Assets", tile.Image), out _);
                
                // Calculate the Global ID (GID) for this tile as used in map layer data.
                // GID in layer data = FirstGID of tileset + local ID of tile within tileset.
                int globalTileId = tileSetRef.FirstGID.Value + tile.Id.Value;
                
                if (!_tileIdMap.TryAdd(globalTileId, tile))
                {
                    Console.WriteLine($"Warning: Failed to add tile with GID {globalTileId} (local ID {tile.Id.Value} from {tileSetRef.Source}) to _tileIdMap. It might already exist or GID calculation is off.");
                }
            }
            _loadedTileSets.Add(tileSet.Name, tileSet);
        }

        if (level.Width == null || level.Height == null)
        {
            throw new Exception("Invalid level dimensions");
        }

        if (level.TileWidth == null || level.TileHeight == null)
        {
            throw new Exception("Invalid tile dimensions");
        }

        _renderer.SetWorldBounds(new Rectangle<int>(0, 0, level.Width.Value * level.TileWidth.Value,
            level.Height.Value * level.TileHeight.Value));
        
        // ADDED: Initialize the collision layer index
        InitializeCollisionLayer();

        _scriptEngine.LoadAll(Path.Combine("Assets", "Scripts"));
    }

    // ADDED: Method to find and store the index of the collision layer.
    private void InitializeCollisionLayer()
    {
        if (_currentLevel?.Layers == null)
        {
            _collisionLayerIndex = -1;
            Console.WriteLine("WARNING: Level or layers not loaded. Cannot initialize collision layer.");
            return;
        }

        // Option 1: Find by a specific name (e.g., "CollisionLayer") - Recommended
        _collisionLayerIndex = _currentLevel.Layers.FindIndex(l => 
            l.Name.Equals("CollisionLayer", StringComparison.OrdinalIgnoreCase) && 
            l.Type.Equals("tilelayer", StringComparison.OrdinalIgnoreCase));

        // Option 2: Fallback to the first visible tile layer if not found by name (less reliable)
        if (_collisionLayerIndex == -1)
        {
            _collisionLayerIndex = _currentLevel.Layers.FindIndex(l => 
                l.Type.Equals("tilelayer", StringComparison.OrdinalIgnoreCase) && 
                l.Visible.GetValueOrDefault(true));
            if (_collisionLayerIndex != -1)
            {
                Console.WriteLine($"WARNING: Collision layer 'CollisionLayer' not found. Using first visible tile layer '{_currentLevel.Layers[_collisionLayerIndex].Name}' as collision layer.");
            }
        }
        
        if (_collisionLayerIndex == -1)
        {
            Console.WriteLine("WARNING: No suitable collision layer found. Tile-based collision detection will not work.");
        }
        else
        {
            Console.WriteLine($"Using layer '{_currentLevel.Layers[_collisionLayerIndex].Name}' (index {_collisionLayerIndex}) for collision.");
        }
    }

    // ADDED: Helper to get a Tile object from the designated collision layer at specific tile coordinates.
    private Tile? GetTileFromCollisionLayer(int tileX, int tileY)
    {
        if (_collisionLayerIndex == -1 || _currentLevel?.Layers == null || 
            _collisionLayerIndex >= _currentLevel.Layers.Count)
        {
            return null; // Collision layer not identified or invalid
        }

        Layer collisionLayer = _currentLevel.Layers[_collisionLayerIndex];

        if (tileX < 0 || tileX >= collisionLayer.Width.GetValueOrDefault() || 
            tileY < 0 || tileY >= collisionLayer.Height.GetValueOrDefault())
        {
            return null; // Coordinates are outside the layer bounds
        }

        int dataIndex = tileY * collisionLayer.Width.GetValueOrDefault() + tileX;
        if (dataIndex < 0 || dataIndex >= collisionLayer.Data.Count || collisionLayer.Data[dataIndex] == null)
        {
            return null; // Index out of bounds for data array or null GID
        }

        int gid = collisionLayer.Data[dataIndex]!.Value; // GID from the layer data
        if (gid == 0) // GID 0 means empty tile, no collision
        {
            return null;
        }

        // _tileIdMap is now keyed by GID
        if (_tileIdMap.TryGetValue(gid, out Tile? tile))
        {
            return tile;
        }
        // Console.WriteLine($"Warning: Tile with GID {gid} not found in _tileIdMap.");
        return null; // Tile definition not found for this GID
    }

    // ADDED: Main collision check method for a given world bounding box.
    public bool CheckTileCollision(Rectangle<int> worldBoundingBox)
    {
        if (_collisionLayerIndex == -1 || 
            _currentLevel.TileWidth == null || _currentLevel.TileHeight == null)
        {
            return false; // Collision system not ready or tile dimensions unknown
        }

        int tileWidth = _currentLevel.TileWidth.Value;
        int tileHeight = _currentLevel.TileHeight.Value;

        // Determine the range of tiles the bounding box could overlap.
        // Min X/Y of the bounding box corresponds to its top-left point.
        // Max X/Y is exclusive (right/bottom edge), so subtract 1 for last inclusive tile.
        int startTileX = worldBoundingBox.Min.X / tileWidth;
        int endTileX   = (worldBoundingBox.Max.X - 1) / tileWidth; 
        int startTileY = worldBoundingBox.Min.Y / tileHeight;
        int endTileY   = (worldBoundingBox.Max.Y - 1) / tileHeight;

        for (int ty = startTileY; ty <= endTileY; ++ty)
        {
            for (int tx = startTileX; tx <= endTileX; ++tx)
            {
                Tile? tile = GetTileFromCollisionLayer(tx, ty);
                if (tile != null && tile.IsSolid)
                {
                    // For pure tile-based collision, simple overlap with a solid tile's grid cell is enough.
                    // If more precise collision (e.g. with tile's specific bounding box if smaller than cell)
                    // is needed later, this is where it would be:
                    // var tileWorldRect = new Rectangle<int>(tx * tileWidth, ty * tileHeight, tileWidth, tileHeight);
                    // if (worldBoundingBox.Intersects(tileWorldRect)) return true;
                    return true; // Found a solid tile overlapped by the bounding box
                }
            }
        }
        return false; // No collision with solid tiles in the checked range
    }

    public void ProcessFrame()
    {
        var currentTime = DateTimeOffset.Now;
        var msSinceLastFrame = (currentTime - _lastUpdate).TotalMilliseconds;
        _lastUpdate = currentTime;

        if (_player == null)
        {
            return;
        }

        double up = _input.IsUpPressed() ? 1.0 : 0.0;
        double down = _input.IsDownPressed() ? 1.0 : 0.0;
        double left = _input.IsLeftPressed() ? 1.0 : 0.0;
        double right = _input.IsRightPressed() ? 1.0 : 0.0;
        bool isAttacking = _input.IsKeyAPressed() && (up + down + left + right <= 1);
        bool addBomb = _input.IsKeyBPressed();

        // MODIFIED: Pass 'this' (Engine instance) to PlayerObject.UpdatePosition
        // Also removed the unused width and height parameters (48, 48)
        _player.UpdatePosition(up, down, left, right, msSinceLastFrame, this); 
        
        if (isAttacking)
        {
            _player.Attack();
        }
        
        _scriptEngine.ExecuteAll(this);

        if (addBomb)
        {
            AddBomb(_player.Position.X, _player.Position.Y, false);
        }
    }

    public void RenderFrame()
    {
        _renderer.SetDrawColor(0, 0, 0, 255);
        _renderer.ClearScreen();

        var playerPosition = _player!.Position;
        _renderer.CameraLookAt(playerPosition.X, playerPosition.Y);

        RenderTerrain();
        RenderAllObjects();

        _renderer.PresentFrame();
    }

    public void RenderAllObjects()
    {
        var toRemove = new List<int>();
        foreach (var gameObject in GetRenderables())
        {
            gameObject.Render(_renderer);
            if (gameObject is TemporaryGameObject { IsExpired: true } tempGameObject)
            {
                toRemove.Add(tempGameObject.Id);
            }
        }

        foreach (var id in toRemove)
        {
            _gameObjects.Remove(id, out var gameObject);

            if (_player == null || gameObject == null) // ADDED: Null check for gameObject
            {
                continue;
            }

            var tempGameObject = (TemporaryGameObject)gameObject; // Safe cast due to previous check
            var deltaX = Math.Abs(_player.Position.X - tempGameObject.Position.X);
            var deltaY = Math.Abs(_player.Position.Y - tempGameObject.Position.Y);
            if (deltaX < 32 && deltaY < 32) // This is a simple proximity check for bomb damage
            {
                _player.GameOver();
            }
        }

        _player?.Render(_renderer);
    }

    public void RenderTerrain()
    {
        if (_currentLevel?.Layers == null || _currentLevel.TileWidth == null || _currentLevel.TileHeight == null) return;

        foreach (var currentLayer in _currentLevel.Layers)
        {
            // Only render tile layers
            if (!currentLayer.Type.Equals("tilelayer", StringComparison.OrdinalIgnoreCase) ||
                !currentLayer.Visible.GetValueOrDefault(true))
            {
                continue;
            }

            if (currentLayer.Width == null || currentLayer.Height == null) continue;

            for (int i = 0; i < currentLayer.Width.Value; ++i) // Use Width from layer
            {
                for (int j = 0; j < currentLayer.Height.Value; ++j) // Use Height from layer
                {
                    int dataIndex = j * currentLayer.Width.Value + i;
                    if (dataIndex < 0 || dataIndex >= currentLayer.Data.Count || currentLayer.Data[dataIndex] == null)
                    {
                        continue;
                    }

                    var gid = currentLayer.Data[dataIndex]!.Value;
                    if (gid == 0) continue; // GID 0 is an empty tile

                    if (!_tileIdMap.TryGetValue(gid, out var currentTile))
                    {
                        // This might happen if GID from map data doesn't match any loaded tile
                        // Console.WriteLine($"Tile with GID {gid} not found for rendering at {i},{j}");
                        continue;
                    }
                    
                    var tileWidth = currentTile.ImageWidth ?? _currentLevel.TileWidth.Value;
                    var tileHeight = currentTile.ImageHeight ?? _currentLevel.TileHeight.Value;

                    var sourceRect = new Rectangle<int>(0, 0, tileWidth, tileHeight);
                    // Destination uses map's tilewidth/height for grid placement
                    var destRect = new Rectangle<int>(i * _currentLevel.TileWidth.Value, 
                                                    j * _currentLevel.TileHeight.Value, 
                                                    _currentLevel.TileWidth.Value, 
                                                    _currentLevel.TileHeight.Value);
                    _renderer.RenderTexture(currentTile.TextureId, sourceRect, destRect);
                }
            }
        }
    }

    public IEnumerable<RenderableGameObject> GetRenderables()
    {
        foreach (var gameObject in _gameObjects.Values)
        {
            if (gameObject is RenderableGameObject renderableGameObject)
            {
                yield return renderableGameObject;
            }
        }
    }

    public (int X, int Y) GetPlayerPosition()
    {
        return _player!.Position;
    }

    public void AddBomb(int X, int Y, bool translateCoordinates = true)
    {
        var worldCoords = translateCoordinates ? _renderer.ToWorldCoordinates(X, Y) : new Vector2D<int>(X, Y);

        SpriteSheet spriteSheet = SpriteSheet.Load(_renderer, "BombExploding.json", "Assets");
        spriteSheet.ActivateAnimation("Explode");

        TemporaryGameObject bomb = new(spriteSheet, 2.1, (worldCoords.X, worldCoords.Y));
        _gameObjects.Add(bomb.Id, bomb);
    }
}