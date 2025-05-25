using Silk.NET.Maths; // Make sure this using is present for Rectangle<int>

namespace TheAdventure.Models;

public class PlayerObject : RenderableGameObject
{
    private const int _speed = 128; // pixels per second

    public enum PlayerStateDirection
    {
        None = 0,
        Down,
        Up,
        Left,
        Right,
    }

    public enum PlayerState
    {
        None = 0,
        Idle,
        Move,
        Attack,
        GameOver
    }

    public (PlayerState State, PlayerStateDirection Direction) CurrentVisualState { get; private set; } // Renamed for clarity

    public PlayerObject(SpriteSheet spriteSheet, int x, int y) : base(spriteSheet, (x, y))
    {
        SetVisualState(PlayerState.Idle, PlayerStateDirection.Down);
    }

    public void SetVisualState(PlayerState state) // Renamed method
    {
        SetVisualState(state, CurrentVisualState.Direction);
    }

    public void SetVisualState(PlayerState state, PlayerStateDirection direction) // Renamed method
    {
        if (CurrentVisualState.State == PlayerState.GameOver && state != PlayerState.GameOver) // Allow setting GameOver once
        {
            return;
        }

        if (CurrentVisualState.State == state && CurrentVisualState.Direction == direction)
        {
            return;
        }

        string? animationName = null;
        if (state == PlayerState.None && direction == PlayerStateDirection.None)
        {
            animationName = null;
        }
        else if (state == PlayerState.GameOver)
        {
            animationName = Enum.GetName(state); // e.g., "GameOver"
        }
        else
        {
            animationName = Enum.GetName(state) + Enum.GetName(direction); // e.g., "IdleDown", "MoveUp"
        }
        SpriteSheet.ActivateAnimation(animationName);
        
        CurrentVisualState = (state, direction);
    }

    public void GameOver()
    {
        SetVisualState(PlayerState.GameOver, PlayerStateDirection.None);
    }

    public void Attack()
    {
        if (CurrentVisualState.State == PlayerState.GameOver)
        {
            return;
        }
        // Use current facing direction for attack animation
        SetVisualState(PlayerState.Attack, CurrentVisualState.Direction);
    }

    // ADDED: Method to get the player's world bounding box based on current position and sprite.
    public Rectangle<int> GetWorldBoundingBox()
    {
        // Position is the 'anchor' or 'center' defined by SpriteSheet.FrameCenter.
        // To get the top-left corner of the visual sprite:
        int topLeftX = Position.X - SpriteSheet.FrameCenter.OffsetX;
        int topLeftY = Position.Y - SpriteSheet.FrameCenter.OffsetY;
        return new Rectangle<int>(topLeftX, topLeftY, SpriteSheet.FrameWidth, SpriteSheet.FrameHeight);
    }

    // ADDED: Helper to get bounding box if player *were* at a specific potential position.
    private Rectangle<int> GetWorldBoundingBoxAt(int potentialWorldX, int potentialWorldY)
    {
        int topLeftX = potentialWorldX - SpriteSheet.FrameCenter.OffsetX;
        int topLeftY = potentialWorldY - SpriteSheet.FrameCenter.OffsetY;
        return new Rectangle<int>(topLeftX, topLeftY, SpriteSheet.FrameWidth, SpriteSheet.FrameHeight);
    }

    // MODIFIED: Method signature and internal logic for collision.
    public void UpdatePosition(double up, double down, double left, double right,
                               double timeDeltaMs, Engine gameEngine)
    {
        if (CurrentVisualState.State == PlayerState.GameOver)
        {
            return;
        }

        var pixelsToMoveTotal = _speed * (timeDeltaMs / 1000.0);
        var oldPosition = Position;

        // Calculate desired displacement
        double deltaX = (right * pixelsToMoveTotal) - (left * pixelsToMoveTotal);
        double deltaY = (down * pixelsToMoveTotal) - (up * pixelsToMoveTotal);

        var targetPosition = Position; // Start with current position

        // --- X-axis movement and collision ---
        if (deltaX != 0)
        {
            int potentialX = Position.X + (int)deltaX;
            var testBoxX = GetWorldBoundingBoxAt(potentialX, Position.Y);
            if (!gameEngine.CheckTileCollision(testBoxX))
            {
                targetPosition.X = potentialX;
            }
            // Else: Collision detected in X, targetPosition.X remains Position.X
        }

        // --- Y-axis movement and collision ---
        // Check Y-axis collision based on the potentially updated X (targetPosition.X)
        // This allows sliding along walls.
        if (deltaY != 0)
        {
            int potentialY = Position.Y + (int)deltaY;
            var testBoxY = GetWorldBoundingBoxAt(targetPosition.X, potentialY); 
            if (!gameEngine.CheckTileCollision(testBoxY))
            {
                targetPosition.Y = potentialY;
            }
            // Else: Collision detected in Y, targetPosition.Y remains Position.Y
        }
        
        Position = targetPosition; // Apply the final, collision-checked position

        // --- Update Visual State and Direction logic ---
        var newPlayerState = CurrentVisualState.State;
        var newDirection = CurrentVisualState.Direction;

        bool moved = (Position.X != oldPosition.X) || (Position.Y != oldPosition.Y);

        if (CurrentVisualState.State == PlayerState.Attack)
        {
            if (SpriteSheet.AnimationFinished)
            {
                newPlayerState = PlayerState.Idle; // Default to idle after attack finishes
            }
            // else, keep Attack state until animation finishes
        }
        else if (moved)
        {
            newPlayerState = PlayerState.Move;
            // Determine direction based on input primarily, then actual movement
            if (up > 0) newDirection = PlayerStateDirection.Up;
            else if (down > 0) newDirection = PlayerStateDirection.Down;
            else if (left > 0) newDirection = PlayerStateDirection.Left;
            else if (right > 0) newDirection = PlayerStateDirection.Right;
            // Fallback if no input but position changed (e.g., pushed - not applicable here yet)
            else if (Position.Y < oldPosition.Y) newDirection = PlayerStateDirection.Up;
            else if (Position.Y > oldPosition.Y) newDirection = PlayerStateDirection.Down;
            else if (Position.X < oldPosition.X) newDirection = PlayerStateDirection.Left;
            else if (Position.X > oldPosition.X) newDirection = PlayerStateDirection.Right;
        }
        else // Not moving and not attacking (or attack finished)
        {
            newPlayerState = PlayerState.Idle;
            // Keep current facing direction when idling
        }

        // Only update animation if state or direction actually changes
        if (newPlayerState != CurrentVisualState.State || newDirection != CurrentVisualState.Direction)
        {
            SetVisualState(newPlayerState, newDirection);
        }
    }
}