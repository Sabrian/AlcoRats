using System;
using System.Globalization;

namespace AlcoRats.Services.Speech;

/// <summary>
/// Truncates display names for TTS by syllable count (RU + EN heuristics).
/// </summary>
public static class NameSyllableHelper
{
    private const string RussianVowels = "аеёиоуыэюяАЕЁИОУЫЭЮЯ";
    private const string EnglishVowels = "aeiouyAEIOUY";

    public static string TruncateToSyllables(string? name, int maxSyllables = 10)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "участник";

        var trimmed = name.Trim();
        if (maxSyllables <= 0)
            return trimmed;

        var syllables = 0;
        var cut = trimmed.Length;
        var prevVowel = false;

        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            var isVowel = IsVowel(ch);
            if (isVowel && !prevVowel)
            {
                syllables++;
                if (syllables > maxSyllables)
                {
                    cut = i;
                    break;
                }
            }

            prevVowel = isVowel;
        }

        if (cut >= trimmed.Length)
            return trimmed;

        while (cut > 0 && !IsLetterOrDigit(trimmed[cut - 1]))
            cut--;

        var result = trimmed[..cut].TrimEnd();
        return string.IsNullOrWhiteSpace(result) ? trimmed[..Math.Min(trimmed.Length, 10)] : result;
    }

    private static bool IsVowel(char ch)
        => RussianVowels.Contains(ch) || EnglishVowels.Contains(ch);

    private static bool IsLetterOrDigit(char ch)
        => char.GetUnicodeCategory(ch) is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.DecimalDigitNumber;
}
