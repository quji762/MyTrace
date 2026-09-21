using Pulse.Core.Ledger;
using Xunit;

namespace Pulse.Core.Tests;

/// <summary>
/// CapturedCSV tests: the RFC 4180 contract the legacy Cursor cache needs —
/// quoted fields with embedded commas and doubled quotes, all three newline
/// forms, the BOM strip, and no phantom trailing record.
/// </summary>
public class CapturedCsvTests
{
    [Fact]
    public void Quoted_Field_Keeps_Comma_And_Doubled_Quote()
    {
        // The legacy Cursor cache embeds model names and money; a comma inside
        // a quoted field must not split the columns.
        var rows = CapturedCsv.Rows(
            "session,\"model, with \"\"quotes\"\"\",tokensIn\r\n" +
            "s1,\"GPT-5, cached\",100");

        Assert.Equal(2, rows.Count);
        Assert.Equal(["session", "model, with \"quotes\"", "tokensIn"], rows[0]);
        Assert.Equal(["s1", "GPT-5, cached", "100"], rows[1]);
    }

    [Fact]
    public void All_Three_Newline_Forms_End_A_Record()
    {
        var rows = CapturedCsv.Rows("a,b\r\nc,d\ne,f\rg,h");
        Assert.Equal(4, rows.Count);
        Assert.Equal(["a", "b"], rows[0]);
        Assert.Equal(["c", "d"], rows[1]);
        Assert.Equal(["e", "f"], rows[2]);
        Assert.Equal(["g", "h"], rows[3]);
    }

    [Fact]
    public void Newline_Inside_Quotes_Is_Kept_In_The_Field()
    {
        var rows = CapturedCsv.Rows("a,\"line1\nline2\",c");
        Assert.Equal(["a", "line1\nline2", "c"], rows[0]);
    }

    [Fact]
    public void Bom_Is_Stripped_Not_Read_As_A_Header_Name()
    {
        var rows = CapturedCsv.Rows("\uFEFFsession,tokens\r\ns1,5");
        Assert.Equal(["session", "tokens"], rows[0]); // no BOM prefix
        Assert.Equal(["s1", "5"], rows[1]);
    }

    [Fact]
    public void Trailing_Newline_Grows_No_Phantom_Record()
    {
        var rows = CapturedCsv.Rows("a,b\n");
        Assert.Single(rows);
    }

    [Fact]
    public void Unterminated_Quote_Runs_To_End_Of_File()
    {
        // Nothing is repaired: the last field simply runs to the end.
        var rows = CapturedCsv.Rows("a,\"unterminated,b");
        Assert.Equal(["a", "unterminated,b"], rows[0]);
    }

    [Fact]
    public void Final_Record_Without_Newline_Is_Kept()
    {
        var rows = CapturedCsv.Rows("a,b\nc,d");
        Assert.Equal(2, rows.Count);
        Assert.Equal(["c", "d"], rows[1]);
    }
}
