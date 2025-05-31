using System.Reflection;
using System.Text.Json;
using Silk.NET.Maths;
using TheAdventure.Models;
using TheAdventure.Models.Data;
using TheAdventure.Scripting;
using System.Linq;

namespace TheAdventure;

public class Engine
{
    private readonly GameRenderer _renderer;
    private readonly Input _input;
    private readonly ScriptEngine _scriptEngine = new();

    private readonly Dictionary<int, GameObject> _gameObjects = new();
    private readonly Dictionary<string, TileSet> _loadedTileSets = new();
    private readonly Dictionary<int, Tile> _tileIdMap = new(); // Keyed by GID

    private Level _currentLevel = new();
    private PlayerObject? _player;

    private DateTimeOffset _lastUpdate = DateTimeOffset.Now;
    private int _collisionLayerIndex = -1;

    public Engine(GameRenderer renderer, Input input)
    {
        _renderer = renderer;
        _input = input;

        // MODIFIED: Make mouse click use PlayerPlaceBomb with translateCoordinates = true
        _input.OnMouseClick += (_, coords) => PlayerPlaceBomb(coords.x, coords.y, true); 
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
        _currentLevel = level;

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
                if (tile.Id == null) continue;

                tile.TextureId = _renderer.LoadTexture(Path.Combine("Assets", tile.Image), out _);
                int globalTileId = tileSetRef.FirstGID.Value + tile.Id.Value;
                
                if (!_tileIdMap.TryAdd(globalTileId, tile))
                {
                    Console.WriteLine($"Warning: Failed to add tile with GID {globalTileId} from {tileSetRef.Source} to _tileIdMap.");
                }
            }
            _loadedTileSets.Add(tileSet.Name, tileSet);
        }

        if (level.Width == null || level.Height == null || level.TileWidth == null || level.TileHeight == null)
        {
            throw new Exception("Invalid level or tile dimensions");
        }

        _renderer.SetWorldBounds(new Rectangle<int>(0, 0, level.Width.Value * level.TileWidth.Value,
            level.Height.Value * level.TileHeight.Value));
        
        InitializeCollisionLayer();
        _scriptEngine.LoadAll(Path.Combine("Assets", "Scripts"));
    }

    private void InitializeCollisionLayer()
    {
        if (_currentLevel?.Layers == null)
        {
            _collisionLayerIndex = -1;
            Console.WriteLine("WARNING: Level or layers not loaded. Cannot initialize collision layer.");
            return;
        }

        _collisionLayerIndex = _currentLevel.Layers.FindIndex(l => 
            l.Name.Equals("CollisionLayer", StringComparison.OrdinalIgnoreCase) && 
            l.Type.Equals("tilelayer", StringComparison.OrdinalIgnoreCase));

        if (_collisionLayerIndex == -1)
        {
            _collisionLayerIndex = _currentLevel.Layers.FindIndex(l => 
                l.Type.Equals("tilelayer", StringComparison.OrdinalIgnoreCase) && 
                l.Visible.GetValueOrDefault(true));
            if (_collisionLayerIndex != -1)
            {
                Console.WriteLine($"INFO: Collision layer 'CollisionLayer' not found. Using first visible tile layer '{_currentLevel.Layers[_collisionLayerIndex].Name}' as collision layer.");
            }
        }
        
        if (_collisionLayerIndex == -1)
        {
            Console.WriteLine("WARNING: No suitable collision layer found. Tile-based collision/destruction will not work.");
        }
        else
        {
            Console.WriteLine($"INFO: Using layer '{_currentLevel.Layers[_collisionLayerIndex].Name}' (index {_collisionLayerIndex}) for collision/destruction.");
        }
    }

    private Tile? GetTileFromCollisionLayer(int tileX, int tileY)
    {
        if (_collisionLayerIndex == -1 || _currentLevel?.Layers == null || _collisionLayerIndex >= _currentLevel.Layers.Count)
            return null;

        Layer collisionLayer = _currentLevel.Layers[_collisionLayerIndex];
        if (tileX < 0 || tileX >= collisionLayer.Width.GetValueOrDefault() || 
            tileY < 0 || tileY >= collisionLayer.Height.GetValueOrDefault())
            return null;

        int dataIndex = tileY * collisionLayer.Width.GetValueOrDefault() + tileX;
        if (dataIndex < 0 || dataIndex >= collisionLayer.Data.Count || collisionLayer.Data[dataIndex] == null)
            return null;

        int gid = collisionLayer.Data[dataIndex]!.Value;
        if (gid == 0) return null;

        _tileIdMap.TryGetValue(gid, out Tile? tile);
        return tile;
    }

    public bool CheckTileCollision(Rectangle<int> worldBoundingBox)
    {
        if (_collisionLayerIndex == -1 || 
            _currentLevel.TileWidth == null || _currentLevel.TileHeight == null)
        {
            return false; 
        }

        int tileWidth = _currentLevel.TileWidth.Value;
        int tileHeight = _currentLevel.TileHeight.Value;

        // MODIFIED: Use .Origin.X and .Origin.Y to avoid compiler confusion with .Min
        int startTileX = worldBoundingBox.Origin.X / tileWidth;    // Was worldBoundingBox.Min.X
        int endTileX   = (worldBoundingBox.Max.X - 1) / tileWidth; 
        int startTileY = worldBoundingBox.Origin.Y / tileHeight;    // Was worldBoundingBox.Min.Y
        int endTileY   = (worldBoundingBox.Max.Y - 1) / tileHeight;

        for (int ty = startTileY; ty <= endTileY; ++ty)
        {
            for (int tx = startTileX; tx <= endTileX; ++tx)
            {
                Tile? tile = GetTileFromCollisionLayer(tx, ty);
                if (tile != null && tile.IsSolid) 
                {
                    return true;
                }
            }
        }
        return false;
    }

    public void ProcessFrame()
    {
        var currentTime = DateTimeOffset.Now;
        var msSinceLastFrame = (currentTime - _lastUpdate).TotalMilliseconds;
        _lastUpdate = currentTime;

        if (_player == null) return;

        double up = _input.IsUpPressed() ? 1.0 : 0.0;
        double down = _input.IsDownPressed() ? 1.0 : 0.0;
        double left = _input.IsLeftPressed() ? 1.0 : 0.0;
        double right = _input.IsRightPressed() ? 1.0 : 0.0;
        bool isAttacking = _input.IsKeyAPressed() && (up + down + left + right <= 1);
        bool addBombInput = _input.IsKeyBPressed();

        _player.UpdatePosition(up, down, left, right, msSinceLastFrame, this); 
        
        if (isAttacking)
        {
            _player.Attack();
        }
        
        _scriptEngine.ExecuteAll(this);

        if (addBombInput)
        {
            PlayerPlaceBomb(_player.Position.X, _player.Position.Y, false); // Player pos is already world coords
        }

        // --- BOMB DETONATION LOGIC ---
        List<TemporaryGameObject> bombsToProcess = _gameObjects.Values
                                                   .OfType<TemporaryGameObject>() 
                                                   .Where(b => b.ShouldApplyDetonationEffect())
                                                   .ToList();
        
        foreach (var bomb in bombsToProcess)
        {
            DestroyTilesInRadius(bomb.Position, bomb.BlastRadius);

            if (_player != null && !_player.CurrentVisualState.State.Equals(PlayerObject.PlayerState.GameOver))
            {
                var playerBox = _player.GetWorldBoundingBox();
                // CORRECTED: Calculate player center manually
                int playerCenterX = playerBox.Origin.X + playerBox.Size.X / 2;
                int playerCenterY = playerBox.Origin.Y + playerBox.Size.Y / 2;
                var playerCenterVec = new Vector2D<int>(playerCenterX, playerCenterY);

                var distanceSq = Vector2D.DistanceSquared(
                                     playerCenterVec, 
                                     new Vector2D<int>(bomb.Position.X, bomb.Position.Y)
                                 );
                // Cast to long for the squared radius comparison to avoid overflow if blastRadius is large
                if (distanceSq <= (long)bomb.BlastRadius * bomb.BlastRadius)
                {
                     _player.GameOver(); 
                }
            }
            
            bomb.MarkEffectTriggered();
        }
    }

    private void DestroyTilesInRadius((int X, int Y) bombCenter, int blastRadius)
    {
        if (_currentLevel.TileWidth == null || _currentLevel.TileHeight == null || _collisionLayerIndex == -1) return;

        int tileWidth = _currentLevel.TileWidth.Value;
        int tileHeight = _currentLevel.TileHeight.Value;

        int minWorldX = bombCenter.X - blastRadius;
        int maxWorldX = bombCenter.X + blastRadius;
        int minWorldY = bombCenter.Y - blastRadius;
        int maxWorldY = bombCenter.Y + blastRadius;

        int startTileX = minWorldX / tileWidth;
        int endTileX = maxWorldX / tileWidth;
        int startTileY = minWorldY / tileHeight;
        int endTileY = maxWorldY / tileHeight;
        
        Layer collisionLayer = _currentLevel.Layers[_collisionLayerIndex];

        for (int ty = startTileY; ty <= endTileY; ++ty)
        {
            for (int tx = startTileX; tx <= endTileX; ++tx)
            {
                int tileWorldCenterX = tx * tileWidth + tileWidth / 2;
                int tileWorldCenterY = ty * tileHeight + tileHeight / 2;
                
                long distSq = (long)(tileWorldCenterX - bombCenter.X) * (tileWorldCenterX - bombCenter.X) +
                              (long)(tileWorldCenterY - bombCenter.Y) * (tileWorldCenterY - bombCenter.Y);

                if (distSq <= (long)blastRadius * blastRadius)
                {
                    Tile? gameTile = GetTileFromCollisionLayer(tx, ty);
                    // Assumes Tile.cs has IsDestructible as a direct or helper property
                    if (gameTile != null && gameTile.IsDestructible) 
                    {
                        if (tx >= 0 && tx < collisionLayer.Width.GetValueOrDefault() &&
                            ty >= 0 && ty < collisionLayer.Height.GetValueOrDefault())
                        {
                            int dataIndex = ty * collisionLayer.Width.GetValueOrDefault() + tx;
                            if (dataIndex >= 0 && dataIndex < collisionLayer.Data.Count)
                            {
                                collisionLayer.Data[dataIndex] = 0;
                                // Console.WriteLine($"Destroyed tile at ({tx},{ty})");
                            }
                        }
                    }
                }
            }
        }
    }

    public void RenderFrame()
    {
        _renderer.SetDrawColor(0, 0, 0, 255);
        _renderer.ClearScreen();

        if (_player != null)
        {
            _renderer.CameraLookAt(_player.Position.X, _player.Position.Y);
        }

        RenderTerrain();
        RenderAllObjects();

        _renderer.PresentFrame();
    }

    public void RenderAllObjects()
    {
        var toRemove = new List<int>();
        foreach (var gameObject in _gameObjects.Values.ToList()) 
        {
            if (gameObject is RenderableGameObject renderable)
            {
                renderable.Render(_renderer);

                if (renderable is TemporaryGameObject bomb) 
                {
                    if (bomb.IsExploding) 
                    {
                        var bombCenter = bomb.Position;
                        var radius = bomb.BlastRadius;
                        _renderer.DrawWorldFilledCircle(bombCenter.X, bombCenter.Y, radius, 255, 100, 0, 100); 
                    }

                    if (bomb.IsExpired) 
                    {
                        toRemove.Add(bomb.Id);
                    }
                }
            }
        }

        foreach (var id in toRemove)
        {
            _gameObjects.Remove(id, out var _);
        }

        _player?.Render(_renderer);
    }

    public void RenderTerrain()
    {
        if (_currentLevel?.Layers == null || _currentLevel.TileWidth == null || _currentLevel.TileHeight == null) return;

        foreach (var currentLayer in _currentLevel.Layers)
        {
            if (!currentLayer.Type.Equals("tilelayer", StringComparison.OrdinalIgnoreCase) ||
                !currentLayer.Visible.GetValueOrDefault(true))
            {
                continue;
            }
            if (currentLayer.Width == null || currentLayer.Height == null) continue;

            for (int i = 0; i < currentLayer.Width.Value; ++i)
            {
                for (int j = 0; j < currentLayer.Height.Value; ++j)
                {
                    int dataIndex = j * currentLayer.Width.Value + i;
                    if (dataIndex < 0 || dataIndex >= currentLayer.Data.Count || currentLayer.Data[dataIndex] == null) continue;

                    var gid = currentLayer.Data[dataIndex]!.Value;
                    if (gid == 0) continue;

                    if (!_tileIdMap.TryGetValue(gid, out var currentTile)) continue;
                    
                    var tileImageWidth = currentTile.ImageWidth ?? _currentLevel.TileWidth.Value;
                    var tileImageHeight = currentTile.ImageHeight ?? _currentLevel.TileHeight.Value;

                    var sourceRect = new Rectangle<int>(0, 0, tileImageWidth, tileImageHeight);
                    var destRect = new Rectangle<int>(
                        i * _currentLevel.TileWidth.Value, 
                        j * _currentLevel.TileHeight.Value, 
                        _currentLevel.TileWidth.Value, 
                        _currentLevel.TileHeight.Value
                    );
                    _renderer.RenderTexture(currentTile.TextureId, sourceRect, destRect);
                }
            }
        }
    }

    public IEnumerable<RenderableGameObject> GetRenderables()
    {
        return _gameObjects.Values.OfType<RenderableGameObject>();
    }

    public (int X, int Y) GetPlayerPosition()
    {
        if (_player == null) return (0,0);
        return _player.Position;
    }

    // Generic AddBomb method
    public void AddBomb(int x, int y, double ttl, double detonationTime, int blastRadius, bool translateCoordinates = true)
    {
        var worldCoords = translateCoordinates ? _renderer.ToWorldCoordinates(x, y) : new Vector2D<int>(x, y);

        SpriteSheet spriteSheet = SpriteSheet.Load(_renderer, "BombExploding.json", "Assets");
        spriteSheet.ActivateAnimation("Explode");

        TemporaryGameObject bomb = new(spriteSheet, 
                                   ttl, 
                                   (worldCoords.X, worldCoords.Y),
                                   detonationTime,
                                   blastRadius);
        _gameObjects.Add(bomb.Id, bomb);
    }

    // Specific method for when the player places a bomb (e.g., via key press)
    public void PlayerPlaceBomb(int x, int y, bool translateCoordinates = true) 
    {
        double totalVisualDuration = 2.1; 
        double fuseDuration = 1.5;        
        int bombBlastRadius = 48;     

        AddBomb(x, y, totalVisualDuration, fuseDuration, bombBlastRadius, translateCoordinates);
    }
}