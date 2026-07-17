namespace Nomeroff.Shared;

/// <summary>
/// Nomeroff RU OCR возвращает латиницу (A/B/.../Y). Для БД и UI — кириллица российского алфавита номеров.
/// </summary>
public static class PlateAlphabet
{
    private static readonly Dictionary<char, char> LatinToCyrillicMap = new()
    {
        ['A'] = 'А',
        ['B'] = 'В',
        ['C'] = 'С',
        ['E'] = 'Е',
        ['H'] = 'Н',
        ['K'] = 'К',
        ['M'] = 'М',
        ['O'] = 'О',
        ['P'] = 'Р',
        ['T'] = 'Т',
        ['X'] = 'Х',
        ['Y'] = 'У',
    };

    public static string LatinToCyrillic(string? plate)
    {
        if (string.IsNullOrWhiteSpace(plate))
            return "";

        var chars = plate.Trim().ToUpperInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (LatinToCyrillicMap.TryGetValue(chars[i], out var cyr))
                chars[i] = cyr;
        }
        return new string(chars);
    }

    /// <summary>Буквы/цифры + ToUpper + латиница→кириллица (для сравнения и watchlist).</summary>
    public static string Normalize(string? plate)
    {
        if (string.IsNullOrWhiteSpace(plate))
            return "";
        var filtered = new string(plate.Where(char.IsLetterOrDigit).ToArray());
        return LatinToCyrillic(filtered);
    }

    /// <summary>Грубая проверка формата РФ: А123ВС45 / А123ВС125.</summary>
    public static bool LooksLikeRuPlate(string? plate)
    {
        var n = Normalize(plate);
        if (n.Length is < 8 or > 9)
            return false;
        const string letters = "АВЕКМНОРСТУХ";
        if (!letters.Contains(n[0])) return false;
        if (!char.IsDigit(n[1]) || !char.IsDigit(n[2]) || !char.IsDigit(n[3])) return false;
        if (!letters.Contains(n[4]) || !letters.Contains(n[5])) return false;
        for (var i = 6; i < n.Length; i++)
            if (!char.IsDigit(n[i])) return false;
        return true;
    }
}
