namespace BlitzballTracker.UI.Views;

/// <summary>
/// One screen inside the shell.
///
/// Views draw their contents and nothing else: the shell owns the window, the
/// navigation, the transitions and the surrounding chrome. That split is what keeps
/// every screen looking like the same product.
/// </summary>
public interface IShellView
{
    /// <summary>Label shown in the navigation rail.</summary>
    string Title { get; }

    /// <summary>Glyph shown beside the label.</summary>
    string Icon { get; }

    /// <summary>Optional badge text, e.g. a count needing attention. Null when nothing to say.</summary>
    string? Badge => null;

    void Draw();
}
