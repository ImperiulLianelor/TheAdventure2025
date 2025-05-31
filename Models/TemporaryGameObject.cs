using Silk.NET.Maths; // ADDED for Vector2D
using Silk.NET.SDL;

namespace TheAdventure.Models;

public class TemporaryGameObject : RenderableGameObject
{
    public double Ttl { get; init; } // Total time to live (visual fuse + explosion effect duration)
    
    // ADDED: Time from spawn until the bomb actually "detonates" (deals damage, destroys tiles)
    // This should be less than Ttl if there's a visual fuse.
    public double DetonationTime { get; init; } 
    public int BlastRadius { get; init; } // ADDED: Blast radius in pixels

    public bool IsExpired => (DateTimeOffset.Now - _spawnTime).TotalSeconds >= Ttl;
    
    // ADDED: Has the bomb's effect (damage, destruction) been triggered?
    public bool HasDetonatedEffectTriggered { get; private set; } = false; 
    
    // ADDED: Is the bomb currently in its actual explosion phase (after fuse)?
    public bool IsExploding => (DateTimeOffset.Now - _spawnTime).TotalSeconds >= DetonationTime && !IsExpired;

    private DateTimeOffset _spawnTime;
    
    public TemporaryGameObject(SpriteSheet spriteSheet, double ttl, (int X, int Y) position,
                               double detonationTime, int blastRadius, // ADDED parameters
                               double angle = 0.0, Point rotationCenter = new())
        : base(spriteSheet, position, angle, rotationCenter)
    {
        Ttl = ttl;
        _spawnTime = DateTimeOffset.Now;
        DetonationTime = detonationTime; // ADDED
        BlastRadius = blastRadius;     // ADDED

        if (DetonationTime > Ttl)
        {
            // Basic validation
            DetonationTime = Ttl; 
            Console.WriteLine("Warning: Bomb DetonationTime was greater than Ttl. Clamped to Ttl.");
        }
    }

    // ADDED: Method to mark that the detonation effect has been applied
    public void MarkEffectTriggered()
    {
        HasDetonatedEffectTriggered = true;
    }

    // ADDED: Helper to check if it's time to apply detonation effects
    public bool ShouldApplyDetonationEffect()
    {
        return (DateTimeOffset.Now - _spawnTime).TotalSeconds >= DetonationTime && !HasDetonatedEffectTriggered;
    }
}