using System.Reflection;
using System.Text.Json;
using Silk.NET.Maths;
using TheAdventure.Models;
using TheAdventure.Models.Data;
using TheAdventure.Scripting;

namespace TheAdventure;

public class Engine
{

    private bool _wasBPressedLastFrame = false;

    private int _bombsAvailable = 5;
    private const int _maxBombs = 5;
    private double _timeSinceLastBombRecharge = 0;
    private const double _bombRechargeInterval = 10.0; // seconds

    private int _score = 0;

    private string _lastHudText = "";


    private bool _isGameOver = false;

    private readonly GameRenderer _renderer;
    private readonly Input _input;
    private readonly ScriptEngine _scriptEngine = new();

    private readonly Dictionary<int, GameObject> _gameObjects = new();
    private readonly Dictionary<string, TileSet> _loadedTileSets = new();
    private readonly Dictionary<int, Tile> _tileIdMap = new();

    private Level _currentLevel = new();
    private PlayerObject? _player;

    private DateTimeOffset _lastUpdate = DateTimeOffset.Now;

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
        var level = JsonSerializer.Deserialize<Level>(levelContent);
        if (level == null)
        {
            throw new Exception("Failed to load level");
        }

        foreach (var tileSetRef in level.TileSets)
        {
            var tileSetContent = File.ReadAllText(Path.Combine("Assets", tileSetRef.Source));
            var tileSet = JsonSerializer.Deserialize<TileSet>(tileSetContent);
            if (tileSet == null)
            {
                throw new Exception("Failed to load tile set");
            }

            foreach (var tile in tileSet.Tiles)
            {
                tile.TextureId = _renderer.LoadTexture(Path.Combine("Assets", tile.Image), out _);
                _tileIdMap.Add(tile.Id!.Value, tile);
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

        _currentLevel = level;

        _scriptEngine.LoadAll(Path.Combine("Assets", "Scripts"));
    }

    public void ProcessFrame()
{
    var currentTime = DateTimeOffset.Now;
    var secondsSinceLastFrame = (currentTime - _lastUpdate).TotalSeconds;
    _lastUpdate = currentTime;

    _timeSinceLastBombRecharge += secondsSinceLastFrame;

    if (_timeSinceLastBombRecharge >= _bombRechargeInterval && _bombsAvailable < _maxBombs)
    {
        _bombsAvailable++;
        _timeSinceLastBombRecharge = 0;
    }

    if (_player == null)
    {
        return;
    }

    if (_isGameOver)
    {
        if (_input.IsKeyRPressed())
        {
            ResetGame();
        }
        return;
    }

    double up = _input.IsUpPressed() ? 1.0 : 0.0;
    double down = _input.IsDownPressed() ? 1.0 : 0.0;
    double left = _input.IsLeftPressed() ? 1.0 : 0.0;
    double right = _input.IsRightPressed() ? 1.0 : 0.0;
    bool isAttacking = _input.IsKeyAPressed() && (up + down + left + right <= 1);
    bool isBPressed = _input.IsKeyBPressed();

    _player.UpdatePosition(up, down, left, right, 48, 48, secondsSinceLastFrame * 1000); // ms

    if (isAttacking)
    {
        _player.Attack();
    }

    if (_player.HP <= 0 && !_isGameOver)
    {
        _isGameOver = true;
        return;
    }

    _scriptEngine.ExecuteAll(this);

    if (isBPressed && !_wasBPressedLastFrame && _bombsAvailable > 0)
    {
        AddBomb(_player.Position.X, _player.Position.Y, false);
        _bombsAvailable--;
    }

    _wasBPressedLastFrame = isBPressed;

    if (_isGameOver && _input.IsKeyRPressed())
    {
        ResetGame();
    }
}

    private void ResetGame()
{
    _gameObjects.Clear();
    _tileIdMap.Clear();
    _loadedTileSets.Clear();
    _currentLevel = new Level();
    _player = null;
    _isGameOver = false;
    _score = 0;
    SetupWorld();
}
    private void DrawHUD()
{
    int secondsLeft = (int)Math.Ceiling(_bombRechargeInterval - _timeSinceLastBombRecharge);
    if (secondsLeft < 0) secondsLeft = 0;

    string hudText = $"Bombs: {_bombsAvailable} | Next bomb in: {secondsLeft}s";
    string hpText = $"HP: {_player?.HP ?? 0}";

    _renderer.DrawText(hudText, 10, 10);
    _renderer.DrawText(hpText, 10, 30);

    if (_isGameOver)
    {
        var windowSize = _renderer.WindowSize; // trebuie să adaugi această proprietate în GameRenderer.cs
        var message = "Press R to retry";

        int textWidth = 200;
        int textHeight = 50;
        int centerX = (windowSize.Width - textWidth) / 2;
        int centerY = (windowSize.Height - textHeight) / 2;

        _renderer.DrawText(message, centerX, centerY, 24); // font mai mare
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
        DrawHUD();


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

            if (_player == null)
            {
                continue;
            }

            var tempGameObject = (TemporaryGameObject)gameObject!;
            var deltaX = Math.Abs(_player.Position.X - tempGameObject.Position.X);
            var deltaY = Math.Abs(_player.Position.Y - tempGameObject.Position.Y);
            if (deltaX < 32 && deltaY < 32)
            {
                _player.TakeDamage(10);
            }
        }

        _player?.Render(_renderer);
    }

    public void RenderTerrain()
    {
        foreach (var currentLayer in _currentLevel.Layers)
        {
            for (int i = 0; i < _currentLevel.Width; ++i)
            {
                for (int j = 0; j < _currentLevel.Height; ++j)
                {
                    int? dataIndex = j * currentLayer.Width + i;
                    if (dataIndex == null)
                    {
                        continue;
                    }

                    var currentTileId = currentLayer.Data[dataIndex.Value] - 1;
                    if (currentTileId == null)
                    {
                        continue;
                    }

                    var currentTile = _tileIdMap[currentTileId.Value];

                    var tileWidth = currentTile.ImageWidth ?? 0;
                    var tileHeight = currentTile.ImageHeight ?? 0;

                    var sourceRect = new Rectangle<int>(0, 0, tileWidth, tileHeight);
                    var destRect = new Rectangle<int>(i * tileWidth, j * tileHeight, tileWidth, tileHeight);
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