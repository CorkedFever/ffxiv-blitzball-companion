namespace BlitzballTracker.UI;

/// <summary>
/// A critically damped spring: reaches its target quickly without overshooting or
/// oscillating.
///
/// Game state changes in discrete jumps, so without this a player repositioning
/// teleports across the field. Sliding them along the lane is the single largest
/// difference between an overlay that feels like a debug panel and one that feels
/// like part of the game.
/// </summary>
public struct Spring(float initial = 0f)
{
    public float Value = initial;
    public float Velocity = 0f;

    /// <summary>
    /// Higher converges faster. Around 14 lands just under a fifth of a second,
    /// which reads as responsive without looking instant.
    /// </summary>
    public const float DefaultStiffness = 14f;

    public void Update(float target, float deltaTime, float stiffness = DefaultStiffness)
    {
        // Guard against the frame hitches that follow a zone load: a large dt makes
        // the integration explode, which would fling everything off screen.
        if (deltaTime <= 0f) return;
        deltaTime = MathF.Min(deltaTime, 0.1f);

        var delta = target - Value;

        // Critical damping: damping = 2 * sqrt(stiffness) with unit mass.
        var acceleration = (stiffness * stiffness * delta) - (2f * stiffness * Velocity);

        Velocity += acceleration * deltaTime;
        Value += Velocity * deltaTime;

        // Settle exactly, so near-zero motion does not keep the UI redrawing forever.
        if (MathF.Abs(delta) < 0.01f && MathF.Abs(Velocity) < 0.01f)
        {
            Value = target;
            Velocity = 0f;
        }
    }

    /// <summary>Jump straight to a value, for first placement or a hard reset.</summary>
    public void Snap(float value)
    {
        Value = value;
        Velocity = 0f;
    }
}

/// <summary>Two springs travelling together, for positions on the field.</summary>
public struct Spring2
{
    public Spring X;
    public Spring Y;

    public readonly System.Numerics.Vector2 Value => new(X.Value, Y.Value);

    public void Update(System.Numerics.Vector2 target, float deltaTime, float stiffness = Spring.DefaultStiffness)
    {
        X.Update(target.X, deltaTime, stiffness);
        Y.Update(target.Y, deltaTime, stiffness);
    }

    public void Snap(System.Numerics.Vector2 value)
    {
        X.Snap(value.X);
        Y.Snap(value.Y);
    }
}
