namespace Pulse.Core.Ledger;

/// <summary>
/// A small RFC 4180 reader for the CSV exports some agents still write; port of
/// upstream CapturedCSV. The legacy Cursor cache is the reason this exists: its
/// rows embed model names and money, a field can be wrapped in double quotes,
/// and a quoted value can hold a comma — so splitting on `,` puts the columns in
/// the wrong places and reads a model name as a token count. This walks the text
/// once with a quote state instead.
///
/// It is deliberately a RECORD READER, not a schema: it returns rows of fields
/// and nothing else. Interpreting a header, coercing a number or deciding what a
/// blank means belongs to the caller, so a malformed row can be skipped without
/// this having guessed.
///
/// Handles: a field wrapped in `"…"` with `""` as one literal quote; a comma
/// inside a quoted field; \r\n, \n and a lone \r all ending a record; a final
/// record with no trailing newline; a leading UTF-8 BOM stripped rather than
/// read as part of the first header name. Newlines INSIDE a quoted field are
/// kept, which is what RFC 4180 asks for. Nothing is repaired: an unterminated
/// quote is simply the last field to the end of the file.
/// </summary>
public static class CapturedCsv
{
    public static IReadOnlyList<IReadOnlyList<string>> Rows(string text)
    {
        var rows = new List<IReadOnlyList<string>>();
        var record = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;

        void EndField()
        {
            record.Add(field.ToString());
            field.Clear();
        }

        void EndRecord()
        {
            EndField();
            rows.Add(record.ToArray());
            record.Clear();
        }

        // A leading UTF-8 byte-order mark, stripped rather than read as part of
        // the first header name.
        if (text.StartsWith('\uFEFF')) text = text[1..];

        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            var next = index + 1;
            if (inQuotes)
            {
                if (character == '"')
                {
                    // A doubled quote is one literal quote; a lone one closes.
                    if (next < text.Length && text[next] == '"')
                    {
                        field.Append('"');
                        index = next + 1;
                    }
                    else
                    {
                        inQuotes = false;
                        index = next;
                    }
                }
                else
                {
                    field.Append(character);
                    index = next;
                }
                continue;
            }

            switch (character)
            {
                case '"':
                    inQuotes = true;
                    index = next;
                    break;
                case ',':
                    EndField();
                    index = next;
                    break;
                case '\r':
                    index = next < text.Length && text[next] == '\n' ? next + 1 : next;
                    EndRecord();
                    break;
                case '\n':
                    index = next;
                    EndRecord();
                    break;
                default:
                    field.Append(character);
                    index = next;
                    break;
            }
        }

        // A record is only pending if something was written for it: a file that
        // ends on a newline must not grow a phantom empty record.
        if (field.Length > 0 || record.Count > 0) EndRecord();
        return rows;
    }
}
