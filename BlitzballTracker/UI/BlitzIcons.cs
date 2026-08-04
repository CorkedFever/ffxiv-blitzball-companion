using Dalamud.Game.Text;

namespace BlitzballTracker.UI;

using BlitzballTracker.Core.GameState;

/// <summary>
/// Status and zone symbols, drawn from the game's own glyph set.
///
/// SeIconChar glyphs live in the standard font, so they need no font switching and
/// work directly in draw-list text. That matters for the world overlay, where
/// pushing a separate icon font would be awkward.
///
/// The zone labels use the real waymark glyphs, so a zone marker in the overlay
/// looks like the waymark it is sitting on.
/// </summary>
public static class BlitzIcons
{
    private static string Glyph(SeIconChar icon) => ((char)icon).ToString();

    // --- Status ---
    public static readonly string Dazed = Glyph(SeIconChar.Debuff);
    public static readonly string Guarding = Glyph(SeIconChar.Buff);
    public static readonly string Blocked = Glyph(SeIconChar.Prohibited);
    public static readonly string Diving = Glyph(SeIconChar.ArrowDown);
    public static readonly string Surveying = Glyph(SeIconChar.Triangle);
    public static readonly string HasBall = Glyph(SeIconChar.Circle);
    public static readonly string Dice = Glyph(SeIconChar.Dice);
    public static readonly string RushGate = Glyph(SeIconChar.LinkMarker);

    /// <summary>
    /// The genuine FFXIV waymark glyphs: boxed letters for A-D, boxed numbers for
    /// the number lane.
    /// </summary>
    public static string WaymarkGlyph(Waymark waymark) => waymark switch
    {
        Waymark.A => Glyph(SeIconChar.BoxedLetterA),
        Waymark.B => Glyph(SeIconChar.BoxedLetterB),
        Waymark.C => Glyph(SeIconChar.BoxedLetterC),
        Waymark.D => Glyph(SeIconChar.BoxedLetterD),
        Waymark.One => Glyph(SeIconChar.BoxedNumber1),
        Waymark.Two => Glyph(SeIconChar.BoxedNumber2),
        Waymark.Four => Glyph(SeIconChar.BoxedNumber4),
        _ => string.Empty,
    };

    [Flags]
    private enum StatusBits
    {
        None = 0,
        Ball = 1 << 0,
        Dazed = 1 << 1,
        Blocked = 1 << 2,
        Diving = 1 << 3,
        Surveying = 1 << 4,
        Guarding = 1 << 5,
    }

    /// <summary>
    /// Composed status strings, cached by combination. There are only 128 possible
    /// combinations, and the draw path runs every frame for every player, so
    /// rebuilding these each time would allocate for no reason.
    /// </summary>
    private static readonly Dictionary<StatusBits, string> Composed = new();

    /// <summary>
    /// A compact run of symbols describing everything currently true of a player.
    /// Empty when nothing notable applies.
    /// </summary>
    public static string StatusFor(PlayerState player)
    {
        var bits = StatusBits.None;

        if (player.HasBall) bits |= StatusBits.Ball;
        if (player.IsDazed) bits |= StatusBits.Dazed;
        if (player.IsBlocked) bits |= StatusBits.Blocked;
        if (player.IsDiving) bits |= StatusBits.Diving;
        if (player.IsSurveying) bits |= StatusBits.Surveying;
        if (player.IsGuarding || player.GuardBonus > 0) bits |= StatusBits.Guarding;

        // No "has rolled" symbol: the roll itself is drawn, and a die that only tells
        // you a number exists while hiding the number is the worst of both.

        if (bits == StatusBits.None) return string.Empty;

        if (Composed.TryGetValue(bits, out var cached))
            return cached;

        // Ordered by how much it matters at a glance.
        var text = string.Concat(
            bits.HasFlag(StatusBits.Ball) ? HasBall : string.Empty,
            bits.HasFlag(StatusBits.Dazed) ? Dazed : string.Empty,
            bits.HasFlag(StatusBits.Blocked) ? Blocked : string.Empty,
            bits.HasFlag(StatusBits.Guarding) ? Guarding : string.Empty,
            bits.HasFlag(StatusBits.Diving) ? Diving : string.Empty,
            bits.HasFlag(StatusBits.Surveying) ? Surveying : string.Empty);

        Composed[bits] = text;
        return text;
    }

    /// <summary>
    /// Colour for a player's status symbols, tinted by whichever condition matters
    /// most.
    ///
    /// Kept strictly separate from team colour. Symbols may be tinted by state
    /// because they are plainly not identity; names and dots may not, because
    /// repainting those reads as a player changing sides.
    /// </summary>
    public static uint StatusColor(PlayerState player)
    {
        if (player.IsDazed) return BlitzPalette.Danger;
        if (player.HasBall) return BlitzPalette.Ball;
        if (player.IsBlocked) return BlitzPalette.Purple;
        if (player.IsGuarding || player.GuardBonus > 0) return BlitzPalette.Success;
        return BlitzPalette.WithAlpha(BlitzPalette.InkDim, 0.9f);
    }

