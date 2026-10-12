using Amberglass.Core.Models;
using Amberglass.Core.Services;
using Xunit;

namespace Amberglass.Tests;

/// <summary>
/// The search vocabulary, and the guarantee that what the interface offers is what the parser
/// accepts. These are the tests that stop the help and the parser drifting apart.
/// </summary>
public class SearchFieldTests
{
    /// <summary>
    /// Every advertised field must actually filter. Colour label was in the first draft of the
    /// catalogue and is not in <c>SearchQuery</c> at all — this is the test that caught it, and it
    /// is the whole reason the catalogue is worth having.
    /// </summary>
    [Fact]
    public void EveryFieldsExampleActuallyFilters()
    {
        Assert.All(SearchFields.All, field =>
        {
            var query = SearchQueryParser.Parse(field.Example);

            Assert.True(query.UnrecognisedTerms.IsDefaultOrEmpty,
                $"{field.Name}: the example '{field.Example}' was not understood.");

            Assert.False(query.IsEmpty, $"{field.Name}: the example '{field.Example}' filtered nothing.");

            // A field whose example lands in free text is not filtering by that field at all — it
            // would still "work", and would quietly match the wrong rows.
            Assert.True(query.FreeText.IsDefaultOrEmpty,
                $"{field.Name}: the example '{field.Example}' fell through to free text.");
        });
    }

    /// <summary>Every spelling the catalogue claims has to resolve to its own field.</summary>
    [Fact]
    public void EverySpellingResolvesToItsField()
    {
        Assert.All(SearchFields.All, field =>
            Assert.All(field.AllSpellings, spelling =>
                Assert.Equal(field.Name, SearchFields.Resolve(spelling))));
    }

    [Fact]
    public void SpellingsAreCaseInsensitive()
    {
        Assert.Equal("keyword", SearchFields.Resolve("KEYWORD"));
        Assert.Equal("keyword", SearchFields.Resolve("K"));
    }

    /// <summary>
    /// The short forms are the point of the exercise, so they are asserted by name rather than only
    /// through the catalogue — a typo that swapped two of them would otherwise pass every other test.
    /// </summary>
    [Theory]
    [InlineData("k", "keyword")]
    [InlineData("r", "rating")]
    [InlineData("t", "type")]
    [InlineData("c", "camera")]
    [InlineData("l", "lens")]
    [InlineData("d", "date")]
    public void ShortFormsMeanWhatTheyLookLike(string shortForm, string expected)
    {
        Assert.Equal(expected, SearchFields.Resolve(shortForm));
    }

    [Fact]
    public void ShortAndLongFormsProduceTheSameQuery()
    {
        var spelledOut = SearchQueryParser.Parse("keyword:motorcycle rating:>=4 type:video");
        var shortForm = SearchQueryParser.Parse("k:motorcycle r:>=4 t:video");

        // Compared part by part rather than as whole records: SearchQuery holds ImmutableArrays,
        // whose equality is by underlying reference, so two identical queries are never Equal.
        // ToArray on both sides: xUnit compares an ImmutableArray against a string[] as different
        // collection types and reports "Collections differ" over two identical lists.
        Assert.Equal(spelledOut.Keywords.Select(k => k.AnyOf.ToArray()).ToArray(),
            shortForm.Keywords.Select(k => k.AnyOf.ToArray()).ToArray());
        Assert.Equal(spelledOut.Rating, shortForm.Rating);
        Assert.Equal(spelledOut.Kinds.ToArray(), shortForm.Kinds.ToArray());
        Assert.Equal(spelledOut.FreeText.ToArray(), shortForm.FreeText.ToArray());
    }

    /// <summary>The alias that shipped before the short forms existed still has to work.</summary>
    [Fact]
    public void TheOlderKeywordAliasStillWorks()
    {
        Assert.Equal([["sand"]], SearchQueryParser.Parse("kw:sand").Keywords.Select(k => k.AnyOf.ToArray()).ToArray());
    }

