namespace UserManagementAPI.Middleware;

public static class LogSanitizer
{
    private const int MaxLength = 256;

    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var span = value.Length > MaxLength ? value[..MaxLength] : value;
        return string.Create(span.Length, span, (chars, src) =>
        {
            for (var i = 0; i < chars.Length; i++)
                chars[i] = IsUnsafe(src[i]) ? '_' : src[i];
        });
    }

    // Control characters plus Unicode line/paragraph separators and format characters
    // (bidi overrides, zero-width), which can split or visually spoof a log line.
    private static bool IsUnsafe(char c) =>
        char.IsControl(c) || char.GetUnicodeCategory(c) is
            System.Globalization.UnicodeCategory.LineSeparator
            or System.Globalization.UnicodeCategory.ParagraphSeparator
            or System.Globalization.UnicodeCategory.Format;
}
