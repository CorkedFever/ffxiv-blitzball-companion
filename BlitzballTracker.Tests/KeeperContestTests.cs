using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Parsing;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// A goalkeeper can be tackled but never blocked.
///
/// This began showing up as log noise: once a keeper could take possession after a
/// save, the whole opposing side declared blocks on them and every one was refused,
/// filling the play-by-play with "cannot be blocked" and nothing else.
/// </summary>
public class KeeperContestTests
{
    private static (BlitzGame Game, ChatParser Parser) NewGame()
    {
        var game = new BlitzGame();
        game.ApplyRoster(MatchSimulator.StandardRoster());
        return (game, new ChatParser(game));
    }

    private static PlayerState Find(BlitzGame game, string team, PlayerRole role) =>
        game.Players.Values.First(p => p.Team == team && p.Role == role);

    [Fact]
    public void AForwardCanTackleAKeeper()
    {
        var (game, parser) = NewGame();
        var now = DateTime.Now;

        var keeper = Find(game, "SIM GOLD", PlayerRole.Goalkeeper);
        var forward = Find(game, "SIM RED", PlayerRole.LeftForward);

        // Put the forward on Centre, which shares the middle lane with both goals.
        game.TryPlace(forward, Waymark.C);

        Assert.True(game.CanTackle(forward, keeper),
            "A forward at Centre should reach a keeper on either goal.");

        parser.ProcessMessage(forward.Name, $"|| {forward.Name} crashes in. [TACKLE -> {keeper.Name}]", now);

        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("not a forward"));
        Assert.DoesNotContain(game.PlayByPlay, l => l.Contains("cannot be blocked"));
    }

    [Fact]
    public void ANonForwardCannotTackleAKeeper()
    {
        var (game, _) = NewGame();

        var keeper = Find(game, "SIM GOLD", PlayerRole.Goalkeeper);
        var defender = Find(game, "SIM RED", PlayerRole.LeftDefender);

        game.TryPlace(defender, Waymark.C);

        Assert.False(game.CanTackle(defender, keeper),
            "Tackling belongs to forwards, whoever the target is.");
    }

    /// <summary>
    /// The regression proper: a whole generated match should never attempt a block on
    /// a keeper, so the refusal advisory should never fire.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(99)]
    [InlineData(2024)]
    public void GeneratedMatchesNeverAttemptToBlockAKeeper(int seed)
    {
        var roster = MatchSimulator.StandardRoster();

        var game = new BlitzGame();
        game.ApplyRoster(roster);

        var parser = new ChatParser(game);
        LogReplay.Replay(new MatchSimulator(roster, seed).Generate(), parser);

        var refused = game.PlayByPlay.Where(l => l.Contains("cannot be blocked")).ToList();

        Assert.True(refused.Count == 0,
            $"Generated match tried to block a keeper {refused.Count} time(s):{Environment.NewLine}" +
            string.Join(Environment.NewLine, refused.Take(3)));
    }

    /// <summary>And keepers themselves never declare a block, having no such action.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2024)]
    public void KeepersNeverDeclareABlock(int seed)
    {
        var roster = MatchSimulator.StandardRoster();

        var keeperNames = roster.Entries
            .Where(e => e.Role == PlayerRole.Goalkeeper)
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var line in new MatchSimulator(roster, seed).Generate())
        {
            if (!keeperNames.Contains(line.Sender)) continue;

            Assert.DoesNotContain("[BLOCK", line.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
