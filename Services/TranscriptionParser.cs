using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhisperNote.Services;

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
    public static string? Parse(string? raw, bool dedicatedAsr = false)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            Logger.Error("Empty response from server");
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var text = ExtractText(doc);

            if (dedicatedAsr && (text == null || !text.Contains(AsrTextTag, StringComparison.Ordinal)))
            {
                Logger.Warn($"ASR model returned no <asr_text> section: {text}");
                return null;
            }

            text = CleanAsrOutput(text);

            // Dedicated ASR models transcribe numbers as words and cannot be
            // steered by the transcription prompt, so normalize them here.
            if (dedicatedAsr && text != null)
                text = NumberNormalizer.Normalize(text);

            if (string.IsNullOrWhiteSpace(text))
            {
                Logger.Info("Server returned empty text");
                return null;
            }
            return RemoveRepetitions(text);
        }
        catch (Exception ex)
        {
            Logger.Error($"JSON parse error: {ex.Message}");
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

            Logger.Warn($"Recovering an unformatted ASR answer: {text}");
            return RemoveRepetitions(text);
        }
        catch (Exception ex)
        {
            Logger.Error($"JSON parse error while salvaging: {ex.Message}");
            return null;
        }
    }

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
