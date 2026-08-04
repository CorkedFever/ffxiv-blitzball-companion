using Xunit;

namespace BlitzballTracker.Tests;

/// <summary>
/// A test that needs a real recorded match to run against, and skips itself when there
/// is not one.
///
/// Chat logs are kept out of the repository — they are full of real players' character
/// names — so these cannot be part of the suite everybody runs. Put a log at
/// <c>BlitzballTracker.Tests/Fixtures/real-match-sample.log</c> and they start running;
/// without it a fresh clone still goes green rather than failing on a missing file.
/// </summary>
public sealed class RealMatchFactAttribute : FactAttribute
{
    public RealMatchFactAttribute()
    {
        if (!Fixtures.HasRealMatchSample)
            Skip = "No real match sample present; chat logs are kept out of the repository.";
    }
}