    [Fact]
    public void SomethingThatIsNotAFieldResolvesToNothing()
    {
        Assert.Null(SearchFields.Resolve("nonsense"));
        Assert.Null(SearchFields.Resolve("http"));
    }

    /// <summary>
    /// A word that only looks like a field must stay free text, so pasting a URL into the box still
    /// searches for it.
    /// </summary>
    [Fact]
    public void AnUnknownFieldIsSearchedForAsText()
    {
        var query = SearchQueryParser.Parse("http://example.com/photo");

        Assert.Contains("http://example.com/photo", query.FreeText);
    }

    [Fact]
    public void ABareColonOffersEverything()
    {
        Assert.Equal(SearchFields.All.Length, SearchFields.Matching(string.Empty).Count());
        Assert.Equal(SearchFields.All.Length, SearchFields.Matching(null).Count());
    }

    [Fact]
    public void APrefixNarrowsTheOffer()
    {
        var matches = SearchFields.Matching("k").ToList();

        Assert.Single(matches);
        Assert.Equal("keyword", matches[0].Name);
    }

    /// <summary>
    /// Two fields claiming one spelling would make a search filter by the wrong thing, so the
    /// catalogue refuses to build. Touching it is enough to run this.
    /// </summary>
    [Fact]
    public void TheCatalogueHasNoClashingSpellings()
    {
        var spellings = SearchFields.All.SelectMany(field => field.AllSpellings).ToList();

        Assert.Equal(spellings.Count, spellings.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}

/// <summary>
/// What the search box offers at the caret, and what happens to the text when something is picked.
/// </summary>
public class SearchSuggestionTests
{
    private static SuggestionRequest At(string text, int caret) => SearchSuggestion.At(text, caret);

    // ---- Choosing a field -------------------------------------------------------------------------

    [Theory]
    [InlineData(":", 1, "")]              // a bare colon means "remind me"
    [InlineData("key:", 4, "key")]        // a partly typed field, not yet a real one
    [InlineData("sand ke:", 8, "ke")]     // the token being typed, not the whole box
    public void OffersFieldsWhileTheWordIsNotYetAField(string text, int caret, string expected)
    {
        var request = At(text, caret);

        Assert.Equal(SuggestionKind.Field, request.Kind);
        Assert.Equal(expected, request.Prefix);
    }

    /// <summary>
    /// The rule that makes it feel immediate: a colon after something that <i>is</i> already a field
    /// moves straight on to that field's values rather than confirming the field back at you.
    /// </summary>
    [Theory]
    [InlineData("k:", 2, "keyword", "")]
    [InlineData("keyword:", 8, "keyword", "")]
    [InlineData("k:sa", 4, "keyword", "sa")]
    [InlineData("lb:sel", 6, "label", "sel")]
    public void OffersValuesOnceTheFieldIsKnown(string text, int caret, string field, string prefix)
    {
        var request = At(text, caret);

        Assert.Equal(SuggestionKind.Value, request.Kind);
        Assert.Equal(field, request.Field);
        Assert.Equal(prefix, request.Prefix);
    }

    /// <summary>Commas separate alternatives, so only the one being typed is completed.</summary>
    [Theory]
    [InlineData("k:sand,", 7, "")]
    [InlineData("k:sand,du", 9, "du")]
    public void OnlyTheAlternativeBeingTypedIsCompleted(string text, int caret, string expected)
    {
        var request = At(text, caret);

        Assert.Equal(SuggestionKind.Value, request.Kind);
        Assert.Equal(expected, request.Prefix);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("sand", 4)]                  // free text, no colon
    [InlineData("http://example.com", 18)]   // a word with a colon is not a filter
    public void StaysQuietOtherwise(string text, int caret)
    {
        Assert.Equal(SuggestionKind.None, At(text, caret).Kind);
    }

    /// <summary>The caret is rarely at the end; what follows it is another term, not a prefix.</summary>
    [Fact]
    public void OffersWhereTheCaretIsRatherThanAtTheEnd()
    {
        var request = At("k:sa t:video", 4);

        Assert.Equal(SuggestionKind.Value, request.Kind);
        Assert.Equal("keyword", request.Field);
        Assert.Equal("sa", request.Prefix);
    }

    // ---- Accepting --------------------------------------------------------------------------------

    [Fact]
    public void AcceptingAFieldReplacesTheHalfTypedName()
    {
        var (text, caret) = SearchSuggestion.AcceptField("key:", 4, "keyword");

        Assert.Equal("keyword:", text);
        Assert.Equal(8, caret);
    }

    [Fact]
    public void AcceptingAFieldLeavesTheRestOfTheQueryAlone()
    {
        var (text, caret) = SearchSuggestion.AcceptField("sand r:", 7, "rating");

        Assert.Equal("sand rating:", text);
        Assert.Equal(12, caret);
    }

    [Fact]
    public void AcceptingAValueCompletesIt()
    {
        var (text, caret) = SearchSuggestion.AcceptValue("k:sa", 4, "sand");

        Assert.Equal("k:sand", text);
        Assert.Equal(6, caret);
    }

    /// <summary>Earlier alternatives in the same term survive.</summary>
    [Fact]
    public void AcceptingAValueKeepsTheOnesAlreadyChosen()
    {
        var (text, caret) = SearchSuggestion.AcceptValue("k:sand,du", 9, "dust");

        Assert.Equal("k:sand,dust", text);
        Assert.Equal(11, caret);
    }

    [Fact]
    public void AcceptingAValueIntoAnEmptyTermJustWritesIt()
    {
        var (text, caret) = SearchSuggestion.AcceptValue("k:", 2, "sand");

        Assert.Equal("k:sand", text);
        Assert.Equal(6, caret);
    }

    /// <summary>What comes after the caret survives, so completing mid-query does not truncate it.</summary>
    [Fact]
    public void AcceptingAValueKeepsWhatComesAfterTheCaret()
    {
        var (text, caret) = SearchSuggestion.AcceptValue("k:sa t:video", 4, "sand");

        Assert.Equal("k:sand t:video", text);
        Assert.Equal(6, caret);
    }

    /// <summary>
    /// A keyword with a space has to come back quoted, or the term ends at the space and the rest
    /// becomes free text.
    /// </summary>
    [Fact]
    public void AValueWithASpaceComesBackQuoted()
    {
        var (text, _) = SearchSuggestion.AcceptValue("k:gol", 5, "Golden Hour");

        Assert.Equal("k:\"Golden Hour\"", text);
    }

    /// <summary>Completing a value has to leave something the parser understands.</summary>
    [Fact]
    public void WhatIsCompletedIsWhatIsParsed()
    {
        var (text, _) = SearchSuggestion.AcceptValue("k:sand,du", 9, "dust");

        var query = SearchQueryParser.Parse(text);

        Assert.Single(query.Keywords);
        Assert.Equal(["sand", "dust"], query.Keywords[0].AnyOf.ToArray());
    }
}

/// <summary>
/// How several values combine, which was the thing nobody could tell from the interface.
/// </summary>
public class SearchCombinationTests
{
    /// <summary>Repeating a field asks for all of them.</summary>
    [Fact]
    public void RepeatingKeywordMeansAllOfThem()
    {
        var query = SearchQueryParser.Parse("k:sand k:dust");

        Assert.Equal(2, query.Keywords.Length);
        Assert.Equal(["sand"], query.Keywords[0].AnyOf.ToArray());
        Assert.Equal(["dust"], query.Keywords[1].AnyOf.ToArray());
    }

    /// <summary>
    /// A comma asks for any of them. Before this, "k:sand,dust" looked for a single keyword
    /// literally named "sand,dust" and therefore matched nothing at all — valid-looking and silent.
    /// </summary>
    [Fact]
    public void CommaInKeywordMeansAnyOfThem()
    {
        var query = SearchQueryParser.Parse("k:sand,dust");

        Assert.Single(query.Keywords);
        Assert.Equal(["sand", "dust"], query.Keywords[0].AnyOf.ToArray());
    }

    [Fact]
    public void CommaInTypeMeansAnyOfThem()
    {
        Assert.Equal(
            [MediaKind.Raw, MediaKind.Video],
            SearchQueryParser.Parse("t:raw,video").Kinds.ToArray());
    }

    /// <summary>
    /// The question that prompted all this: do rating, keyword and type combine? They do, and every
    /// field narrows the result together with the others.
    /// </summary>
    [Fact]
    public void RatingKeywordAndTypeCombine()
    {
        var query = SearchQueryParser.Parse("r:>=4 k:sand,dust k:wide t:raw c:Fujifilm");

        Assert.Equal(4, query.Rating!.Value);
        Assert.Equal(ComparisonOperator.GreaterThanOrEqual, query.Rating.Operator);
        Assert.Equal([MediaKind.Raw], query.Kinds.ToArray());
        Assert.Equal(["Fujifilm"], query.Cameras.ToArray());

        Assert.Equal(2, query.Keywords.Length);
        Assert.Equal(["sand", "dust"], query.Keywords[0].AnyOf.ToArray());
        Assert.Equal(["wide"], query.Keywords[1].AnyOf.ToArray());

        Assert.Empty(query.UnrecognisedTerms);
        Assert.Empty(query.FreeText);
    }
}

/// <summary>
/// The writing half of the filter panel's keyword picker.
///
/// The picker never holds a filter of its own — it edits the query text and reads it back — so
/// "any" and "all" are only correct if the two spellings survive a round trip through the parser.
/// That round trip is what these check, rather than the string that comes out.
/// </summary>
public class KeywordFilterWritingTests
{
    [Fact]
    public void AnyIsOneTermWithCommas()
    {
        var text = SearchQueryText.WithFieldTerms(string.Empty, "keyword", ["sand,dust"]);

        var query = SearchQueryParser.Parse(text);

        // One group offering alternatives: a file needs either word, not both.
        Assert.Single(query.Keywords);
        Assert.Equal(new[] { "sand", "dust" }, query.Keywords[0].AnyOf.ToArray());
    }

    [Fact]
    public void AllIsOneTermEach()
    {
        var text = SearchQueryText.WithFieldTerms(string.Empty, "keyword", ["sand", "dust"]);

        var query = SearchQueryParser.Parse(text);

        // Two groups, each with one word: every group has to match, so a file needs both.
        Assert.Equal(2, query.Keywords.Length);
        Assert.All(query.Keywords, group => Assert.Single(group.AnyOf));
    }

    [Fact]
    public void GroupCountDistinguishesAnyFromAll()
    {
        // This is exactly what the panel reads to set the any/all switch, so it is worth pinning:
        // the two spellings have to differ in group count or the switch cannot be restored.
        var any = SearchQueryParser.Parse(SearchQueryText.WithFieldTerms(null, "keyword", ["sand,dust"]));
        var all = SearchQueryParser.Parse(SearchQueryText.WithFieldTerms(null, "keyword", ["sand", "dust"]));

        Assert.Single(any.Keywords);
        Assert.Equal(2, all.Keywords.Length);

        // Both name the same words, which is why the count is the only thing that tells them apart.
        Assert.Equal(
            any.Keywords.SelectMany(g => g.AnyOf).OrderBy(w => w).ToArray(),
            all.Keywords.SelectMany(g => g.AnyOf).OrderBy(w => w).ToArray());
    }

    [Fact]
    public void ReplacesEverySpellingOfTheField()
    {
        // Ticking a box has to clear what was typed, including the long form and any earlier terms,
        // or the panel would show one filter while the query ran another.
        var text = SearchQueryText.WithFieldTerms(
            "keyword:old r:>=3 k:older", "keyword", ["sand", "dust"]);

        var query = SearchQueryParser.Parse(text);

        Assert.Equal(new[] { "sand", "dust" }, query.Keywords.SelectMany(g => g.AnyOf).ToArray());

        // Other fields are left alone: the picker edits one field, not the query.
        Assert.NotNull(query.Rating);
    }

    [Fact]
    public void UntickingEverythingRemovesTheTerm()
    {
        var text = SearchQueryText.WithFieldTerms("k:sand k:dust r:>=3", "keyword", []);

        Assert.Empty(SearchQueryParser.Parse(text).Keywords);
        Assert.Equal("r:>=3", text);
    }

    [Fact]
    public void KeywordsWithSpacesComeBackWhole()
    {
        // Ticked from a list, so a two-word keyword is entirely ordinary here — unquoted it would
        // tokenize as two terms and filter for something nothing has.
        var text = SearchQueryText.WithFieldTerms(null, "keyword", ["golden hour", "sand"]);

        var query = SearchQueryParser.Parse(text);

        Assert.Equal(2, query.Keywords.Length);
        Assert.Contains("golden hour", query.Keywords.SelectMany(g => g.AnyOf));
    }
}

/// <summary>
/// What the filter controls do with whatever was already in the search box.
///
/// They edit one field and leave the rest, which is what lets free text and filters combine. The
/// exception is a term someone abandoned half-typed: a bare colon is not neutral, it becomes a
/// free-text term that matches nothing and empties the results.
/// </summary>
public class FilterWritingOverExistingTextTests
{
    [Fact]
    public void ALoneColonIsCleared()
    {
        // Typing ":" opens the field list. Going to the filter panel instead used to leave it there,
        // and ": r:>=3" then found nothing at all.
        var text = SearchQueryText.WithField(":", "rating", ">=3");

        Assert.Equal("r:>=3", text);
        Assert.Empty(SearchQueryParser.Parse(text).FreeText);
    }

    [Fact]
    public void AFieldWithNoValueYetIsCleared()
    {
        var text = SearchQueryText.WithField("k:", "rating", ">=3");

        Assert.Equal("r:>=3", text);
        Assert.Empty(SearchQueryParser.Parse(text).UnrecognisedTerms);
    }

    [Fact]
    public void FreeTextIsKept()
    {
        // The whole point of writing into the box rather than holding a filter apart from it: a word
        // and a filter are a legitimate query together, and clicking a star must not discard typing.
        var text = SearchQueryText.WithField("bush", "rating", ">=3");

        var query = SearchQueryParser.Parse(text);

        Assert.Equal(new[] { "bush" }, query.FreeText.ToArray());
        Assert.NotNull(query.Rating);
    }

    [Fact]
    public void OtherFieldsAreKept()
    {
        var text = SearchQueryText.WithField("k:sand : f:accepted", "rating", ">=3");

        var query = SearchQueryParser.Parse(text);

        Assert.Single(query.Keywords);
        Assert.Single(query.Flags);
        Assert.NotNull(query.Rating);
        Assert.Empty(query.FreeText);
    }

    [Fact]
    public void AColonWithSomethingAfterItIsFreeTextAndStays()
    {
        // ":foo" reaches the index as a search for foo, because the tokenizer drops the punctuation.
        // It works, so it is not ours to throw away.
        var text = SearchQueryText.WithField(":foo", "rating", ">=3");

        Assert.Equal(new[] { ":foo" }, SearchQueryParser.Parse(text).FreeText.ToArray());
    }

    [Fact]
    public void TheKeywordPickerClearsThemToo()
    {
        // Both writers, since every filter control goes through one or the other.
        var text = SearchQueryText.WithFieldTerms("k: :", "keyword", ["sand", "dust"]);

        var query = SearchQueryParser.Parse(text);

        Assert.Equal(2, query.Keywords.Length);
        Assert.Empty(query.FreeText);
        Assert.Empty(query.UnrecognisedTerms);
    }

    [Fact]
    public void ClearingTheLastFilterLeavesAnEmptyBox()
    {
        // Not " " or ":", which would still be a query — unticking everything has to leave nothing.
        Assert.Equal(string.Empty, SearchQueryText.WithField(":", "rating", null));
        Assert.Equal(string.Empty, SearchQueryText.WithFieldTerms("k:sand :", "keyword", []));
    }
}
