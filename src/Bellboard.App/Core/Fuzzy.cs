namespace Bellboard.Core;

/// <summary>Command-palette matching: substring hits rank highest, then in-order subsequences (word starts and runs score more).</summary>
public static class Fuzzy
{
    /// <summary>0 = no match; higher is better.</summary>
    public static int Score(string text, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 1;
        if (string.IsNullOrEmpty(text)) return 0;
        var t = text.ToLowerInvariant();
        var q = query.Trim().ToLowerInvariant();

        int score = 0;
        int idx = t.IndexOf(q, System.StringComparison.Ordinal);
        if (idx >= 0) score += 100 + q.Length * 4 + (idx == 0 ? 50 : IsBoundary(text, idx) ? 30 : 0);

        int ti = 0, streak = 0, sub = 0;
        foreach (var qc in q)
        {
            if (qc == ' ') continue;
            int found = t.IndexOf(qc, ti);
            if (found < 0) return idx >= 0 ? score : 0;
            streak = found == ti ? streak + 1 : 0;
            sub += 1 + streak * 3 + (IsBoundary(text, found) ? 6 : 0);
            ti = found + 1;
        }
        return score + sub;
    }

    private static bool IsBoundary(string s, int i) =>
        i == 0 || !char.IsLetterOrDigit(s[i - 1]) || (char.IsUpper(s[i]) && char.IsLower(s[i - 1]));
}
