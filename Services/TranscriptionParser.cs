using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhisperNote.Services;

public enum AsrFailure
{
    None,
    NoSpeech,
    ForeignLanguage,
    Malformed
}

static class TranscriptionParser
{
    const string AsrTextTag = "<asr_text>";

    static readonly Regex LanguagePrefix =
        new(@"^\s*language\s+[A-Za-z][A-Za-z'-]*\s*[.,:!]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? Parse(string? raw, bool dedicatedAsr = false) =>
        Parse(raw, dedicatedAsr, out _);

    public static string? Parse(string? raw, bool dedicatedAsr, out AsrFailure failure)
    {
        failure = AsrFailure.None;

        if (string.IsNullOrWhiteSpace(raw))
        {
            Logger.Error("Empty response from server");
            failure = AsrFailure.Malformed;
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var text = ExtractText(doc);

            var declaresNoSpeech = DeclaresNoSpeech(text);

            if (dedicatedAsr && (text == null || !text.Contains(AsrTextTag, StringComparison.Ordinal)))
            {
                failure = string.IsNullOrWhiteSpace(text) || declaresNoSpeech
                    ? AsrFailure.NoSpeech
                    : AsrFailure.Malformed;
                Logger.Warn(failure == AsrFailure.NoSpeech
                    ? $"ASR model heard no speech in the recording: {Truncate(text)}"
                    : $"ASR model returned no <asr_text> section: {Truncate(text)}");
                return null;
            }

            text = CleanAsrOutput(text);

            if (dedicatedAsr && declaresNoSpeech)
            {
                failure = AsrFailure.NoSpeech;
                Logger.Warn($"ASR model declared no speech and then answered: {Truncate(text)}");
                return null;
            }

            // Decoding is temperature 0: the same audio answers identically, so re-sending cannot help.
            if (string.IsNullOrWhiteSpace(text))
            {
                Logger.Info("Server returned empty text");
                failure = dedicatedAsr ? AsrFailure.NoSpeech : AsrFailure.Malformed;
                return null;
            }

            if (dedicatedAsr && IsForeignScript(text))
            {
                failure = AsrFailure.ForeignLanguage;
                Logger.Warn($"Discarding an answer in a non-Latin script: {Truncate(text)}");
                return null;
            }

            if (dedicatedAsr)
                text = NumberNormalizer.Normalize(text);

            return RemoveRepetitions(text);
        }
        catch (Exception ex)
        {
            Logger.Error($"JSON parse error: {ex.Message}");
            failure = AsrFailure.Malformed;
            return null;
        }
    }

    static string? ExtractText(JsonDocument doc)
    {
        string? text = null;

        if (doc.RootElement.TryGetProperty("text", out var textProp))
            text = textProp.GetString()?.Trim();

        if (string.IsNullOrWhiteSpace(text) &&
            doc.RootElement.TryGetProperty("choices", out var choices) &&
            choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("text", out var choiceText))
                text = choiceText.GetString()?.Trim();
            else if (firstChoice.TryGetProperty("message", out var msg) &&
                     msg.TryGetProperty("content", out var msgContent))
                text = msgContent.GetString()?.Trim();
        }

        return text;
    }

    // Call only after all parse attempts have failed; unformatted answers are the model's own chat drift.
    public static string? SalvageUnformatted(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var text = CleanAsrOutput(ExtractText(doc));
            if (text == null)
                return null;

            text = LanguagePrefix.Replace(text, "", count: 1).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return null;

            if (IsForeignScript(text))
            {
                Logger.Warn($"Discarding an unformatted non-Latin answer: {Truncate(text)}");
                return null;
            }

            Logger.Warn($"Recovering an unformatted ASR answer: {Truncate(text)}");
            return RemoveRepetitions(text);
        }
        catch (Exception ex)
        {
            Logger.Error($"JSON parse error while salvaging: {ex.Message}");
            return null;
        }
    }

    static bool DeclaresNoSpeech(string? text) =>
        text != null && Regex.IsMatch(text, @"^\s*language\s+(none|no speech)\b", RegexOptions.IgnoreCase);

    // Requests pin language=en, so any non-Latin letter in an answer is a hallucination.
    internal static bool IsForeignScript(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (char.IsHighSurrogate(c))
            {
                // Surrogate pairs cover astral CJK; non-letter symbols there pass.
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    if (IsLetterCategory(CharUnicodeInfo.GetUnicodeCategory(char.ConvertToUtf32(c, text[i + 1]))))
                        return true;
                    i++;
                }
                continue;
            }

            if (!char.IsLetter(c))
                continue;

            if (c <= 0x024F || (c >= 0x1E00 && c <= 0x1EFF))
                continue;

            return true;
        }

        return false;
    }

    static bool IsLetterCategory(UnicodeCategory category) =>
        category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter;

    static string Truncate(string? text) =>
        string.IsNullOrEmpty(text) ? "(nothing)" : text.Length <= 200 ? text : text[..200] + "...";

    static string? CleanAsrOutput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var start = text.IndexOf(AsrTextTag, StringComparison.Ordinal);
        if (start >= 0)
            text = text.Substring(start + AsrTextTag.Length);
        text = text.Replace("</asr_text>", "");
        return text.Trim();
    }

    static string? RemoveRepetitions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var delimiters = new[] { '.', '!', '?', '\n' };
        var parts = new List<(string text, char delim)>();
        var remaining = text.AsSpan();

        while (remaining.Length > 0)
        {
            var idx = remaining.IndexOfAny(delimiters);
            if (idx < 0)
            {
                var trimmed = remaining.Trim().ToString();
                if (!string.IsNullOrEmpty(trimmed))
                    parts.Add((trimmed, '.'));
                break;
            }
            var sentence = remaining.Slice(0, idx).Trim().ToString();
            if (!string.IsNullOrEmpty(sentence))
                parts.Add((sentence, remaining[idx]));
            remaining = remaining.Slice(idx + 1);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new StringBuilder();

        foreach (var (s, d) in parts)
        {
            if (!seen.Add(s)) continue;
            if (result.Length > 0) result.Append(' ');
            result.Append(s).Append(d);
        }

        var output = result.ToString();
        var delims = new[] { '.', '!', '?' };
        while (output.Length >= 2 && delims.Contains(output[^1]) && output[^2] == output[^1])
            output = output[..^1];
        return output.TrimEnd(' ');
    }
}
