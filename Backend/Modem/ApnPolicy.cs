using System.Text;

namespace EasyFM350.Wpf.Backend.Modem;

internal static class ApnPolicy
{
    private const int MaxApnBytes = 100;

    public static string NormalizeForConfiguration(string? value)
    {
        return AtInput.Normalize(value ?? string.Empty);
    }

    public static bool IsValidForConfiguration(string value)
    {
        if (value == null || !AtInput.IsSafeValue(value)) return false;
        if (value.Length == 0) return true;
        if (Encoding.ASCII.GetByteCount(value) > MaxApnBytes) return false;

        var labels = value.Split('.');
        if (labels.Length == 0) return false;
        foreach (var label in labels)
        {
            if (label.Length is < 1 or > 63) return false;
            if (!IsAlphaNumeric(label[0]) || !IsAlphaNumeric(label[^1])) return false;
            for (var index = 1; index < label.Length - 1; index++)
            {
                var character = label[index];
                if (!IsAlphaNumeric(character) && character != '-') return false;
            }
        }

        return true;
    }

    private static bool IsAlphaNumeric(char character)
    {
        return character is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9';
    }
}