// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

namespace Meltype.Detection;

/// <summary>
/// 英語 / 日本語 (ローマ字) の打ち間違いを直す。直し間違い (FP) を減らすのを最優先にする。
///   入力 → 正規化 → 辞書にそのままあれば終わり → 候補 (SymSpell、削除 2 回まで) → 点数 → 1 位と 2 位の差が θ 以上なら直す、でなければそのまま。
/// 点数 S(c, l) = −α·D(入力, c) + log P₄(c | l) + log P(l | 直前の言語)。
/// D は QWERTY の距離で重みを付けた Damerau-Levenshtein (隣のキーの置換 .4、入れ替え .5、同じ文字の重複 .3、挿入・削除 .7)。
/// </summary>
public sealed class TypoCorrector
{
    public const int MaxDeletes = 2;
    public const int MinLength = 3;

    public enum Language { Japanese, English }

    public readonly record struct Candidate(string Word, Language Language, double Distance, double Score);

    public readonly record struct Result(string Input, string Output, Language Language, bool Corrected, double Margin, IReadOnlyList<Candidate> Candidates);

    /// <summary>点数の距離の重み α。</summary>
    public double Alpha { get; set; } = 6.0;

    /// <summary>直すのに要る、1 位と 2 位 (2 位が無ければ、入力そのまま) の点数の差 θ。</summary>
    public double Theta { get; set; } = 2.0;

    /// <summary>直前と同じ言語の確率 (日本語の後なら P(日本語) = .8。英語も同じ)。</summary>
    public double SameLanguagePrior { get; set; } = 0.8;

    private readonly HashSet<string> _japanese = new(StringComparer.Ordinal);
    private readonly HashSet<string> _english = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _deletes = new(StringComparer.Ordinal);
    private readonly NGramModel _japaneseModel;
    private readonly NGramModel _englishModel;
    private readonly Dictionary<(char Typed, char Intended), double> _learned = [];

    public TypoCorrector(IEnumerable<string> japaneseRomaji, IEnumerable<string> englishWords)
    {
        foreach (var w in japaneseRomaji) AddTo(_japanese, Normalize(w));
        foreach (var w in englishWords) AddTo(_english, w.ToLowerInvariant());
        _japaneseModel = new NGramModel(_japanese);
        _englishModel = new NGramModel(_english);
    }

    /// <summary>同梱の辞書 (japanese.txt のローマ字、english-words.txt) で作る。</summary>
    public static TypoCorrector FromBuiltIn() => new(
        DictionarySource.Load("japanese.txt", null),
        WordList.ParseWords(DictionarySource.ReadEmbedded("english-words.txt")));

    private void AddTo(HashSet<string> set, string word)
    {
        if (word.Length < 2 || !word.All(c => char.IsAsciiLetterLower(c) || c == 'N') || !set.Add(word)) return;
        if (_japanese.Contains(word) && _english.Contains(word)) return; // 両方にある語は 1 回だけ索引に入れる
        foreach (var d in Deletes(word))
        {
            if (!_deletes.TryGetValue(d, out var list)) _deletes[d] = list = [];
            list.Add(word);
        }
    }

