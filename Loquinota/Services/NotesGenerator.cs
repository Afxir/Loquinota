using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Loquinota.Models;

namespace Loquinota.Services;

/// <summary>
/// Turns a raw transcript into a structured, bullet-point note.
/// The text-shaping logic is pure and deterministic (no UI, no I/O); only the note header mirrors
/// <see cref="BuildTitle"/> by stamping the current local date/time, so the note matches the
/// session it belongs to.
/// </summary>
public static class NotesGenerator
{
    /// <summary>Em dash used between a source label and its subject in titles.</summary>
    private const string Dash = "\u2014";

    /// <summary>Maximum length of the overview gist sentence (keeps notes scannable).</summary>
    private const int GistMaxLength = 240;

    /// <summary>
    /// Splits after sentence punctuation, but only when the next sentence clearly starts
    /// (capital letter, digit, quote, bracket) so decimals like 3.14 are never split.
    /// </summary>
    private static readonly Regex SentenceBoundary = new(
        @"(?<=[.!?])\s+(?=[A-Z0-9""'\(\[]|\u201C|\u2018)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Single-letter initialisms such as "U.S." or "J." that must not end a sentence.</summary>
    private static readonly Regex Initialism = new(
        @"^(?:[A-Z]\.)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Common abbreviations (without the trailing dot) that must not end a sentence.
    /// "a.m."/"p.m." are deliberately absent: they are very often sentence-final, and when they are
    /// mid-sentence the next word is normally lower-case, so no split happens in the first place.
    /// </summary>
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "mt", "vs", "etc", "e.g", "i.e",
        "u.s", "u.k", "no", "nos", "vol", "vols", "fig", "figs", "approx", "min", "mins", "max", "sec",
        "secs", "hr", "hrs", "inc", "ltd", "co", "corp", "dept", "est", "cf", "al", "jan", "feb", "mar",
        "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec", "mon", "tue", "tues", "wed",
        "thu", "thur", "thurs", "fri", "sat", "sun"
    };

    /// <summary>
    /// Builds the note text shown/saved for a session:
    ///  - a header with source, date and duration,
    ///  - a short "Overview" section,
    ///  - bullet points grouped into paragraphs, keeping timestamp prefixes when segments exist.
    /// Never returns null; an empty transcript yields a friendly placeholder note.
    /// </summary>
    public static string Generate(
        string transcript,
        SessionSourceType sourceType,
        TimeSpan duration,
        IReadOnlyList<TranscriptSegment>? segments = null,
        string? sourceFileName = null)
    {
        var transcriptText = CollapseWhitespace(transcript);
        var segmentBullets = BuildSegmentBullets(segments);
        if (transcriptText.Length == 0 && segmentBullets.Count > 0)
        {
            transcriptText = CollapseWhitespace(string.Join(" ", segments!.Select(s => s.Text)));
        }

        var sourceLabel = DescribeSource(sourceType);
        var durationText = FormatDuration(duration);
        var fileName = SafeFileName(sourceFileName);

        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(BuildTitle(sourceType, DateTimeOffset.Now, sourceFileName));
        builder.AppendLine();
        builder.Append("- **Source:** ").AppendLine(sourceLabel);
        if (sourceType == SessionSourceType.Mp4 && fileName.Length > 0)
        {
            builder.Append("- **File:** ").AppendLine(fileName);
        }

        builder.Append("- **Duration:** ").AppendLine(durationText);
        builder.AppendLine();

        // Empty transcript: friendly, structured placeholder instead of an empty note.
        if (transcriptText.Length == 0)
        {
            builder.AppendLine("## Overview");
            builder.AppendLine("No speech was detected in this recording, so there is nothing to summarise yet.");
            builder.AppendLine();
            builder.AppendLine("## Key points");
            builder.AppendLine("- No speech was detected in the audio.");
            builder.AppendLine("- Try recording again, or import a different file that contains audible speech.");
            return Finish(builder);
        }

        var transcriptBullets = ToBulletPoints(transcriptText);
        var bullets = segmentBullets.Count > 0 ? segmentBullets : transcriptBullets;
        if (bullets.Count == 0)
        {
            bullets = new List<string> { Truncate(transcriptText, 500) };
        }

        var wordCount = CountWords(transcriptText);
        var sentenceCount = Math.Max(1, transcriptBullets.Count);
        var gist = Truncate(transcriptBullets.Count > 0 ? transcriptBullets[0] : transcriptText, GistMaxLength);

        builder.AppendLine("## Overview");
        builder.Append("This ").Append(sourceLabel.ToLowerInvariant())
            .Append(" contains about ").Append(Pluralize(wordCount, "word"))
            .Append(" of transcript across ").Append(Pluralize(sentenceCount, "sentence"))
            .Append(" and lasts ").Append(durationText)
            .Append(". It opens with: \u201C").Append(gist).AppendLine("\u201D");
        builder.AppendLine();
        builder.AppendLine("## Key points");
        foreach (var bullet in bullets)
        {
            builder.Append("- ").AppendLine(bullet);
        }

        return Finish(builder);
    }

    /// <summary>Builds a readable default session title, e.g. "Live capture — 17 Oct 2026 19:42".</summary>
    public static string BuildTitle(SessionSourceType sourceType, DateTimeOffset createdAt, string? sourceFileName = null)
    {
        var stamp = createdAt.LocalDateTime.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture);
        var fileName = SafeFileName(sourceFileName);

        return sourceType switch
        {
            SessionSourceType.Mp4 when fileName.Length > 0 => $"Meeting recording {Dash} {fileName}",
            SessionSourceType.Mp4 => $"Meeting recording {Dash} {stamp}",
            SessionSourceType.Live => $"Live capture {Dash} {stamp}",
            _ => $"Recording {Dash} {stamp}"
        };
    }

