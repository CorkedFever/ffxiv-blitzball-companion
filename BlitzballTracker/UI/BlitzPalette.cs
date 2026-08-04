using System.Numerics;

namespace BlitzballTracker.UI;

using BlitzballTracker.Core.GameState;

/// <summary>
/// The colour tokens from the web app's blitz.css, so the in-game overlay and the
/// Blazor view are the same product rather than two lookalikes.
///
/// Values are stored packed for ImGui (0xAABBGGRR) rather than as Vector4, because
/// the draw list wants packed uints and converting per frame is wasted work in an
/// immediate-mode UI that rebuilds everything every frame.
/// </summary>
public static class BlitzPalette
{
    /// <summary>Pack an 0xRRGGBB literal, matching how the CSS reads.</summary>
    public static uint Rgb(uint hex, float alpha = 1f)
    {
        var r = (hex >> 16) & 0xFF;
        var g = (hex >> 8) & 0xFF;
        var b = hex & 0xFF;
        var a = (uint)Math.Clamp(alpha * 255f, 0f, 255f);
        return (a << 24) | (b << 16) | (g << 8) | r;
    }

    public static Vector4 ToVector(uint packed) => new(
        (packed & 0xFF) / 255f,
        ((packed >> 8) & 0xFF) / 255f,
        ((packed >> 16) & 0xFF) / 255f,
        ((packed >> 24) & 0xFF) / 255f);

    /// <summary>Replace a packed colour's alpha, for fades and glow layers.</summary>
    public static uint WithAlpha(uint packed, float alpha)
    {
        var a = (uint)Math.Clamp(alpha * 255f, 0f, 255f);
        return (packed & 0x00FFFFFF) | (a << 24);
    }

    // --- blitz.css custom properties ---
    public const uint BgDarkHex = 0x0a0e1a;
    public const uint BgCardHex = 0x131829;
    public const uint BgCardHoverHex = 0x1a2035;
    public const uint BorderHex = 0x2a3050;
    public const uint InkHex = 0xe0e4f0;
    public const uint InkDimHex = 0x8890a8;
    public const uint AccentHex = 0x4fc3f7;
    public const uint SuccessHex = 0x66bb6a;
    public const uint DangerHex = 0xef5350;
    public const uint WarningHex = 0xffa726;
    public const uint GoldHex = 0xffd54f;

    /// <summary>
    /// The ball itself.
    ///
    /// Kept separate from gold, which now means scoring rather than possession. A
    /// bright aqua rather than a muted teal, because the arena water is already teal
    /// and the ball has to read against it.
    /// </summary>
    public const uint BallHex = 0x1de9b6;
    public const uint PurpleHex = 0xab47bc;

    public static readonly uint BgDark = Rgb(BgDarkHex);
    public static readonly uint BgCard = Rgb(BgCardHex);
    public static readonly uint BgCardHover = Rgb(BgCardHoverHex);
    public static readonly uint Border = Rgb(BorderHex);
    public static readonly uint Ink = Rgb(InkHex);
    public static readonly uint InkDim = Rgb(InkDimHex);
    public static readonly uint Accent = Rgb(AccentHex);
    public static readonly uint AccentGlow = Rgb(AccentHex, 0.3f);
    public static readonly uint Success = Rgb(SuccessHex);
    public static readonly uint Danger = Rgb(DangerHex);
    public static readonly uint Warning = Rgb(WarningHex);
    public static readonly uint Gold = Rgb(GoldHex);
    public static readonly uint Ball = Rgb(BallHex);
    public static readonly uint Purple = Rgb(PurpleHex);

    // --- Field view, mirroring the .zone-* and .player-* rules ---
    public static readonly uint ZoneFill = Rgb(BgCardHex, 0.9f);
    public static readonly uint ZoneStroke = Border;
    public static readonly uint ZoneGoalStroke = Rgb(GoldHex, 0.4f);
    public static readonly uint ZoneBallStroke = Ball;
    public static readonly uint ZoneBallFill = Rgb(BallHex, 0.08f);
    public static readonly uint LaneLine = Rgb(BorderHex, 0.75f);
    public static readonly uint ZoneLabel = Rgb(InkDimHex, 0.85f);
    public static readonly uint ZoneSubLabel = Rgb(InkDimHex, 0.5f);
    public static readonly uint RushGate = Rgb(WarningHex, 0.8f);

    /// <summary>Home reads red and away yellow, matching the existing overlay.</summary>
    public static readonly uint TeamHome = Rgb(0xff6b6b);
    public static readonly uint TeamAway = Rgb(0xffd54f);

    public static uint TeamColor(bool isHome) => isHome ? TeamHome : TeamAway;

    /// <summary>Phase accent, matching the web app's .phase-* rules.</summary>
    public static uint PhaseColor(GamePhase phase) => phase switch
    {
        GamePhase.OuterPhase or GamePhase.InnerPhase => Danger,
        GamePhase.BallCarrierOuter or GamePhase.BallCarrierInner => Success,
        GamePhase.OuterReposition or GamePhase.InnerReposition => Accent,
        GamePhase.OuterHuddle or GamePhase.InnerHuddle => Warning,
        GamePhase.BuzzerPhase => Warning,
        GamePhase.Blitzoff => Gold,
        GamePhase.Shootout or GamePhase.SuddenDeath => Purple,
        GamePhase.Halftime => InkDim,
        _ => Ink,
    };

    public static string PhaseLabel(GamePhase phase) => phase switch
    {
        GamePhase.PreGame => "Pre-Game",
        GamePhase.Blitzoff => "BLITZOFF",
        GamePhase.OuterHuddle => "Outer Huddle",
        GamePhase.OuterPhase => "Outer Phase",
        GamePhase.OuterReposition => "Outer Reposition",
        GamePhase.BallCarrierOuter => "Ball Carrier — Strike",
        GamePhase.InnerHuddle => "Inner Huddle",
        GamePhase.InnerPhase => "Inner Phase",
        GamePhase.InnerReposition => "Inner Reposition",
        GamePhase.BallCarrierInner => "Ball Carrier — Center/Goal",
        GamePhase.BuzzerPhase => "BUZZER — must shoot",
        GamePhase.Halftime => "Halftime",
        GamePhase.Shootout => "Shootout",
        GamePhase.SuddenDeath => "Sudden Death",
        GamePhase.PostGame => "Game Over",
        _ => "Unknown",
    };

    public static string RoleBadge(PlayerRole role) => Roster.RoleAbbreviation(role);
}
