using BlitzballTracker.Core.GameState;
using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// Telling a world from a surname.
///
/// A crossworld name reaches the plugin welded together — "Akii MalaguldCactuar" —
/// because the world icon between them is a payload that does not survive being
/// flattened to text. Nothing in the string marks the join, so the world has to be
/// recognised by name. Get this wrong in a cross-world league and you do not lose one
/// player, you lose everyone not on your own world.
///
/// The names here are real-shaped but invented.
/// </summary>
public class WorldNameTests
{
    [Theory]
    [InlineData("Akii MalaguldCactuar", "Akii Malaguld", "Cactuar")]
    [InlineData("Qasim Abd-al-daiyaHalicarnassus", "Qasim Abd-al-daiya", "Halicarnassus")]
    [InlineData("S'cylle AldmiirCoeurl", "S'cylle Aldmiir", "Coeurl")]
    [InlineData("Laguz Djt-maroucMateus", "Laguz Djt-marouc", "Mateus")]
    [InlineData("Y'tonga NunhZalera", "Y'tonga Nunh", "Zalera")]
    public void AFusedWorldIsSplitOff(string raw, string name, string world)
    {
        var (gotName, gotWorld) = Worlds.Split(raw);

        Assert.Equal(name, gotName);
        Assert.Equal(world, gotWorld);
    }

    /// <summary>The spaced spelling turns up too, and means the same thing.</summary>
    [Fact]
    public void ASpacedWorldIsSplitOff()
    {
        var (name, world) = Worlds.Split("Akii Malaguld Cactuar");

        Assert.Equal("Akii Malaguld", name);
        Assert.Equal("Cactuar", world);
    }

    /// <summary>
    /// The guard that keeps this from eating real names. Several worlds are ordinary
    /// enough words to be a surname, and a player really called "Rielle Phoenix" must
    /// survive intact — cutting the world off would leave "Rielle", which matches nobody.
    /// </summary>
    [Theory]
    [InlineData("Rielle Phoenix")]
    [InlineData("Marcus Titan")]
    [InlineData("Ysera Shiva")]
    public void ASurnameThatHappensToBeAWorldIsKept(string raw)
    {
        var (name, world) = Worlds.Split(raw);

        Assert.Equal(raw, name);
        Assert.Null(world);
    }

    [Fact]
    public void AnOrdinaryNameIsLeftAlone()
    {
        var (name, world) = Worlds.Split("Beki Dotharl");

        Assert.Equal("Beki Dotharl", name);
        Assert.Null(world);
    }

    /// <summary>
    /// The whole point of the fix: a fused name has to compare equal to the roster
    /// entry, because that comparison is the only thing standing between a player and
    /// being ignored for the entire match.
    /// </summary>
    [Fact]
    public void AFusedNameNormalizesOntoItsRosterEntry()
    {
        Assert.Equal(
            PlayerNames.Normalize("Akii Malaguld"),
            PlayerNames.Normalize("Akii MalaguldCactuar"));
    }

    [Fact]
    public void TheBracketedSpellingStillWorks()
    {
        Assert.Equal("Beki Dotharl", PlayerNames.StripWorld("Beki Dotharl [Mateus]"));
        Assert.Equal("Mateus", PlayerNames.ExtractWorld("Beki Dotharl [Mateus]"));
    }

    [Fact]
    public void TheFusedSpellingReportsItsWorldToo()
    {
        Assert.Equal("Cactuar", PlayerNames.ExtractWorld("Akii MalaguldCactuar"));
    }
}
