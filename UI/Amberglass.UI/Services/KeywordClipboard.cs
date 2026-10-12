using System.Collections.Immutable;

namespace Amberglass.UI.Services;

/// <summary>
/// A set of keywords copied from one photograph, ready to be applied to others.
///
/// Deliberately not the system clipboard. Keywords copied here would be destroyed by the next
/// ordinary copy — a file path, a word from a caption — and pasting a set of tags is not something
/// anyone wants to lose by copying something else first. It is also the wrong shape: the system
/// clipboard carries text, and this carries a set with an identity.
/// </summary>
public interface IKeywordClipboard
{
    ImmutableArray<string> Keywords { get; }

    bool HasKeywords { get; }

    /// <summary>Raised so buttons can enable themselves the moment something is copied.</summary>
    event EventHandler? Changed;

    void Copy(IEnumerable<string> keywords);

    void Clear();
}

public sealed class KeywordClipboard : IKeywordClipboard
{
    public ImmutableArray<string> Keywords { get; private set; } = [];

    public bool HasKeywords => Keywords.Length > 0;

    public event EventHandler? Changed;

    public void Copy(IEnumerable<string> keywords)
    {
        Keywords = [.. keywords
            .Select(keyword => keyword.Trim())
            .Where(keyword => keyword.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        Keywords = [];
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
