namespace EasyFM350.Wpf.Backend.Modem;

internal static class AtInput
{
    public static string Normalize(string value, bool trim = true)
    {
        if (value == null) return string.Empty;
        return trim ? value.Trim() : value;
    }

    public static bool IsSafeValue(string value)
    {
        if (value == null) return false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character >= 0x7F || character < 0x20 || character == '"' || character == ';') return false;
        }

        return true;
    }

    public static string Sanitize(string value)
    {
        if (value == null) return string.Empty;
        var chars = value.ToCharArray();
        var length = 0;
        foreach (var character in chars)
            if (character >= 0x20 && character != 0x7F)
                chars[length++] = character;
        return length == chars.Length ? value : new string(chars, 0, length);
    }
}