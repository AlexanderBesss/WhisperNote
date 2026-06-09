using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WhisperNote.Services;

static class TranscriptionParser
{
    public static string? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            Logger.Error("Empty response from server");
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
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

            text = CleanAsrOutput(text);

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

    static string? CleanAsrOutput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var start = text.IndexOf("<asr_text>", StringComparison.Ordinal);
        if (start >= 0)
            text = text.Substring(start + "<asr_text>".Length);
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
