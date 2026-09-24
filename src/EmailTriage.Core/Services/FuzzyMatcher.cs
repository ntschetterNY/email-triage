namespace EmailTriage.Core.Services;

/// <summary>
/// Subsequence scorer in the style of fzf: every query character must appear in
/// order, and the score rewards matches that land on word boundaries and run
/// consecutively. A plain "contains" filter is not enough here, because the
/// point of the move palette is that typing "acinv" finds "Clients\Acme\Invoices".
/// </summary>
public static class FuzzyMatcher
{
    private const int ScoreMatch = 16;
    private const int BonusBoundary = 10;
    private const int BonusCamel = 6;
    private const int BonusConsecutive = 8;
    private const int BonusExactCase = 1;
    private const int PenaltyGapStart = -3;
    private const int PenaltyGapExtend = -1;

    /// <summary>
    /// Scores <paramref name="query"/> against <paramref name="target"/>.
    /// Returns null when the query is not a subsequence of the target.
    /// An empty query scores 0 and matches everything.
    /// </summary>
    public static int? Score(string query, string target)
        => Score(query, target, out _);

    /// <summary>
    /// As <see cref="Score(string,string)"/>, but also reports the index of each
    /// matched character so the UI can highlight them.
    /// </summary>
    public static int? Score(string query, string target, out int[] positions)
    {
        positions = Array.Empty<int>();

        if (string.IsNullOrEmpty(query)) return 0;
        if (string.IsNullOrEmpty(target)) return null;
        if (query.Length > target.Length) return null;

        // Cheap reject before paying for the matrix.
        if (!IsSubsequence(query, target)) return null;

        int n = query.Length, m = target.Length;

        var bonus = new int[m];
        for (int j = 0; j < m; j++) bonus[j] = BoundaryBonus(target, j);

        // match[i, j]: best score for query[0..i] where query[i] matches target[j].
        // best[i, j]:  best score for query[0..i] using only target[0..j].
        var match = new int[n, m];
        var best = new int[n, m];
        const int NegInf = int.MinValue / 4;

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < m; j++)
            {
                match[i, j] = NegInf;

                if (Eq(query[i], target[j]))
                {
                    int gained;
                    if (i == 0)
                    {
                        // First query char: only the positional bonus applies.
                        gained = ScoreMatch + bonus[j] * 2;
                    }
                    else if (j == 0)
                    {
                        gained = NegInf;
                    }
                    else
                    {
                        int fromGap = best[i - 1, j - 1];
                        int fromRun = match[i - 1, j - 1];

                        int viaGap = fromGap <= NegInf ? NegInf : fromGap + ScoreMatch + bonus[j];

                        // A consecutive match keeps the larger of the run bonus
                        // and the boundary bonus, so "Inv" still scores well at
                        // the start of "Invoices".
                        int viaRun = fromRun <= NegInf
                            ? NegInf
                            : fromRun + ScoreMatch + Math.Max(BonusConsecutive, bonus[j]);

                        gained = Math.Max(viaGap, viaRun);
                    }

                    if (gained > NegInf && query[i] == target[j]) gained += BonusExactCase;
                    match[i, j] = gained;
                }

                int carry;
                if (j == 0)
                {
                    carry = NegInf;
                }
                else
                {
                    int prev = best[i, j - 1];
                    // Gaps cost, but the first skipped character costs the most.
                    bool extending = j >= 2 && best[i, j - 1] == best[i, j - 2] + PenaltyGapExtend;
                    carry = prev <= NegInf
                        ? NegInf
                        : prev + (extending ? PenaltyGapExtend : PenaltyGapStart);
                }

                best[i, j] = Math.Max(match[i, j], carry);
            }
        }

        int endJ = -1, bestScore = NegInf;
        for (int j = 0; j < m; j++)
        {
            if (match[n - 1, j] > bestScore)
            {
                bestScore = match[n - 1, j];
                endJ = j;
            }
        }

        if (endJ < 0 || bestScore <= NegInf) return null;

        positions = Backtrack(match, best, n, endJ, NegInf);
        return bestScore;
    }

    private static int[] Backtrack(int[,] match, int[,] best, int n, int endJ, int negInf)
    {
        var result = new int[n];
        int j = endJ;

        for (int i = n - 1; i >= 0; i--)
        {
            // Walk left to the column where this query character actually matched.
            while (j >= 0 && match[i, j] <= negInf) j--;
            if (j < 0) break;

            if (i > 0)
            {
                int target = match[i, j];
                int scan = j;
                while (scan > 0 && match[i, scan] != target) scan--;
                j = scan;
            }

            result[i] = j;
            j--;
        }

        return result;
    }

    private static bool IsSubsequence(string query, string target)
    {
        int qi = 0;
        for (int ti = 0; ti < target.Length && qi < query.Length; ti++)
        {
            if (Eq(query[qi], target[ti])) qi++;
        }
        return qi == query.Length;
    }

    private static bool Eq(char a, char b) =>
        a == b || char.ToLowerInvariant(a) == char.ToLowerInvariant(b);

    /// <summary>
    /// How "important" a position is: the start of a path segment or word scores
    /// highest, an interior capital next, everything else nothing.
    /// </summary>
    private static int BoundaryBonus(string target, int j)
    {
        char c = target[j];
        if (!char.IsLetterOrDigit(c)) return 0;

        if (j == 0) return BonusBoundary;

        char prev = target[j - 1];
        if (!char.IsLetterOrDigit(prev)) return BonusBoundary;
        if (char.IsLower(prev) && char.IsUpper(c)) return BonusCamel;
        if (char.IsLetter(prev) && char.IsDigit(c)) return BonusCamel;

        return 0;
    }
}
