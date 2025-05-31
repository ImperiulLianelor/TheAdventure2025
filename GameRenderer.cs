using Silk.NET.Maths;
using Silk.NET.SDL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TheAdventure.Models;
using Point = Silk.NET.SDL.Point; // Existing alias

namespace TheAdventure;

public unsafe class GameRenderer
{
    private Sdl _sdl;
    private Renderer* _renderer;
    private GameWindow _window;
    private Camera _camera;

    private Dictionary<int, IntPtr> _texturePointers = new();
    private Dictionary<int, TextureData> _textureData = new();
    private int _textureId;

    public GameRenderer(Sdl sdl, GameWindow window)
    {
        _sdl = sdl;
        
        _renderer = (Renderer*)window.CreateRenderer();
        // Ensure blend mode is set for transparency effects (like the blast radius)
        _sdl.SetRenderDrawBlendMode(_renderer, BlendMode.Blend); 
        
        _window = window;
        var windowSize = window.Size;
        _camera = new Camera(windowSize.Width, windowSize.Height);
    }

    public void SetWorldBounds(Rectangle<int> bounds)
    {
        _camera.SetWorldBounds(bounds);
    }

    public void CameraLookAt(int x, int y)
    {
        _camera.LookAt(x, y);
    }

    public int LoadTexture(string fileName, out TextureData textureInfo)
    {
        using (var fStream = new FileStream(fileName, FileMode.Open))
        {
            var image = Image.Load<Rgba32>(fStream);
            textureInfo = new TextureData()
            {
                Width = image.Width,
                Height = image.Height
            };
            var imageRAWData = new byte[textureInfo.Width * textureInfo.Height * 4];
            image.CopyPixelDataTo(imageRAWData.AsSpan());
            fixed (byte* data = imageRAWData)
            {
                // SDL uses pitch (bytes per row), which is width * bytesPerPixel
                var imageSurface = _sdl.CreateRGBSurfaceWithFormatFrom(data, textureInfo.Width,
                    textureInfo.Height, 32, textureInfo.Width * 4, (uint)PixelFormatEnum.Rgba32); // Assuming 32 bpp (RGBA)
                if (imageSurface == null)
                {
                    throw new Exception($"Failed to create surface from image data: {_sdl.GetErrorS()}");
                }
                
                var imageTexture = _sdl.CreateTextureFromSurface(_renderer, imageSurface);
                if (imageTexture == null)
                {
                    _sdl.FreeSurface(imageSurface);
                    throw new Exception($"Failed to create texture from surface: {_sdl.GetErrorS()}");
                }
                
                _sdl.FreeSurface(imageSurface); // Free the surface after creating the texture
                
                _textureData[_textureId] = textureInfo;
                _texturePointers[_textureId] = (IntPtr)imageTexture;
            }
        }

        return _textureId++;
    }

    public void RenderTexture(int textureId, Rectangle<int> src, Rectangle<int> dst,
        RendererFlip flip = RendererFlip.None, double angle = 0.0, Point center = default)
    {
        if (_texturePointers.TryGetValue(textureId, out var imageTexture))
        {
            // Convert destination rectangle from world to screen coordinates
            var translatedDst = _camera.ToScreenCoordinates(dst); 
            
            // SDL_RenderCopyEx expects SDL_Rect, which Rectangle<int> from Silk.NET.Maths should be compatible with
            // when passed by 'in' or as a pointer.
            _sdl.RenderCopyEx(_renderer, (Texture*)imageTexture, &src, // Pass src by pointer
                &translatedDst, // Pass translatedDst by pointer
                angle,
                &center, // Pass center by pointer
                flip);
        }
    }

    public Vector2D<int> ToWorldCoordinates(int x, int y)
    {
        return _camera.ToWorldCoordinates(new Vector2D<int>(x, y));
    }

    public void SetDrawColor(byte r, byte g, byte b, byte a)
    {
        _sdl.SetRenderDrawColor(_renderer, r, g, b, a);
    }

    public void ClearScreen()
    {
        _sdl.RenderClear(_renderer);
    }

    public void PresentFrame()
    {
        _sdl.RenderPresent(_renderer);
    }

    // --- ADDED METHODS for Primitive Drawing ---

    /// <summary>
    /// Draws a rectangle outline in world coordinates.
    /// The rectangle's position will be translated by the camera.
    /// </summary>
    public void DrawWorldRectangleOutline(Rectangle<int> worldRect, byte r, byte g, byte b, byte a)
    {
        var screenRect = _camera.ToScreenCoordinates(worldRect);
        SetDrawColor(r, g, b, a);
        // SDL_RenderDrawRect expects a pointer to an SDL_Rect.
        // Silk.NET.Maths.Rectangle<int> should be layout-compatible.
        _sdl.RenderDrawRect(_renderer, &screenRect); 
    }

    /// <summary>
    /// Draws a filled rectangle in world coordinates.
    /// The rectangle's position will be translated by the camera.
    /// </summary>
    public void DrawWorldFilledRectangle(Rectangle<int> worldRect, byte r, byte g, byte b, byte a)
    {
        var screenRect = _camera.ToScreenCoordinates(worldRect);
        SetDrawColor(r, g, b, a);
        _sdl.RenderFillRect(_renderer, &screenRect);
    }
    
    /// <summary>
    /// Draws a "filled circle" effect in world coordinates by drawing many points.
    /// This is a basic implementation; more optimized methods exist.
    /// The circle's center will be translated by the camera.
    /// </summary>
    public void DrawWorldFilledCircle(int worldCenterX, int worldCenterY, int radius, byte r, byte g, byte b, byte a)
    {
        // Translate the world center point to screen coordinates
        // We can do this by translating a 0x0 rectangle at that world point and getting its new origin.
        var screenCenterPoint = _camera.ToScreenCoordinates(new Rectangle<int>(worldCenterX, worldCenterY, 0, 0)).Origin;

        SetDrawColor(r, g, b, a);

        // Iterate over a bounding box around the circle
        for (int y = -radius; y <= radius; y++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                // Check if the point (x,y) relative to center is within the circle
                if (x * x + y * y <= radius * radius)
                {
                    _sdl.RenderDrawPoint(_renderer, screenCenterPoint.X + x, screenCenterPoint.Y + y);
                }
            }
        }
    }

    // If you need a line (e.g., for a more complex circle outline)
    /// <summary>
    /// Draws a line in world coordinates.
    /// The line's endpoints will be translated by the camera.
    /// </summary>
    public void DrawWorldLine(int worldX1, int worldY1, int worldX2, int worldY2, byte r, byte g, byte b, byte a)
    {
        var screenP1 = _camera.ToScreenCoordinates(new Rectangle<int>(worldX1, worldY1, 0, 0)).Origin;
        var screenP2 = _camera.ToScreenCoordinates(new Rectangle<int>(worldX2, worldY2, 0, 0)).Origin;

        SetDrawColor(r, g, b, a);
        _sdl.RenderDrawLine(_renderer, screenP1.X, screenP1.Y, screenP2.X, screenP2.Y);
    }
}