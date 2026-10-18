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

    [Fact]
    public void SalvageKeepsAChatStyleTranscriptThatDroppedTheAsrTextTag()
    {
        // Observed in logs.log: the model transcribed the speech correctly but
        // answered in chat style, so the strict parser threw the words away.
        var text = TranscriptionParser.SalvageUnformatted(
            """{"type":"transcript.text.done","text":"language English. Yeah, apply small fixes, low risk."}""");

        Assert.Equal("Yeah, apply small fixes, low risk.", text);
    }

    [Fact]
    public void SalvageReturnsNothingWhenTheModelReportedNoLanguage()
    {
        Assert.Null(TranscriptionParser.SalvageUnformatted("""{"text":"language None"}"""));
        Assert.Null(TranscriptionParser.SalvageUnformatted("""{"text":""}"""));
        Assert.Null(TranscriptionParser.SalvageUnformatted("not json"));
        Assert.Null(TranscriptionParser.SalvageUnformatted(null));
    }

    [Fact]
    public void SalvageRefusesAChatStyleAnswerInAnotherScript()
    {
        Assert.Null(TranscriptionParser.SalvageUnformatted("""{"text":"language Chinese. 啊哈哈！"}"""));
    }

    [Fact]
    public void FailureNamesWhyTheAnswerWasRejected()
    {
        // "Nothing happened" needs a different message for a recording that
        // holds no speech, an answer in another language, and a model that
        // broke its own output format.
        Assert.Equal(AsrFailure.NoSpeech, Classify("""{"text":"language None<asr_text>"}"""));
        Assert.Equal(AsrFailure.NoSpeech, Classify("""{"text":"language English<asr_text>"}"""));
        Assert.Equal(AsrFailure.ForeignLanguage, Classify("""{"text":"language Chinese<asr_text>啊哈哈！</asr_text>"}"""));
        Assert.Equal(AsrFailure.Malformed, Classify("""{"text":"language English. Sure, here you go."}"""));
        Assert.Equal(AsrFailure.None, Classify("""{"text":"language English<asr_text>Run the scan.</asr_text>"}"""));
    }

    static AsrFailure Classify(string raw)
    {
        TranscriptionParser.Parse(raw, dedicatedAsr: true, out var failure);
        return failure;
    }

    [Fact]
    public void LatinTranscriptsSurviveTheScriptGuard()
    {
        // Accents and symbols are not another language; only an answer built of
        // non-Latin letters is.
        var text = TranscriptionParser.Parse(
            """{"text":"language English<asr_text>Café résumé → done.</asr_text>"}""",
            dedicatedAsr: true);

        Assert.Equal("Café résumé → done.", text);
    }

    [Fact]
    public void OneStrayForeignCharacterDistrustsTheWholeAnswer()
    {
        // With the ASR language pinned to English, any non-Latin letter is the
        // model guessing; the old majority vote let both of these through.
        Assert.Equal(AsrFailure.ForeignLanguage,
            Classify("""{"text":"language English<asr_text>Run the scan мир now.</asr_text>"}"""));
        Assert.Equal(AsrFailure.ForeignLanguage,
            Classify("""{"text":"language English<asr_text>𠀀 done</asr_text>"}"""));
    }

    [Fact]
    public void VietnameseLatinAndEmojiPassTheScriptGuard()
    {
        // Latin Extended Additional carries Vietnamese tone marks, which the old
        // range check counted as another language. Emoji are symbols, not letters.
        Assert.Equal("Đi Hà Nội rồi.", TranscriptionParser.Parse(
            """{"text":"language English<asr_text>Đi Hà Nội rồi.</asr_text>"}""", dedicatedAsr: true));
        Assert.Equal("Ok 👍.", TranscriptionParser.Parse(
            """{"text":"language English<asr_text>Ok 👍</asr_text>"}""", dedicatedAsr: true));
    }

    [Fact]
    public void DeclaredNoSpeechIsRefusedEvenWhenTheModelAnswersAfterIt()
    {
        // "language None" is the model's own verdict on the recording, so text
        // after it is invented; the tag being present used to make it pasteable.
        Assert.Equal(AsrFailure.NoSpeech,
            Classify("""{"text":"language None<asr_text>Thank you.</asr_text>"}"""));
    }
}
