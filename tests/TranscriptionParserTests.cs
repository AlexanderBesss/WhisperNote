using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

public class TranscriptionParserTests
{
    [Fact]
    public void DedicatedAsrWithoutAsrTextSectionIsTreatedAsEmpty()
    {
        // Qwen3-ASR emits "language X" when it fails to transcribe instead of
        // producing an <asr_text> section; that must not be pasted as text.
        Assert.Null(TranscriptionParser.Parse("""{"type":"transcript.text.done","text":"language None"}""", dedicatedAsr: true));
        Assert.Null(TranscriptionParser.Parse("""{"text":"language Chinese"}""", dedicatedAsr: true));
    }

    [Fact]
    public void DedicatedAsrStripsLanguagePrefixAndAsrTextTags()
    {
        var text = TranscriptionParser.Parse(
            """{"type":"transcript.text.done","text":"language English<asr_text>Hello, how are you?</asr_text>"}""",
            dedicatedAsr: true);

        Assert.Equal("Hello, how are you?", text);
    }

    [Fact]
    public void DedicatedAsrNormalizesNumberWordsToDigits()
    {
        // Qwen3-ASR transcribes numbers verbatim as words; the parser converts
        // them because the model cannot follow the transcription prompt.
        var text = TranscriptionParser.Parse(
            """{"text":"language English<asr_text>Call me at twenty five.</asr_text>"}""",
            dedicatedAsr: true);

        Assert.Equal("Call me at 25.", text);
    }

    [Fact]
    public void LlmModelOutputKeepsNumberWordsUnnormalized()
    {
        // LLM models follow the prompt's digit rules themselves; their output
        // must not be rewritten by the normalizer.
        var text = TranscriptionParser.Parse("""{"text":"I have twenty five dollars."}""", dedicatedAsr: false);

        Assert.Equal("I have twenty five dollars.", text);
    }

    [Fact]
    public void PlainTextIsLeftUntouchedForLlmModels()
    {
        // LLM-based models (non-dedicated-ASR) answer the custom prompt with plain
        // text and may legitimately start with the word "Language".
        var text = TranscriptionParser.Parse("""{"text":"Language models are great."}""", dedicatedAsr: false);

        Assert.Equal("Language models are great.", text);
    }

    [Fact]
    public void EmptyTextIsStillTreatedAsEmpty()
    {
        Assert.Null(TranscriptionParser.Parse("""{"text":""}"""));
        Assert.Null(TranscriptionParser.Parse("""{"text":"   "}""", dedicatedAsr: true));
        Assert.Null(TranscriptionParser.Parse(""));
    }
}
