using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhisperNote.Services;

// Why an ASR answer was thrown away. The model can hear the recording and
// still refuse to transcribe it ("language None"), answer in a script the user
// is not dictating in, or drift out of its output format; these need different
// words in the UI and different retry decisions.
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

    // Qwen3-ASR opens every answer with "language <Name>"; when it answers in
    // chat style the language name is followed by the transcript itself.
    static readonly Regex LanguagePrefix =
        new(@"^\s*language\s+[A-Za-z][A-Za-z'-]*\s*[.,:!]?\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // dedicatedAsr: the model is a purpose-built ASR model (Qwen3-ASR) whose
    // output format is "language X<asr_text>...</asr_text>". A response without
    // the <asr_text> section (e.g. just "language None") means the model failed
    // to transcribe, so it must not be pasted as if it were text.
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

            // "language None" is the model's own verdict that the recording held
            // no speech. It is read before CleanAsrOutput cuts the tag, so an
            // answer that declares no speech and then answers anyway is refused
            // instead of pasting whatever the model invented after the words.
            var declaresNoSpeech = DeclaresNoSpeech(text);

            if (dedicatedAsr && (text == null || !text.Contains(AsrTextTag, StringComparison.Ordinal)))
            {
                // Either the model explicitly reported "language None", or it
                // answered with nothing at all: both say the recording holds no
                // speech, and greedy decoding repeats that on every retry.
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

            // A bare "language English<asr_text>" is the model saying it heard
            // nothing; the answer is deterministic at temperature 0, so
            // re-sending the same audio cannot help.
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

            // Dedicated ASR models transcribe numbers as words and cannot be
            // steered by the transcription prompt, so normalize them here.
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

    // llama.cpp answers the audio endpoint with {"text": ...}; the OpenAI-style
    // {"choices": [...]} shape is kept for remote providers.
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

    // Last-resort recovery for a dedicated ASR model that answered in chat
    // style ("language English. Yeah, apply small fixes.") instead of its
    // trained "language X<asr_text>..." format. The words are a correct
    // transcript; discarding them makes a good recording look like a lost
    // request, so it is used once the retries have already failed.
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

    // "language None" is Qwen3-ASR's own way of reporting that the recording
    // holds no speech: hums, clicks and room tone all answer with it (measured
    // against the running server), so the retries that used to follow were
    // three identical requests buying nothing.
    static bool DeclaresNoSpeech(string? text) =>
        text != null && Regex.IsMatch(text, @"^\s*language\s+(none|no speech)\b", RegexOptions.IgnoreCase);

    // A dedicated ASR model answers in whatever language it guesses, and it
    // guesses wrong on audio that is not clean speech: a 1.4 s low-level
    // vocalisation came back as "language Chinese<asr_text>啊哈哈！" and was
    // pasted into the user's document. Requests now pin language=en, so any
    // letter outside Latin is a hallucination rather than a transcription, and
    // a majority vote let a single stray CJK character inside an English answer
    // through.
    internal static bool IsForeignScript(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (char.IsHighSurrogate(c))
            {
                // Astral planes are where the rest of CJK lives. Emoji and other
                // symbols an answer might carry are not letters, so they pass.
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

            // Latin: the Latin-1 accents, Extended A/B (č, ł) and Extended
            // Additional, which carries Vietnamese tone marks.
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

    // A discarded answer is dictation and therefore private: log only as much of
    // it as explains the failure.
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
