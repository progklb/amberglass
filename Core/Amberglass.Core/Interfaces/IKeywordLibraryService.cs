using Amberglass.Core.Models;

namespace Amberglass.Core.Interfaces;

/// <summary>
/// Stores the user's keyword vocabulary.
///
/// Separate from <see cref="ISettingsService"/> because it is content rather than preference: it can
/// grow to hundreds of entries, people will want to back it up or move it between machines, and it
/// deserves a file of its own rather than swelling settings.json.
/// </summary>
public interface IKeywordLibraryService
{
    KeywordLibrary Current { get; }

    event EventHandler<KeywordLibrary>? Changed;

    Task SaveAsync(KeywordLibrary library, CancellationToken cancellationToken = default);
}
