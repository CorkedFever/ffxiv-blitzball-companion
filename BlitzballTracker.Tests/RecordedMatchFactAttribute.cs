using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// A test that runs against a folder of recordings made by the plugin's record button,
/// and skips itself when there is not one.
///
/// Point <c>BLITZ_LOGS</c> at the plugin's <c>recordings/</c> folder to switch these on.
/// Like <see cref="RealMatchFactAttribute"/>, the logs stay out of the repository: they
/// are real matches played by real people.
/// </summary>
public sealed class RecordedMatchFactAttribute : FactAttribute
{
    public RecordedMatchFactAttribute()
    {
        if (!Fixtures.HasRecordings)
            Skip = "Set BLITZ_LOGS to a folder of recordings to run this.";
    }
}
