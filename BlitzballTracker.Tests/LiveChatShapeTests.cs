using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// The shapes real chat actually takes, taken from a live match.
///
/// Names here are invented — chat logs stay out of the repository — but every message
/// format is transcribed from a real one. The generator writes tidy, consistent lines;
/// people do not, and the difference is where the parser breaks.
/// </summary>
public class LiveChatShapeTests
{
    /// <summary>
    /// The glyph the game puts between "a" and the number in a /random line. It is a
    /// private-use character, not whitespace, which is exactly why it broke the match.
    /// </summary>
    private const string Dice = "";

    /// <summary>The crossworld glyph that sits between a character name and their world.</summary>
    private const string CrossWorld = "";

    private const string Ref = "Match Referee";

    private static (BlitzGame Game, ChatParser Parser) NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        return (game, new ChatParser(game));
    }

    private static PlayerState Player(BlitzGame game, string team, PlayerRole role) =>
        game.Players.Values.First(p =>
            p.Role == role && p.Team.Equals(team, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The one that mattered. Every roll in a real match carries a dice glyph, so
    /// requiring digits straight after "a" meant nothing resolved all game.
    /// </summary>
    [Fact]
    public void ARollWithTheDiceGlyphIsRead()
    {
        var (game, parser) = NewGame();
        var player = Player(game, "SIM RED", PlayerRole.Midfield);

        parser.ProcessMessage(Ref,
            $"Random! {player.Name}{CrossWorld} Balmung rolls a {Dice} 7 (out of 100).", DateTime.Now);

        Assert.Equal(7, player.PhaseRoll);
    }

    [Fact]
    public void APlainRollStillReads()
    {
        var (game, parser) = NewGame();
        var player = Player(game, "SIM RED", PlayerRole.Midfield);

        parser.ProcessMessage(Ref, $"Random! {player.Name} rolls a 42 (out of 100).", DateTime.Now);

        Assert.Equal(42, player.PhaseRoll);
    }

    [Fact]
    public void TheLocalPlayersRollCarriesTheGlyphToo()
    {
        var (game, parser) = NewGame();
        var player = Player(game, "SIM RED", PlayerRole.Midfield);

        parser.LocalPlayerName = player.Name;
        parser.ProcessMessage(player.Name, $"You roll a {Dice} 88 (out of 100).", DateTime.Now);

        Assert.Equal(88, player.PhaseRoll);
    }

    /// <summary>
    /// Everybody writes their action differently. These are all real, from one match.
    /// </summary>
    [Theory]
    [InlineData("|| {0} surges forward through the Blitz Sphere and attempts to spear [TACKLE] {1}!")]
    [InlineData("|| {0} attempts to clip their legs! [TACKLE → {1}]!")]
    [InlineData("|| {0} slams into her target.[TACKLE {1}]!")]
    [InlineData("|| {0} attempts to [TACKLE] {1}!")]
    public void EveryWrittenFormOfATackleIsRead(string template)
    {
        var (game, parser) = NewGame();

        var actor = Player(game, "SIM RED", PlayerRole.LeftForward);
        var target = Player(game, "SIM GOLD", PlayerRole.Midfield);

        actor.Position = Waymark.B;
        target.Position = Waymark.Two;

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", DateTime.Now);
        parser.ProcessMessage(actor.Name, string.Format(template, actor.Name, target.Name), DateTime.Now);

        var declared = game.CurrentPhaseActions.SingleOrDefault(a => a.Action == ActionType.Tackle);

        Assert.NotNull(declared);
        Assert.Equal(target.Name, declared!.TargetName);
    }

    [Theory]
    [InlineData("|| {0} prepares to swim! [MOVE → C]")]
    [InlineData("|| {0} attempts to [MOVE] to [C]")]
    [InlineData("|| {0} heads to the goal. [MOVE to C]")]
    [InlineData("|| {0} attempts to swim toward Zone C [MOVE C]")]
    public void EveryWrittenFormOfAMoveIsRead(string template)
    {
        var (game, parser) = NewGame();

        var actor = Player(game, "SIM RED", PlayerRole.LeftDefender);
        actor.Position = Waymark.A;

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", DateTime.Now);
        parser.ProcessMessage(actor.Name, string.Format(template, actor.Name), DateTime.Now);

        var declared = game.CurrentPhaseActions.SingleOrDefault(a => a.Action == ActionType.Move);

        Assert.NotNull(declared);
        Assert.Equal(Waymark.C, declared!.TargetWaymark);
    }

    /// <summary>Rally names its target after a colon, which used to stop the capture dead.</summary>
    [Fact]
    public void ARallyNamesItsTargetAfterAColon()
    {
        var (game, parser) = NewGame();

        var mid = Player(game, "SIM RED", PlayerRole.Midfield);
        var mate = Player(game, "SIM RED", PlayerRole.LeftForward);

        mid.Position = Waymark.One;
        mate.Position = Waymark.A;

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", DateTime.Now);
        parser.ProcessMessage(mid.Name,
            $"ll {mid.Name} gave a sharp whistle. [RALLY] on: {mate.Name}!", DateTime.Now);

        var declared = game.CurrentPhaseActions.SingleOrDefault(a => a.Action == ActionType.Rally);

        Assert.NotNull(declared);
        Assert.Equal(mate.Name, declared!.TargetName);
    }

    /// <summary>
    /// A Blitzon is called inline and in passing, not with the referee's angle brackets.
    /// </summary>
    [Fact]
    public void ABlitzonCalledInPassingIsRecognised()
    {
        var (game, parser) = NewGame();
        game.Score = new Score(2, 0);

        parser.ProcessMessage(Ref, "Djrn launches the ball over to the wing. [BLITZON.]", DateTime.Now);

        Assert.Equal(GamePhase.Blitzoff, game.Phase);
        Assert.Equal(BlitzoffKind.Blitzon, game.BlitzoffVariant);
    }

    /// <summary>
    /// The crowd shouts "BLITZOFF!!!" at kickoff. Matching bare text would restart the
    /// match on every cheer.
    /// </summary>
    [Fact]
    public void TheCrowdShoutingDoesNotRestartTheMatch()
    {
        var (game, parser) = NewGame();
        game.Phase = GamePhase.OuterPhase;

        parser.ProcessMessage("Some Spectator", "\"BLITZOFF!!!\"", DateTime.Now);
        parser.ProcessMessage("Another Onlooker", "LET'S GOOOO! BLITZOFF!", DateTime.Now);

        Assert.Equal(GamePhase.OuterPhase, game.Phase);
    }

    [Fact]
    public void ADazeAnnouncedWithAWorldSuffixIsRead()
    {
        var (game, parser) = NewGame();
        var target = Player(game, "SIM GOLD", PlayerRole.Midfield);

        parser.ProcessMessage(Ref, $"[[ DAZED - {target.Name}{CrossWorld} Balmung ]]", DateTime.Now);

        Assert.True(target.IsDazed);
    }

    [Fact]
    public void ADiveWrittenWithATrailingStopIsRead()
    {
        var (game, parser) = NewGame();
        var diver = Player(game, "SIM RED", PlayerRole.LeftDefender);

        parser.ProcessMessage(Ref, "<< OUTER PHASE (A/B/1/2) >> Start!", DateTime.Now);
        parser.ProcessMessage(diver.Name,
            $"|| {diver.Name} tenses in the water, ready to intercept. [DIVE].", DateTime.Now);

        Assert.True(diver.IsDiving);
    }

    [Fact]
    public void ARushGateCalledWithAnArrowIsRead()
    {
        var (game, parser) = NewGame();
        var keeper = Player(game, "SIM RED", PlayerRole.Goalkeeper);

        parser.ProcessMessage(Ref, "<< INNER PHASE (4/C/D) >> Start!", DateTime.Now);
        parser.ProcessMessage(keeper.Name, $"{keeper.Name} activates a Rush Gate! [RUSH → C]", DateTime.Now);

        Assert.NotNull(game.RushGateAt(Waymark.C));
    }
}
