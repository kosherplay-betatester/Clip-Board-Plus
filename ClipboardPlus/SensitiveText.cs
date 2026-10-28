using System.Text.RegularExpressions;

namespace ClipboardPlus;

// Local, offline detection of content people usually do not want to keep in plain clipboard history.
// Returns a short label ("credit card number", "private key"...) or null. Never sends text anywhere.
internal static class SensitiveText
{
    private const int ScanLimit = 65536;
    private static readonly (string Label, Regex Pattern)[] Patterns =
    [
        ("private key", new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant)),
        ("AWS access key", new(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.CultureInvariant)),
        ("GitHub token", new(@"\b(gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})", RegexOptions.CultureInvariant)),
        ("API key", new(@"\b(sk-[A-Za-z0-9_\-]{20,}|sk_live_[A-Za-z0-9]{16,}|xox[baprs]-[A-Za-z0-9\-]{10,}|AIza[0-9A-Za-z_\-]{35})", RegexOptions.CultureInvariant)),
        ("JWT token", new(@"\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}", RegexOptions.CultureInvariant)),
        ("bearer token", new(@"\bBearer\s+[A-Za-z0-9._~+/=\-]{20,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)),
        ("password", new(@"(?<![\p{L}\p{N}_])(password|passwd|pwd|סיסמה|סיסמא)\s*[:=]\s*\S{4,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)),
    ];
    private static readonly Regex CardCandidate = new(@"(?<!\d)(?:\d[ \-]?){12,18}\d(?!\d)", RegexOptions.CultureInvariant);
    private static readonly Regex IbanCandidate = new(@"\b[A-Z]{2}\d{2}[A-Z0-9]{11,30}\b", RegexOptions.CultureInvariant);

    internal static string? Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Length > ScanLimit) text = text[..ScanLimit];
        foreach (var (label, pattern) in Patterns) if (pattern.IsMatch(text)) return label;
        foreach (Match m in CardCandidate.Matches(text))
        {
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            if (digits.Length is >= 13 and <= 19 && digits.Distinct().Count() > 1 && Luhn(digits)) return "credit card number";
        }
        foreach (Match m in IbanCandidate.Matches(text)) if (IbanValid(m.Value)) return "bank account (IBAN)";
        return LooksLikePassword(text.Trim()) ? "possible password" : null;
    }
    internal static bool Luhn(string digits)
    {
        int sum = 0; bool alternate = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (alternate) { d *= 2; if (d > 9) d -= 9; }
            sum += d; alternate = !alternate;
        }
        return sum % 10 == 0;
    }
    private static bool IbanValid(string iban)
    {
        if (iban.Length is < 15 or > 34) return false;
        var rearranged = iban[4..] + iban[..4]; int remainder = 0;
        foreach (char c in rearranged)
        {
            int value = char.IsDigit(c) ? c - '0' : c - 'A' + 10;
            remainder = value > 9 ? (remainder * 100 + value) % 97 : (remainder * 10 + value) % 97;
        }
        return remainder == 1;
    }
    // A single token that mixes upper, lower, digits and symbols is overwhelmingly a password, not prose or code.
    private static bool LooksLikePassword(string s)
    {
        if (s.Length is < 10 or > 64 || s.Any(char.IsWhiteSpace) || s.Contains("://") || s.Contains('\\') || s.Contains('@') && s.Contains('.')) return false;
        return s.Any(char.IsUpper) && s.Any(char.IsLower) && s.Any(char.IsDigit) && s.Any(c => !char.IsLetterOrDigit(c));
    }
}
