using Xunit;

namespace WhisperNote.Tests;

public class StartupRegistryTests
{
    [Theory]
    [InlineData("\"F:\\Projects\\WhisperNote\\publish\\WhisperNote.exe\" --startup", @"F:\Projects\WhisperNote\publish\WhisperNote.exe")]
    [InlineData("\"F:\\Projects\\WhisperNote\\publish\\WhisperNote.exe\"", @"F:\Projects\WhisperNote\publish\WhisperNote.exe")]
    [InlineData("F:\\Tools\\WhisperNote.exe", @"F:\Tools\WhisperNote.exe")]
    public void ExtractExePathHandlesQuotedArgsAndLegacyValues(string value, string expected)
    {
        Assert.Equal(expected, StartupRegistry.ExtractExePath(value));
    }

    [Fact]
    public void PathsMatchIgnoresCaseAndSeparators()
    {
        Assert.True(StartupRegistry.PathsMatch(
            @"f:\projects\whispernote\publish\WhisperNote.exe",
            @"F:\Projects\WhisperNote\publish\WhisperNote.exe"));
        Assert.False(StartupRegistry.PathsMatch(
            @"F:\Projects\WhisperNote\bin\Debug\WhisperNote.exe",
            @"F:\Projects\WhisperNote\publish\WhisperNote.exe"));
    }
}