    /// <summary>
    /// Composed roll strings, cached by value and modifier.
    ///
    /// This runs every frame for every player and <c>int.ToString</c> allocates, which
    /// is exactly the kind of steady garbage that turns smooth motion into a stutter.
    /// A match only ever produces a hundred or so distinct combinations.
    /// </summary>
    private static readonly Dictionary<(int Roll, int Modifier), string> RollLabels = new();

    /// <summary>
    /// A player's roll, ready to draw: the die, the number, and the modifier it is
    /// carrying.
    ///
    /// The modifier is kept visible rather than folded into a total, because in a
    /// dispute what people argue about is whether the bonus applied — and a bare
    /// total cannot answer that.
    /// </summary>
    public static string RollText(int roll, int modifier = 0)
    {
        var key = (roll, modifier);

        if (RollLabels.TryGetValue(key, out var cached))
            return cached;

        var text = modifier switch
        {
            0 => $"{Dice}{roll}",
            > 0 => $"{Dice}{roll}+{modifier}",
            _ => $"{Dice}{roll}{modifier}",
        };

        RollLabels[key] = text;
        return text;
    }

    /// <summary>
    /// Colour for a roll, banded only at the ends.
    ///
    /// Higher always beats lower in blitzball, so the extremes can be called: a 90+
    /// wins nearly everything and a single digit loses nearly everything. The middle
    /// stays neutral on purpose — whether a 54 is good depends entirely on what it is
    /// rolled against, and tinting it would be inventing a threshold the game does
    /// not have.
    /// </summary>
    public static uint RollColor(int effective) => effective switch
    {
        >= 90 => BlitzPalette.Gold,
        <= 9 => BlitzPalette.Danger,
        _ => BlitzPalette.Ink,
    };

    /// <summary>Short label for a declared action.</summary>
    public static string ActionLabel(ActionType action) => action switch
    {
        ActionType.Tackle => "TACKLE",
        ActionType.Block => "BLOCK",
        ActionType.Move => "MOVE",
        ActionType.Dive => "DIVE",
        ActionType.Pass => "PASS",
        ActionType.Shoot => "SHOOT",
        ActionType.Guard => "GUARD",
        ActionType.Taunt => "TAUNT",
        ActionType.Rally => "RALLY",
        ActionType.Shove => "SHOVE",
        ActionType.Survey => "SURVEY",
        ActionType.Rush => "RUSH",
        _ => string.Empty,
    };

    private static readonly Dictionary<(ActionType Action, Waymark Destination), string> DestinationLabels = new();

    /// <summary>
    /// A declared action together with the waymark it is aimed at — "MOVE→C".
    ///
    /// Where someone is going is most of what a declared move tells you, and moves are
    /// declared a whole phase before they land. Without this the field showed that a
    /// player intended to move and made you wait to find out where.
    ///
    /// Cached by pair: thirteen actions and eight waymarks, drawn every frame.
    /// </summary>
    public static string ActionLabel(ActionType action, Waymark destination)
    {
        if (destination == Waymark.None) return ActionLabel(action);

        var key = (action, destination);

        if (DestinationLabels.TryGetValue(key, out var cached))
            return cached;

        var name = ActionLabel(action);
        var glyph = WaymarkGlyph(destination);

        // Either half missing means there is nothing to join, so fall back rather than
        // drawing a dangling arrow.
        var text = name.Length == 0 || glyph.Length == 0 ? name : $"{name}→{glyph}";

        DestinationLabels[key] = text;
        return text;
    }

    /// <summary>
    /// Colour for how an action turned out, so a glance separates what is still
    /// hanging from what has already landed.
    /// </summary>
    public static uint OutcomeColor(ActionOutcome outcome) => outcome switch
    {
        ActionOutcome.Goal => BlitzPalette.Gold,
        ActionOutcome.Success => BlitzPalette.Success,
        ActionOutcome.Caught => BlitzPalette.Accent,
        ActionOutcome.Fail or ActionOutcome.Fumble => BlitzPalette.Danger,
        ActionOutcome.Dazed => BlitzPalette.Purple,

        // Pending: declared, waiting on rolls.
        _ => BlitzPalette.WithAlpha(BlitzPalette.Ink, 0.75f),
    };

    /// <summary>Symbol and meaning pairs, for the legend.</summary>
    public static readonly (string Icon, string Meaning, uint Color)[] Legend =
    [
        (HasBall, "Ball carrier", BlitzPalette.Ball),
        (Dazed, "Dazed", BlitzPalette.Danger),
        (Blocked, "Blocked", BlitzPalette.Purple),
        (Guarding, "Guarding / GK bonus", BlitzPalette.Success),
        (Diving, "Diving", BlitzPalette.Accent),
        (Surveying, "Surveying", BlitzPalette.Accent),
        (Dice, "Roll this phase, with any modifier", BlitzPalette.Ink),
        (RushGate, "Rush Gate", BlitzPalette.Warning),
    ];
}