    /// <summary>小文字にし、訓令式を揃える (si → shi、tu → tsu、ti → chi、hu → fu、n' / nn → N)。</summary>
    public static string Normalize(string input)
    {
        var s = input.ToLowerInvariant();
        var sb = new System.Text.StringBuilder(s.Length + 4);
        for (var i = 0; i < s.Length; i++)
        {
            var next = i + 1 < s.Length ? s[i + 1] : '\0';
            var prev = i > 0 ? s[i - 1] : '\0';
            if (s[i] == 'n' && (next == '\'' || next == 'n')) { sb.Append('N'); i++; continue; }
            if (next == 'i' && s[i] == 's' && prev != 's' || next == 'i' && s[i] == 't' && prev != 't' && prev != 'c')
            {
                sb.Append(s[i] == 's' ? "sh" : "ch");
                continue;
            }
            if (s[i] == 't' && next == 'u' && prev != 't') { sb.Append("ts"); continue; }
            if (s[i] == 'h' && next == 'u' && prev is not ('s' or 'c' or 'f')) { sb.Append('f'); continue; }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    public Result Correct(string input, Language? previous = null)
    {
        var lower = input.ToLowerInvariant();
        var romaji = Normalize(input);
        if (_english.Contains(lower)) return Keep(input, Language.English);
        if (_japanese.Contains(romaji)) return Keep(input, Language.Japanese);
        if (lower.Length < MinLength || !lower.All(char.IsAsciiLetterLower)) return Keep(input, previous ?? Language.English);

        var candidates = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, lang) in new[] { (lower, Language.English), (romaji, Language.Japanese) })
        {
            foreach (var d in Deletes(key).Append(key))
            {
                if (!_deletes.TryGetValue(d, out var words) && !Has(d)) continue;
                foreach (var w in words is null ? [d] : words.Append(d))
                {
                    if (!seen.Add(w + "|" + lang)) continue;
                    var inJa = _japanese.Contains(w);
                    var inEn = _english.Contains(w);
                    if (lang == Language.Japanese ? !inJa : !inEn) continue;
                    var dist = Distance(key, w);
                    if (dist > MaxDeletes) continue;
                    candidates.Add(new Candidate(w, lang, dist, Score(dist, w, lang, previous)));
                }
            }
        }
        if (candidates.Count == 0) return Keep(input, previous ?? Language.English);
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        var top = candidates[0];
        // 2 位が無ければ「入力のまま」を対抗にする (辞書に無い語を打った可能性)。距離 0 で、言語モデルの点だけ。
        var keepScore = Math.Max(Score(0, lower, Language.English, previous), Score(0, romaji, Language.Japanese, previous)) - Alpha * 0.9;
        var second = candidates.Count > 1 ? Math.Max(candidates[1].Score, keepScore) : keepScore;
        var margin = top.Score - second;
        return margin >= Theta
            ? new Result(input, top.Word, top.Language, true, margin, candidates)
            : new Result(input, input, top.Language, false, margin, candidates);
    }

    private bool Has(string w) => _japanese.Contains(w) || _english.Contains(w);

    private static Result Keep(string input, Language language) => new(input, input, language, false, double.PositiveInfinity, []);

    private double Score(double distance, string word, Language language, Language? previous)
    {
        var prior = previous is null ? 0.5 : previous == language ? SameLanguagePrior : 1 - SameLanguagePrior;
        var model = language == Language.Japanese ? _japaneseModel : _englishModel;
        return -Alpha * distance + model.LogProbabilityPerChar(word) * 2 + Math.Log(prior);
    }

    /// <summary>確定した直し (入力 → 確定) を覚え、その置換を安くする。</summary>
    public void Learn(string typed, string confirmed) => AdjustPairs(typed, confirmed, 0.8);

    /// <summary>直した直後に BackSpace で戻された直し (入力 → 直した語) は、その置換を高くする。</summary>
    public void Reject(string typed, string proposed) => AdjustPairs(typed, proposed, 1.5);

