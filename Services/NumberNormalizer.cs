using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WhisperNote.Services;

// Converts spoken English numbers ("twenty five", "three point five",
// "forty dollars") into digits ("25", "3.5", "$40"). Dedicated ASR models
// (Qwen3-ASR) transcribe verbatim and cannot follow the transcription
// prompt, so their parsed output is normalized here. The pass is
// deliberately conservative: anything that is not a well-formed number
// phrase, or that matches a known idiom guard ("one of them", "no one"),
// is left as words.
static class NumberNormalizer
{
    static readonly Dictionary<string, long> Ones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4,
        ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9,
        ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13,
        ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16,
        ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };

    static readonly Dictionary<string, long> Tens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50,
        ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    static readonly Dictionary<string, long> Scales = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hundred"] = 100, ["thousand"] = 1_000, ["million"] = 1_000_000,
        ["billion"] = 1_000_000_000, ["trillion"] = 1_000_000_000_000,
    };

    // "oh" is only a digit after "point" ("three point oh five" -> 3.05).
    static readonly Dictionary<string, char> DecimalDigits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = '0', ["one"] = '1', ["two"] = '2', ["three"] = '3',
        ["four"] = '4', ["five"] = '5', ["six"] = '6', ["seven"] = '7',
        ["eight"] = '8', ["nine"] = '9', ["oh"] = '0',
    };

    static readonly Dictionary<string, string> CurrencySymbols = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dollar"] = "$", ["dollars"] = "$",
        ["euro"] = "€", ["euros"] = "€",
        ["pound"] = "£", ["pounds"] = "£",
    };

    // A standalone "one" in these contexts is an idiom or pronoun, not a quantity.
    static readonly HashSet<string> OnePrevGuards = new(StringComparer.OrdinalIgnoreCase)
    {
        "no", "the", "every", "any", "this", "that", "which", "by",
    };

    static readonly HashSet<string> OneNextGuards = new(StringComparer.OrdinalIgnoreCase)
    {
        "of", "another", "by", "day",
    };

    static readonly string[] NumberWords =
    {
        "trillion", "billion", "million", "thousand", "hundred",
        "nineteen", "eighteen", "seventeen", "sixteen", "fifteen", "fourteen",
        "thirteen", "twelve", "eleven", "ten",
        "nine", "eight", "seven", "six", "five", "four", "three", "two", "one", "zero",
        "ninety", "eighty", "seventy", "sixty", "fifty", "forty", "thirty", "twenty",
        "point", "and", "oh",
    };

    static readonly Regex NumberRun = BuildNumberRunRegex();
    static readonly Regex CentsTail = BuildCentsTailRegex();
    static readonly Regex PrevWord = new(@"([A-Za-z]+)[^A-Za-z]*$", RegexOptions.Compiled);
    static readonly Regex NextWord = new(@"^[^A-Za-z]*([A-Za-z]+)", RegexOptions.Compiled);

    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var matches = NumberRun.Matches(text);
        if (matches.Count == 0)
            return text;

        var sb = new StringBuilder();
        var last = 0;

        foreach (Match match in matches)
        {
            if (match.Index < last)
                continue;

            if (!TryLocateRun(match, out var runIndex, out var runLength, out var words))
            {
                sb.Append(text, last, match.Index + match.Length - last);
                last = match.Index + match.Length;
                continue;
            }

            var replacement = BuildReplacement(text, runIndex, runLength, words, out var consumed);

            sb.Append(text, last, runIndex - last);
            sb.Append(replacement ?? text.Substring(runIndex, runLength));
            last = runIndex + consumed;
        }
        sb.Append(text, last, text.Length - last);
        return sb.ToString();
    }

    static bool IsAnd(string word) => word.Equals("and", StringComparison.OrdinalIgnoreCase);

    // Splits a matched run into words and trims conjunction "and" from its
    // edges ("six and bring" -> run "six"; "and one hundred" -> run "one
    // hundred"). Internal "and" stays ("one hundred and five" -> 105).
    static bool TryLocateRun(Match match, out int runIndex, out int runLength, out string[] words)
    {
        var value = match.Value;
        var allWords = Regex.Split(value, @"[\s\-]+");

        var lo = 0;
        var hi = allWords.Length - 1;
        while (lo < hi && IsAnd(allWords[lo])) lo++;
        while (hi > lo && IsAnd(allWords[hi])) hi--;

        var pos = 0;
        var start = 0;
        for (var i = 0; i <= hi; i++)
        {
            var wordStart = value.IndexOf(allWords[i], pos, StringComparison.OrdinalIgnoreCase);
            pos = wordStart + allWords[i].Length;
            if (i == lo) start = wordStart;
        }

        runIndex = match.Index + start;
        runLength = pos - start;
        words = allWords[lo..(hi + 1)];
        return words.Length > 0;
    }

    // Returns the digit replacement for one number run and how many characters
    // of the input it consumes (more than the run when a currency word
    // follows). A null replacement keeps the original words.
    static string? BuildReplacement(string text, int runIndex, int runLength, string[] words, out int consumed)
    {
        consumed = runLength;

        if (words.Length == 1 && IsGuardedSingleWord(text, runIndex, runLength, words[0]))
            return null;

        string? formatted = FormatNumber(words);
        if (formatted == null)
            return null;

        return TryFormatCurrency(text, runIndex, runLength, formatted, ref consumed) ?? formatted;
    }

    static bool IsGuardedSingleWord(string text, int runIndex, int runLength, string word)
    {
        if (word.Equals("point", StringComparison.OrdinalIgnoreCase) ||
            IsAnd(word) ||
            word.Equals("oh", StringComparison.OrdinalIgnoreCase))
            return true;

        var prev = PrevWord.Match(text.Substring(0, runIndex));
        var next = NextWord.Match(text.Substring(runIndex + runLength));

        if (word.Equals("one", StringComparison.OrdinalIgnoreCase))
        {
            if (prev.Success && OnePrevGuards.Contains(prev.Groups[1].Value)) return true;
            if (next.Success && OneNextGuards.Contains(next.Groups[1].Value)) return true;
        }

        // "thanks a million", "a hundred times": keep the scale word when the
        // indefinite article precedes it.
        if (Scales.ContainsKey(word) && prev.Success &&
            (prev.Groups[1].Value.Equals("a", StringComparison.OrdinalIgnoreCase) ||
             prev.Groups[1].Value.Equals("an", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    // "forty dollars" -> "$40", "forty dollars and fifty cents" -> "$40.50".
    // The currency word (and cents tail) is absorbed into the replacement.
    static string? TryFormatCurrency(string text, int runIndex, int runLength, string formatted, ref int consumed)
    {
        var after = text.Substring(runIndex + runLength);
        var next = NextWord.Match(after);
        if (!next.Success || !CurrencySymbols.TryGetValue(next.Groups[1].Value, out var symbol))
            return null;

        consumed = runLength + next.Groups[1].Index + next.Groups[1].Length;

        // "forty dollars and fifty cents" -> "$40.50"
        var value = formatted;
        var cents = CentsTail.Match(text.Substring(runIndex + consumed));
        if (cents.Success)
        {
            var centsWords = Regex.Split(cents.Groups[1].Value, @"[\s\-]+");
            var centsValue = ParseIntegerWords(centsWords);
            if (centsValue != null && centsValue <= 99)
            {
                value += "." + centsValue.Value.ToString("D2", CultureInfo.InvariantCulture);
                consumed += cents.Length;
            }
        }

        return symbol + value;
    }

    static string? FormatNumber(string[] words)
    {
        var pointIndex = Array.FindIndex(words, w => w.Equals("point", StringComparison.OrdinalIgnoreCase));
        var intWords = pointIndex >= 0 ? words[..pointIndex] : words;

        var intPart = ParseIntegerWords(intWords);
        if (intPart == null)
            return null;

        var formatted = intPart.Value.ToString("#,##0", CultureInfo.InvariantCulture);
        if (pointIndex < 0)
            return formatted;

        var fraction = new StringBuilder();
        for (var i = pointIndex + 1; i < words.Length; i++)
        {
            if (!DecimalDigits.TryGetValue(words[i], out var digit))
                return null;
            fraction.Append(digit);
        }
        if (fraction.Length == 0)
            return null;

        return formatted + "." + fraction;
    }

    enum State { Start, Ones, Tens, Hundred, Scale, And }

    // Standard spoken-number grammar: ones/tens accumulate, "hundred" scales
    // the current chunk, "thousand"+ closes a chunk into the total. Rejects
    // ambiguous sequences ("one two", "five thirty") and a trailing "and" so
    // those runs stay words.
    static long? ParseIntegerWords(string[] words)
    {
        if (words.Length == 0)
            return null;

        long total = 0, current = 0;
        var state = State.Start;

        foreach (var word in words)
        {
            if (Ones.TryGetValue(word, out var one))
            {
                if (state == State.Ones)
                    return null;
                current += one;
                state = State.Ones;
            }
            else if (Tens.TryGetValue(word, out var ten))
            {
                if (state == State.Ones || state == State.Tens)
                    return null;
                current += ten;
                state = State.Tens;
            }
            else if (word.Equals("hundred", StringComparison.OrdinalIgnoreCase))
            {
                if (state == State.And)
                    return null;
                current = Math.Max(current, 1) * 100;
                state = State.Hundred;
            }
            else if (Scales.TryGetValue(word, out var scale) && scale > 100)
            {
                if (state == State.And)
                    return null;
                total += Math.Max(current, 1) * scale;
                current = 0;
                state = State.Scale;
            }
            else if (IsAnd(word))
            {
                if (state != State.Hundred && state != State.Scale)
                    return null;
                state = State.And;
            }
            else
            {
                return null;
            }
        }

        return state == State.And ? null : total + current;
    }

    static Regex BuildNumberRunRegex()
    {
        // Longest alternatives first so "eighty" wins over "eight".
        var sorted = (string[])NumberWords.Clone();
        Array.Sort(sorted, (a, b) => b.Length.CompareTo(a.Length));
        var word = "(?:" + string.Join("|", sorted) + ")";
        return new Regex(
            $@"\b{word}(?:[\s\-]+{word})*\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    static Regex BuildCentsTailRegex()
    {
        var small = new List<string>(Ones.Keys);
        small.AddRange(Tens.Keys);
        small.Sort((a, b) => b.Length.CompareTo(a.Length));
        var word = "(?:" + string.Join("|", small) + ")";
        return new Regex(
            $@"^\s+and\s+({word}(?:[\s\-]+{word})*)\s+cents\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