    /// <summary>
    /// Splits transcript text into sentences and returns them as clean bullet lines.
    /// Used by <see cref="Generate"/> and easy to unit-test independently.
    /// </summary>
    public static IReadOnlyList<string> ToBulletPoints(string transcript)
    {
        var bullets = new List<string>();
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return bullets;
        }

        // Paragraphs (and hard line breaks) are treated as bullet boundaries too.
        var paragraphs = transcript
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawParagraph in paragraphs)
        {
            var paragraph = CollapseWhitespace(rawParagraph);
            if (paragraph.Length == 0)
            {
                continue;
            }

            foreach (var rawSentence in SentenceBoundary.Split(paragraph))
            {
                var sentence = CollapseWhitespace(rawSentence);
                if (sentence.Length == 0)
                {
                    continue;
                }

                // Re-join sentences that were split after an abbreviation or initial ("Dr.", "U.S.").
                if (bullets.Count > 0 && EndsWithAbbreviation(bullets[^1]))
                {
                    bullets[^1] = bullets[^1] + " " + sentence;
                    continue;
                }

                if (HasLetterOrDigit(sentence))
                {
                    bullets.Add(sentence);
                }
            }
        }

        return bullets;
    }

    private static List<string> BuildSegmentBullets(IReadOnlyList<TranscriptSegment>? segments)
    {
        var bullets = new List<string>();
        if (segments is null || segments.Count == 0)
        {
            return bullets;
        }

        foreach (var segment in segments)
        {
            var text = CollapseWhitespace(segment.Text);
            if (text.Length == 0 || !HasLetterOrDigit(text))
            {
                continue;
            }

            var stamp = FormatTimestamp(segment.Start);
            bullets.Add($"[{stamp}] {text}");
        }

        return bullets;
    }

    private static bool EndsWithAbbreviation(string sentence)
    {
        var trimmed = sentence.TrimEnd();
        if (!trimmed.EndsWith('.'))
        {
            return false;
        }

        var lastToken = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrEmpty(lastToken))
        {
            return false;
        }

        if (Initialism.IsMatch(lastToken))
        {
            return true;
        }

        var withoutDot = lastToken.TrimEnd('.');
        return withoutDot.Length > 0 && Abbreviations.Contains(withoutDot);
    }

    private static bool HasLetterOrDigit(string text)
    {
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeSource(SessionSourceType sourceType) => sourceType switch
    {
        SessionSourceType.Mp4 => "MP4 import",
        SessionSourceType.Live => "Live capture",
        _ => "Recording"
    };

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatTimestamp(TimeSpan start)
    {
        if (start < TimeSpan.Zero)
        {
            start = TimeSpan.Zero;
        }

        return start.TotalHours >= 1
            ? start.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : start.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static int CountWords(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string CollapseWhitespace(string? text)
        => string.IsNullOrWhiteSpace(text) ? "" : Whitespace.Replace(text, " ").Trim();

    private static string SafeFileName(string? sourceFileName)
    {
        if (string.IsNullOrWhiteSpace(sourceFileName))
        {
            return "";
        }

        try
        {
            return Path.GetFileName(sourceFileName.Trim());
        }
        catch
        {
            return sourceFileName.Trim();
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', Math.Min(maxLength, text.Length - 1));
        if (cut < maxLength / 2)
        {
            cut = maxLength;
        }

        return text[..cut].TrimEnd(',', ';', ':', ' ', '-', '\u2014') + "\u2026";
    }

    private static string Pluralize(int count, string singular)
        => count == 1 ? $"1 {singular}" : $"{count} {singular}s";

    private static string Finish(StringBuilder builder)
        => builder.ToString().TrimEnd() + Environment.NewLine;
}