    private void AdjustPairs(string typed, string target, double factor)
    {
        var a = Normalize(typed);
        var b = Normalize(target);
        if (a.Length != b.Length) return;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            var pair = (a[i], b[i]);
            var current = _learned.GetValueOrDefault(pair, 1.0);
            _learned[pair] = Math.Clamp(current * factor, 0.3, 3.0);
        }
    }

    /// <summary>重み付きの Damerau-Levenshtein (隣り合う入れ替えだけを許す、制限付きの版)。</summary>
    public double Distance(string a, string b)
    {
        var d = new double[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i * DeleteCost;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j * InsertCost;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                // 入力 a に余分な文字がある (挿入の打ち間違い)。直前と同じ文字の重複は安い。
                var extra = a[i - 1] == (i >= 2 ? a[i - 2] : '\0') ? DuplicateCost : InsertCost;
                var v = Math.Min(d[i - 1, j] + extra, d[i, j - 1] + DeleteCost);
                v = Math.Min(v, d[i - 1, j - 1] + SubstituteCost(a[i - 1], b[j - 1]));
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, d[i - 2, j - 2] + TransposeCost);
                d[i, j] = v;
            }
        }
        return d[a.Length, b.Length];
    }

    public const double TransposeCost = 0.5;
    public const double DuplicateCost = 0.3;
    public const double InsertCost = 0.7;
    public const double DeleteCost = 0.7;

    public double SubstituteCost(char typed, char intended)
    {
        if (typed == intended) return 0;
        var cost = Qwerty.Distance(typed, intended) <= 1.2 ? 0.4 : 1.0;
        return cost * _learned.GetValueOrDefault((typed, intended), 1.0);
    }

    /// <summary>削除を 0 〜 2 回した文字列 (SymSpell の前もって作る索引と、引くときの両方に使う)。</summary>
    public static IEnumerable<string> Deletes(string word)
    {
        var level = new HashSet<string>(StringComparer.Ordinal) { word };
        var all = new HashSet<string>(StringComparer.Ordinal);
        for (var n = 0; n < MaxDeletes; n++)
        {
            var next = new HashSet<string>(StringComparer.Ordinal);
            foreach (var w in level)
            {
                if (w.Length <= 1) continue;
                for (var i = 0; i < w.Length; i++)
                {
                    var d = w.Remove(i, 1);
                    if (all.Add(d)) next.Add(d);
                }
            }
            level = next;
        }
        return all;
    }

    /// <summary>QWERTY のキーの位置。行ごとのずれは 0 / .25 / .75。</summary>
    public static class Qwerty
    {
        private static readonly string[] Rows = ["qwertyuiop", "asdfghjkl", "zxcvbnm"];
        private static readonly double[] Offsets = [0, 0.25, 0.75];

        public static double Distance(char a, char b)
        {
            if (!TryPosition(char.ToLowerInvariant(a), out var pa) || !TryPosition(char.ToLowerInvariant(b), out var pb)) return double.MaxValue;
            var dx = pa.X - pb.X;
            var dy = pa.Y - pb.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public static bool TryPosition(char c, out (double X, double Y) position)
        {
            for (var r = 0; r < Rows.Length; r++)
            {
                var i = Rows[r].IndexOf(c);
                if (i >= 0) { position = (i + Offsets[r], r); return true; }
            }
            position = default;
            return false;
        }

        /// <summary>隣のキー (距離 1.2 以下) のどれか。評価の雑音作りに使う。</summary>
        public static IEnumerable<char> Neighbors(char c)
        {
            foreach (var row in Rows)
                foreach (var k in row)
                    if (k != c && Distance(c, k) <= 1.2) yield return k;
        }
    }

    /// <summary>文字 4-gram (Witten-Bell ではなく、加算 1 の後ろに引く簡単な版)。語の頭と終わりに印を付ける。</summary>
    private sealed class NGramModel
    {
        private const int N = 4;
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _contexts = new(StringComparer.Ordinal);
        private const int Vocabulary = 29;

        public NGramModel(IEnumerable<string> words)
        {
            foreach (var w in words)
            {
                var s = new string('^', N - 1) + w + "$";
                for (var i = N - 1; i < s.Length; i++)
                    for (var k = 1; k <= N; k++)
                    {
                        var gram = s.Substring(i - k + 1, k);
                        _counts[gram] = _counts.GetValueOrDefault(gram) + 1;
                        _contexts[gram[..^1]] = _contexts.GetValueOrDefault(gram[..^1]) + 1;
                    }
            }
        }

        public double LogProbabilityPerChar(string word)
        {
            var s = new string('^', N - 1) + word + "$";
            var total = 0.0;
            for (var i = N - 1; i < s.Length; i++)
            {
                // 長い文脈から順に、見たことのあるものを使う (後ろに引くたびに log .4 の罰)。
                var p = 0.0;
                var penalty = 0.0;
                for (var k = N; k >= 1; k--)
                {
                    var gram = s.Substring(i - k + 1, k);
                    var c = _counts.GetValueOrDefault(gram);
                    if (c > 0 || k == 1)
                    {
                        p = (c + 0.01) / (_contexts.GetValueOrDefault(gram[..^1]) + 0.01 * Vocabulary);
                        break;
                    }
                    penalty += Math.Log(0.4);
                }
                total += Math.Log(p) + penalty;
            }
            return total / (word.Length + 1);
        }
    }
}
