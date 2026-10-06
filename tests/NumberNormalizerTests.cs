using WhisperNote.Services;
using Xunit;

namespace WhisperNote.Tests;

public class NumberNormalizerTests
{
    [Theory]
    // Plain quantities, including hyphenated and capitalized forms.
    [InlineData("I said twenty five", "I said 25")]
    [InlineData("twenty-one players", "21 players")]
    [InlineData("Twenty five minutes", "25 minutes")]
    [InlineData("He scored eighty points", "He scored 80 points")]
    [InlineData("there are zero left", "there are 0 left")]
    // Compound numbers with and without "and".
    [InlineData("one hundred and five", "105")]
    [InlineData("one hundred five", "105")]
    [InlineData("one thousand two hundred thirty four", "1,234")]
    [InlineData("two million five hundred thousand", "2,500,000")]
    [InlineData("three billion", "3,000,000,000")]
    // Decimals, including "oh" as zero.
    [InlineData("three point five", "3.5")]
    [InlineData("two point three five", "2.35")]
    [InlineData("three point oh two", "3.02")]
    // Currency, including cents.
    [InlineData("it cost forty dollars", "it cost $40")]
    [InlineData("one thousand dollars", "$1,000")]
    [InlineData("forty dollars and fifty cents", "$40.50")]
    [InlineData("five euros", "€5")]
    [InlineData("ten pounds", "£10")]
    public void ConvertsNumberWordsToDigits(string input, string expected)
    {
        Assert.Equal(expected, NumberNormalizer.Normalize(input));
    }

    [Theory]
    // Idioms and pronouns must stay words.
    [InlineData("one of them")]
    [InlineData("no one knows")]
    [InlineData("one by one")]
    [InlineData("see you one day")]
    [InlineData("thanks a million")]
    // Ambiguous or malformed number runs must stay words.
    [InlineData("count one two three")]
    [InlineData("five thirty")]
    [InlineData("and then we left")]
    [InlineData("point of sale")]
    [InlineData("oh really")]
    [InlineData("the point is clear")]
    // Ordinary text without number words.
    [InlineData("Hello, how are you?")]
    [InlineData("None")]
    public void LeavesWordsUntouched(string input)
    {
        Assert.Equal(input, NumberNormalizer.Normalize(input));
    }

    [Fact]
    public void NormalizesInsideFullSentences()
    {
        var input = "Call me at twenty five past six and bring one hundred dollars. One of them costs fifty dollars and ten cents.";
        var expected = "Call me at 25 past 6 and bring $100. One of them costs $50.10.";

        Assert.Equal(expected, NumberNormalizer.Normalize(input));
    }
}
