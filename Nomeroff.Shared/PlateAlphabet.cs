namespace Nomeroff.Shared;

/// <summary>
/// Nomeroff RU OCR возвращает латиницу (A/B/.../Y). Для БД и UI — кириллица российского алфавита номеров.
/// </summary>
public static class PlateAlphabet
{
    /// <summary>Буквы, допустимые на российских номерах (гражданских и военных).</summary>
    public const string RuPlateLetters = "АВЕКМНОРСТУХ";

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

    private static bool IsRuLetter(char c) => RuPlateLetters.Contains(c);

    /// <summary>Гражданский: А123ВС45 / А123ВС125.</summary>
    public static bool LooksLikeCivilianRuPlate(string? plate)
    {
        var n = Normalize(plate);
        if (n.Length is < 8 or > 9)
            return false;
        if (!IsRuLetter(n[0])) return false;
        if (!char.IsDigit(n[1]) || !char.IsDigit(n[2]) || !char.IsDigit(n[3])) return false;
        if (!IsRuLetter(n[4]) || !IsRuLetter(n[5])) return false;
        for (var i = 6; i < n.Length; i++)
            if (!char.IsDigit(n[i])) return false;
        return true;
    }

    /// <summary>
    /// Военный / спец: 9036СС45 — 4 цифры + 2 буквы + регион 2–3 цифры.
    /// </summary>
    public static bool LooksLikeMilitaryRuPlate(string? plate)
    {
        var n = Normalize(plate);
        if (n.Length is < 8 or > 9)
            return false;
        for (var i = 0; i < 4; i++)
            if (!char.IsDigit(n[i])) return false;
        if (!IsRuLetter(n[4]) || !IsRuLetter(n[5])) return false;
        for (var i = 6; i < n.Length; i++)
            if (!char.IsDigit(n[i])) return false;
        return true;
    }

    /// <summary>Гражданский или военный формат РФ.</summary>
    public static bool LooksLikeRuPlate(string? plate) =>
        LooksLikeCivilianRuPlate(plate) || LooksLikeMilitaryRuPlate(plate);

    /// <summary>
    /// OCR иногда клеит лишнюю букву перед военным номером: Е9036СС45 → 9036СС45.
    /// </summary>
    public static string? TryFixMilitaryWithLeadingLetter(string? plate)
    {
        var n = Normalize(plate);
        if (n.Length is < 9 or > 10) return null;
        if (!IsRuLetter(n[0])) return null;
        var rest = n[1..];
        return LooksLikeMilitaryRuPlate(rest) ? rest : null;
    }

    /// <summary>
    /// На инвертированном кадре военный 9036СС45 часто читается как гражданский У036СС45
    /// (потеряна первая цифра, вместо неё буква). Пробуем подставить цифру 0–9.
    /// Вызывать только для invert-прохода — иначе ломает обычные А123ВС45.
    /// </summary>
    public static string? TryFixMilitaryFromCivilianLookalike(string? plate)
    {
        var n = Normalize(plate);
        if (!LooksLikeCivilianRuPlate(n) || n.Length < 8)
            return null;
        // Типичные серии военных: СС, ВВ, КК, ММ, ТТ…
        var pair = n[4].ToString() + n[5];
        if (pair is not ("СС" or "ВВ" or "КК" or "ММ" or "ТТ" or "НН" or "ЕЕ" or "АА"))
            return null;

        var rest = n[1..]; // 036СС45
        string? best = null;
        foreach (var d in "0123456789")
        {
            var cand = d + rest;
            if (LooksLikeMilitaryRuPlate(cand))
                best = cand; // последняя подходящая; ниже предпочтём '9' если есть
        }
        // часто первая цифра военных — 0–9; если есть вариант с 9 (как 9036…) — он приоритетнее
        var with9 = '9' + rest;
        if (LooksLikeMilitaryRuPlate(with9))
            return with9;
        return best;
    }

    /// <summary>Ключ дедупа: гражданский — первые 6 символов (А123ВС), военный — 4 цифры + 2 буквы.</summary>
    public static string DedupStem(string? plate)
    {
        var n = Normalize(plate);
        if (LooksLikeMilitaryRuPlate(n) && n.Length >= 6)
            return "M:" + n[..6];
        if (LooksLikeCivilianRuPlate(n) && n.Length >= 6)
            return "C:" + n[..6];
        return n;
    }

    /// <summary>
    /// Слить почти одинаковые чтения (1–2 отличающихся символа) — оставить с большим confidence.
    /// </summary>
    public static List<string> CollapseNearDuplicates(
        IReadOnlyDictionary<string, double> plateToConfidence,
        int maxDistance = 1)
    {
        var meta = plateToConfidence.ToDictionary(
            kv => kv.Key,
            kv => (kv.Value, (string?)null),
            StringComparer.Ordinal);
        return CollapseNearDuplicates(meta, maxDistance).Select(x => x.Plate).ToList();
    }

    /// <summary>
    /// То же, что CollapseNearDuplicates, но сохраняет confidence и кроп номера победителя.
    /// </summary>
    public static List<(string Plate, double Confidence, string? PlateImageBase64)> CollapseNearDuplicates(
        IReadOnlyDictionary<string, (double Confidence, string? PlateImageBase64)> plateToMeta,
        int maxDistance = 1)
    {
        var byStem = new Dictionary<string, (string Plate, double Conf, string? Crop)>(StringComparer.Ordinal);
        foreach (var (plate, meta) in plateToMeta)
        {
            var stem = DedupStem(plate);
            if (!byStem.TryGetValue(stem, out var prev)
                || meta.Confidence > prev.Conf + 0.05
                || (plate.Length > prev.Plate.Length && meta.Confidence >= prev.Conf - 0.08)
                || (plate.Length == prev.Plate.Length && meta.Confidence > prev.Conf))
            {
                // При смене текста на стволе не тащим чужой кроп
                var crop = string.Equals(prev.Plate, plate, StringComparison.Ordinal)
                    ? (meta.PlateImageBase64 ?? prev.Crop)
                    : meta.PlateImageBase64;
                if (!byStem.ContainsKey(stem))
                    crop = meta.PlateImageBase64;
                byStem[stem] = (plate, meta.Confidence, crop);
            }
            else if (meta.PlateImageBase64 != null
                     && prev.Crop == null
                     && string.Equals(prev.Plate, plate, StringComparison.Ordinal))
            {
                byStem[stem] = (prev.Plate, prev.Conf, meta.PlateImageBase64);
            }
        }

        var items = byStem.Values
            .OrderByDescending(x => x.Conf)
            .ThenByDescending(x => x.Plate.Length)
            .ToList();

        var kept = new List<(string Plate, double Confidence, string? PlateImageBase64)>();
        foreach (var (plate, conf, crop) in items)
        {
            var dominated = false;
            foreach (var better in kept)
            {
                if (plate.Length == better.Plate.Length && Hamming(plate, better.Plate) <= maxDistance)
                {
                    dominated = true;
                    break;
                }
            }
            if (!dominated)
                kept.Add((plate, conf, crop));
        }
        return kept;
    }

    private static int Hamming(string a, string b)
    {
        var d = 0;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) d++;
        return d;
    }
}
