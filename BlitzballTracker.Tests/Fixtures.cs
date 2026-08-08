using BlitzballTracker.Core.GameState;

namespace BlitzballTracker.Tests;

public static class Fixtures
{
    /// <summary>
    /// A sample of a real recorded match, if one is present next to the test assembly.
    ///
    /// Generated matches cover behaviour and edge cases; this covers the messiness
    /// of real human chat, which a generator will not reproduce faithfully: stray
    /// spacing, world suffixes, smart quotes, inconsistent bracket styles.
    ///
    /// **Deliberately not in the repository.** Match logs are full of real players'
    /// character names, and those belong to the people who played rather than to this
    /// project. Drop one in <c>BlitzballTracker.Tests/Fixtures/</c> locally and the
    /// tests that use it come to life; without it they skip, so a fresh clone still
    /// builds and runs green.
    /// </summary>
    public static string RealMatchSample =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "real-match-sample.log");

    public static bool HasRealMatchSample => File.Exists(RealMatchSample);

    /// <summary>
    /// A folder of recordings made by the plugin's own record button, for measuring how
    /// much of a real match the parser actually resolves.
    ///
    /// Set <c>BLITZ_LOGS</c> to point at one — the plugin writes them to its config
    /// directory under <c>recordings/</c>. Kept out of the repository for the same
    /// reason as the sample above: they are somebody's real match.
    /// </summary>
    public static string? RecordingsDirectory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("BLITZ_LOGS");
            return string.IsNullOrWhiteSpace(configured) ? null : configured;
        }
    }

    public static bool HasRecordings =>
        RecordingsDirectory is { } dir &&
        Directory.Exists(dir) &&
        Directory.EnumerateFiles(dir, "*.txt").Any();

    /// <summary>
    /// Match officials and commentators from the sampled match.
    ///
    /// Two of them post bracketed game notation on behalf of others: O'looqa Honji
    /// and Ffon Aveross announce goalkeeper bonuses and the score line, while
    /// Ziandria Dorthal posts possession. None of them are players.
    /// </summary>
    public static readonly string[] RealMatchNonPlayers =
    [
        "O'looqa Honji",        // referee: phase calls, GK bonus announcements
        "Ffon Aveross",         // scorekeeper: score line, GK status
        "Ziandria Dorthal",     // scorekeeper: ball possession
        "Lakaera Riverthorn",   // commentator
        "R'ahreh Khirnha",      // commentator
    ];

    /// <summary>
    /// Best-effort reconstruction of the sampled match's lineup.
    ///
    /// This is a test fixture, not verified ground truth: the roster is exactly what
    /// cannot be recovered from a log, which is why the feature exists. Teams are
    /// evidence-based (possession lines and who blocks whom); the specific role
    /// assignments are plausible filler. Tests using it assert mechanical properties,
    /// never historical accuracy.
    /// </summary>
    public static Roster RealMatchRoster() => new()
    {
        HomeTeam = "DAIGOROS",
        AwayTeam = "AUSPICES",
        Entries =
        [
            new() { Name = "Mhinco Pokhmhakwaahni", Team = "DAIGOROS", Role = PlayerRole.Midfield },
            new() { Name = "Soren Kell",            Team = "DAIGOROS", Role = PlayerRole.LeftForward },
            new() { Name = "Verre Meiken",          Team = "DAIGOROS", Role = PlayerRole.RightForward },
            new() { Name = "Kota Qalli",            Team = "DAIGOROS", Role = PlayerRole.LeftDefender },
            new() { Name = "Makoto Mifune",         Team = "DAIGOROS", Role = PlayerRole.RightDefender },
            new() { Name = "Mirita Ebenae",         Team = "DAIGOROS", Role = PlayerRole.Goalkeeper },

            new() { Name = "Manami Tsukino",        Team = "AUSPICES", Role = PlayerRole.LeftDefender },
            new() { Name = "J'dextera Sol",         Team = "AUSPICES", Role = PlayerRole.Goalkeeper },
            new() { Name = "Soleil Mas",            Team = "AUSPICES", Role = PlayerRole.Midfield },
            new() { Name = "Sataya Saoraigne",      Team = "AUSPICES", Role = PlayerRole.LeftForward },
            new() { Name = "Bucharattui Hiragad",   Team = "AUSPICES", Role = PlayerRole.RightForward },
            new() { Name = "Tomatoka Matoka",       Team = "AUSPICES", Role = PlayerRole.RightDefender },
        ],
    };
}
