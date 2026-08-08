using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// A recording has to replay into the same match it recorded.
///
/// The lines here are in the exact format <c>GameRecorder</c> writes — roster header,
/// then <c>[timestamp] [Channel] Sender: message</c> — and carry the glyphs real chat
/// carries. Names are invented; chat logs stay out of the repository.
/// </summary>
public class RecordingRoundTripTests
{
    /// <summary>
    /// The glyph the game puts between "a" and the number in a /random line.
    ///
    /// Written as an escape rather than pasted: it is invisible in an editor, and a
    /// copy that quietly loses it turns this into a test of nothing.
    /// </summary>
    private const string Dice = "";

    /// <summary>The glyph between a character name and their world.</summary>
    private const string CrossWorld = "";

    private static string Line(string channel, string sender, string message) =>
        $"[2026-08-07 21:30:00] [{channel}] {sender}: {message}";

    /// <summary>
    /// The whole point: what was recorded is what replays. Roster from the header,
    /// phases from the referee's channel, actions and rolls from Yell.
    /// </summary>
    [Fact]
    public void ARecordingReplaysIntoTheMatchItRecorded()
    {
        var roster = MatchSimulator.StandardRoster();

        var forward = roster.Entries.First(e =>
            e.Role == PlayerRole.LeftForward && e.Team == "SIM RED");
        var victim = roster.Entries.First(e =>
            e.Role == PlayerRole.Midfield && e.Team == "SIM GOLD");

        var recorded = new List<string>();
        recorded.AddRange(RosterHeader.Write(roster).Split('\n', StringSplitOptions.RemoveEmptyEntries));

        recorded.Add(Line("CrossLinkShell", "Match Referee", "<< OUTER PHASE (A/B/1/2) >> Start!"));
        recorded.Add(Line("Yell", forward.Name,
            $"|| {forward.Name}{CrossWorld} Balmung crashes in. [TACKLE -> {victim.Name}]"));
        recorded.Add(Line("Dice Roll", "Match Referee",
            $"Random! {forward.Name}{CrossWorld} Balmung rolls a {Dice} 90 (out of 100)."));
        recorded.Add(Line("Dice Roll", "Match Referee",
            $"Random! {victim.Name}{CrossWorld} Mateus rolls a {Dice} 10 (out of 100)."));

        // Read the roster back out of the header, exactly as a replay does.
        var recovered = RosterHeader.Read(recorded);
        Assert.NotNull(recovered);

        var game = new BlitzGame();
        game.ApplyRoster(recovered!);

        var parser = new ChatParser(game);

        var lines = recorded
            .Select(LogReplay.ParseLine)
            .Where(l => l is not null)
            .Select(l => l!)
            .ToList();

        LogReplay.Replay(lines, parser);

        Assert.True(game.HasPhaseFeed, "The referee's channel should survive the round trip.");
        Assert.Equal(GamePhase.OuterPhase, game.Phase);
        Assert.Equal(90, game.Players[forward.Name].PhaseRoll);
        Assert.True(game.Players[victim.Name].IsDazed, "The contest should resolve on replay.");
    }

    /// <summary>
    /// The parser does not care whether the glyphs survived the trip, which is the
    /// property that matters: it reads a line with them and without them alike.
    /// </summary>
    [Theory]
    [InlineData("Random! {0} rolls a  64 (out of 100).")]
    [InlineData("Random! {0} rolls a 64 (out of 100).")]
    [InlineData("Random! {0} Balmung rolls a  64 (out of 100).")]
    [InlineData("Random! {0} Balmung rolls a 64 (out of 100).")]
    public void ARollReadsWithOrWithoutTheGlyphs(string template)
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        var parser = new ChatParser(game);

        var player = game.Players.Values.First(p => !p.IsGoalkeeper);

        parser.ProcessMessage("Match Referee", string.Format(template, player.Name), DateTime.Now);

        Assert.Equal(64, player.PhaseRoll);
    }

    /// <summary>
    /// A recording made by somebody who is playing has to say who they were.
    ///
    /// The game writes your own dice as "You roll a 40" and everyone else's by name. The
    /// plugin knows who you are while it runs, but the file does not, so without this
    /// line every roll the recorder made is unattributable — and it is their own team
    /// that ends up with a player who apparently never rolled all match.
    /// </summary>
    [Fact]
    public void TheRecordersOwnRollsSurviveIntoTheReplay()
    {
        var roster = MatchSimulator.StandardRoster();
        var me = roster.Entries.First(e => e.Role == PlayerRole.LeftForward);

        var recorded = new List<string>();
        recorded.AddRange(RosterHeader.Write(roster, me.Name)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries));

        recorded.Add(Line("CrossLinkShell", "Match Referee", "<< OUTER PHASE (A/B/1/2) >> Start!"));
        recorded.Add(Line("Dice Roll", "", "Random! You roll a 73 (out of 100)."));

        var recovered = RosterHeader.Read(recorded);
        Assert.NotNull(recovered);
        Assert.Equal(me.Name, recovered!.RecordedBy);

        var game = new BlitzGame();
        game.ApplyRoster(recovered);

        var parser = new ChatParser(game) { LocalPlayerName = recovered.RecordedBy };

        LogReplay.Replay(
            recorded.Select(LogReplay.ParseLine).Where(l => l is not null).Select(l => l!).ToList(),
            parser);

        Assert.Equal(73, game.Players[me.Name].PhaseRoll);
    }

    /// <summary>Recording as a spectator names nobody, and must stay valid.</summary>
    [Fact]
    public void ARecordingWithNoRecorderNamedIsStillFine()
    {
        var roster = MatchSimulator.StandardRoster();

        var recovered = RosterHeader.Read(
            RosterHeader.Write(roster).Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.NotNull(recovered);
        Assert.Null(recovered!.RecordedBy);
    }

    /// <summary>
    /// A recording without its roster header is only half a record: the lineup is the
    /// one thing chat never carries, so nothing in it can be attributed later.
    /// </summary>
    [Fact]
    public void AHeaderlessRecordingRecoversNoRoster()
    {
        var lines = new[]
        {
            Line("Yell", "Somebody", "|| Somebody swims. [MOVE to C]"),
        };

        Assert.Null(RosterHeader.Read(lines));
    }
}
