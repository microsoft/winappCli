using Xunit;

namespace ContosoDesk.Tests;

public class CsvReaderTests
{
    [Fact]
    public void SplitsPlainFields() => Assert.Equal(new[] { "a", "b" }, CsvReader.ParseLine("a,b"));

    [Fact]
    public void KeepsQuotedCommaInOneField() => Assert.Equal(new[] { "a,b", "c" }, CsvReader.ParseLine("\"a,b\",c"));

    [Fact]
    public void UnescapesDoubledQuotes() => Assert.Equal(new[] { "say \"hi\"", "x" }, CsvReader.ParseLine("\"say \"\"hi\"\"\",x"));
}
