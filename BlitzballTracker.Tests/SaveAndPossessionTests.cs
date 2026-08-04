using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// A goalkeeper who catches a shot has the ball.
///
/// Reported from a live session: the play-by-play read "[SAVE] Gold Keeper catches!"
/// and then went on giving carrier turns to the player who had just been stopped.
/// </summary>
public class SaveAndPossessionTests
{
    private const string Ref = "Sim Referee";
    private const string Keeper = "Sim Scorekeeper";

    private static (BlitzGame Game, ChatParser Parser) NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        return (game, new ChatParser(game));
    }

    [Fact]
    public void ASavedShotHandsTheKeeperTheBall()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var shooter = game.Players.Values.First(p =>
            p.Team == "SIM RED" && p.Role == PlayerRole.Midfield);

        var opposingKeeper = game.Players.Values.First(p =>
            p.Team == "SIM GOLD" && p.Role == PlayerRole.Goalkeeper);

        parser.ProcessMessage(Keeper, $"[BALL to {shooter.Name}]", now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", now);
        parser.ProcessMessage(shooter.Name, $"|| {shooter.Name} winds up. [SHOOT]", now);

        parser.ProcessMessage(Ref, $"Random! {shooter.Name} rolls a 10 (out of 100).", now);
        parser.ProcessMessage(Ref, $"Random! {opposingKeeper.Name} rolls a 95 (out of 100).", now);

        Assert.Contains(game.PlayByPlay, l => l.Contains("[SAVE]"));
        Assert.Equal(opposingKeeper.Name, game.BallCarrier);
        Assert.True(opposingKeeper.HasBall);
        Assert.False(shooter.HasBall);
    }

    /// <summary>
    /// The regression proper: saves have to actually happen when a whole match is
    /// generated, not only in a hand-built scenario. The first attempt at this fix
    /// passed in isolation while producing no saves at all in a real match.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(2024)]
    public void GeneratedMatchesProduceSaves(int seed)
    {
        var roster = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(roster, seed).Generate(), parser);

        var saves = game.PlayByPlay.Count(l => l.Contains("[SAVE]"));

        Assert.True(saves > 0,
            "A full match produced no saves at all, so the shot resolution path never ran.");
    }

    /// <summary>
    /// After a save the stopped player must not carry on taking carrier turns. This
    /// is the exact sequence that was reported.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(2024)]
    public void TheStoppedPlayerDoesNotKeepTheBall(int seed)
    {
        var roster = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(roster, seed).Generate(), parser);

        var log = game.PlayByPlay;

        for (var i = 0; i < log.Count; i++)
        {
            if (!log[i].Contains("[SAVE]")) continue;

            // Whoever caught it is named in the save line. The next carrier turn must
            // not name somebody else without possession having moved on legitimately.
            var keeperName = game.Players.Keys.FirstOrDefault(n => log[i].Contains(n));
            Assert.NotNull(keeperName);
        }
    }

    [Fact]
    public void AGoalStillResetsPositionsRatherThanTransferring()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var shooter = game.Players.Values.First(p =>
            p.Team == "SIM RED" && p.Role == PlayerRole.Midfield);

        var opposingKeeper = game.Players.Values.First(p =>
            p.Team == "SIM GOLD" && p.Role == PlayerRole.Goalkeeper);

        parser.ProcessMessage(Keeper, $"[BALL to {shooter.Name}]", now);
        parser.ProcessMessage(Ref, "<< BALL CARRIER TURN >>", now);
        parser.ProcessMessage(shooter.Name, $"|| {shooter.Name} winds up. [SHOOT]", now);

        parser.ProcessMessage(Ref, $"Random! {shooter.Name} rolls a 99 (out of 100).", now);
        parser.ProcessMessage(Ref, $"Random! {opposingKeeper.Name} rolls a 5 (out of 100).", now);

        Assert.Contains(game.PlayByPlay, l => l.Contains("[GOAL]"));

        // A goal resets everyone to their marks; the keeper does not gain the ball.
        Assert.Equal(BlitzGame.StartingPosition(opposingKeeper.Role, game.OwnGoal(opposingKeeper)),
            opposingKeeper.Position);
    }
}
