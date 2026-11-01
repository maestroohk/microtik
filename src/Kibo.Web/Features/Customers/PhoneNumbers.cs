namespace Kibo.Web.Features.Customers;

public static class PhoneNumbers
{
    public static string? NormalizeUganda(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var digits = new string(input.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("256") && digits.Length == 12)
        {
            return digits;
        }

        if (digits.StartsWith('0') && digits.Length == 10)
        {
            return "256" + digits[1..];
        }

        if (digits.Length == 9 && digits[0] is '7' or '3' or '2')
        {
            return "256" + digits;
        }

        return null;
    }

    public static string Network(string normalized)
    {
        if (normalized.Length != 12)
        {
            return "Unknown";
        }

        var prefix = normalized.Substring(3, 2);
        return prefix switch
        {
            "77" or "78" or "76" or "39" => "MTN",
            "70" or "75" or "74" or "20" => "Airtel",
            _ => "Other"
        };
    }

    public static string Display(string normalized) =>
        normalized.Length == 12 ? $"+{normalized[..3]} {normalized.Substring(3, 3)} {normalized.Substring(6, 3)} {normalized[9..]}" : normalized;
}
