using BlitzballTracker.Core.GameState;
using BlitzballTracker.Core.Simulation;
using Xunit;

namespace BlitzballTracker.Tests;

public class PhaseTimingTests
{
    [Theory]
    [InlineData(GamePhase.OuterHuddle)]
    [InlineData(GamePhase.InnerHuddle)]
    public void HuddlesAreTheShortPlanningWindow(GamePhase phase)
        => Assert.Equal(PhaseTiming.Huddle, PhaseTiming.For(phase));

    [Theory]
    [InlineData(GamePhase.OuterPhase)]
    [InlineData(GamePhase.InnerPhase)]
    [InlineData(GamePhase.BallCarrierOuter)]
    [InlineData(GamePhase.BallCarrierInner)]
    public void ActionPhasesRunAboutAMinute(GamePhase phase)
        => Assert.Equal(PhaseTiming.Action, PhaseTiming.For(phase));

    [Theory]
    [InlineData(GamePhase.PreGame)]
    [InlineData(GamePhase.Halftime)]
    [InlineData(GamePhase.PostGame)]
    public void RefereeDrivenPhasesHaveNoClock(GamePhase phase)
        => Assert.Null(PhaseTiming.For(phase));

    /// <summary>
    /// The mistake this guards against: the late-roll window was originally a full
    /// minute, the same length as a phase, so a straggling roll could attach itself
    /// to an action from a phase that had already finished.
    /// </summary>
    [Fact]
    public void LateRollGraceIsWellInsideASinglePhase()
    {
        Assert.True(PhaseTiming.LateRollGrace < PhaseTiming.Action,
            "A late roll must not be able to reach back into the previous phase.");

        Assert.True(PhaseTiming.LateRollGrace <= PhaseTiming.Action / 2,
            "Leave clear daylight between the grace window and a full phase.");
    }

    [Fact]
    public void ChangingPhaseRestartsTheClock()
    {
        var game = new BlitzGame { Phase = GamePhase.OuterPhase };
        var started = game.PhaseStartedAt;

        // Re-assigning the same phase must not restart the countdown, or a repeated
        // announcement would hand everyone a fresh minute.
        game.Phase = GamePhase.OuterPhase;
        Assert.Equal(started, game.PhaseStartedAt);

        game.Phase = GamePhase.InnerPhase;
        Assert.True(game.PhaseStartedAt >= started);
    }

    [Fact]
    public void UntimedPhasesReportNoRemainingTime()
    {
        var game = new BlitzGame { Phase = GamePhase.Halftime };
        Assert.Null(game.PhaseRemaining);
    }

    [Fact]
    public void TimedPhasesCountDownFromTheirDuration()
    {
        var game = new BlitzGame { Phase = GamePhase.OuterPhase };

        var remaining = game.PhaseRemaining;
        Assert.NotNull(remaining);
        Assert.True(remaining <= PhaseTiming.Action);
        Assert.True(remaining > PhaseTiming.Action - TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Generated matches must span realistic time. Late-roll handling keys off
    /// message timestamps, so a match crammed into a few seconds per line would
    /// never exercise it.
    /// </summary>
    [Fact]
    public void GeneratedMatchesSpanRealisticTime()
    {
        var roster = MatchSimulator.StandardRoster();
        var lines = new MatchSimulator(roster, 5, new SimulationOptions
        {
            Sets = 1,
            RoundsPerSet = 4,
        }).Generate();

        var span = lines[^1].Timestamp - lines[0].Timestamp;

        // Four rounds, two timed segments each, roughly a minute apiece.
        Assert.True(span >= TimeSpan.FromMinutes(6),
            $"A four-round match only spanned {span.TotalMinutes:0.0} minutes.");
    }
}
