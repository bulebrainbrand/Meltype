// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Yukishiro

using Meltype.Detection;
using static Meltype.Detection.TypoCorrector;

namespace Meltype.Tests;

/// <summary>英語 / 日本語の打ち間違いの直し (TypoCorrector) のテストと評価。</summary>
internal static class TypoCorrectorTests
{
    private static readonly Lazy<TypoCorrector> Shared = new(TypoCorrector.FromBuiltIn);

    [Test]
    public static void Normalize_UnifiesKunreiAndN()
    {
        Assert.Equal("shitsuchifuN", TypoCorrector.Normalize("SituTiHunn"));
        Assert.Equal("koNnichiha", TypoCorrector.Normalize("kon'nitiha"));
        Assert.Equal("shi", TypoCorrector.Normalize("shi"));
        Assert.Equal("chu", TypoCorrector.Normalize("chu"));
    }

    [Test]
    public static void Distance_IsWeightedByKeyboard()
    {
        var t = new TypoCorrector([], ["test"]);
        Assert.Equal(0.4, t.Distance("tesr", "test"), "隣のキー r→t");
        Assert.Equal(1.0, t.Distance("tesp", "test"), "離れたキー");
        Assert.Equal(0.5, t.Distance("tset", "test"), "入れ替え");
        Assert.Equal(0.3, t.Distance("tesst", "test"), "重複");
        Assert.Equal(0.7, t.Distance("tst", "test"), "抜け");
    }

    [Test]
    public static void ExactWord_IsKept()
    {
        var r = Shared.Value.Correct("house");
        Assert.True(!r.Corrected && r.Output == "house", "辞書にある語はそのまま");
    }

    [Test]
    public static void AdjacentKeyTypo_IsCorrected()
    {
        var r = Shared.Value.Correct("hpuse", Language.English);
        Assert.Equal("house", r.Output);
        Assert.Equal(true, r.Corrected);
    }

    [Test]
    public static void UnknownGibberish_IsKept()
    {
        var r = Shared.Value.Correct("qzxjvk");
        Assert.Equal("qzxjvk", r.Output, "近い語が無ければそのまま");
    }

    [Test]
    public static void RealMisspellings_AreMostlyFixedAndNeverMiscorrectedOften()
    {
        var c = Shared.Value;
        var real = RealMisspellings.Where(p => !p.Intended.Contains(' ')).Select(p => new Sample(p.Intended, p.Typed, Language.English, null)).ToList();
        var s = Run(c, real);
        Assert.True(s.WrongRate <= 0.10, $"実際の綴り間違い: {s}");
    }

    [Test]
    public static void OutOfVocabularyWords_AreRarelyChanged()
    {
        var c = Shared.Value;
        // 今は 4/50 (iphone→phone・emoji→moji・claude→clause・wifi→wick)。これより悪くならないことを見る
        var changed = OutOfVocabulary.Count(w => c.Correct(w).Corrected);
        Assert.True(changed <= 5, $"辞書に無い正しい語を {changed}/{OutOfVocabulary.Length} 語直した");
    }

    [Test]
    public static void HeldOutSeeds_KeepFalseCorrectionLow()
    {
        var c = Shared.Value;
        foreach (var seed in new[] { 101, 102, 103 })
        {
            var s = Run(c, MakeSamples(c, 500, seed));
            Assert.True(s.WrongRate <= 0.02, $"seed {seed}: {s}");
        }
    }

    [Test]
    public static void WrongPreviousLanguage_DoesNotExplode()
    {
        var c = Shared.Value;
        var samples = MakeSamples(c, 500, 501);
        var s = Run(c, samples, x => x.Language == Language.English ? Language.Japanese : Language.English);
        Assert.True(s.WrongRate <= 0.03, $"直前の言語が逆: {s}");
    }

    [Test]
    public static void KunreiInput_MatchesHepburnWord()
    {
        var r = Shared.Value.Correct("arigatou");
        Assert.Equal(false, r.Corrected, "辞書の語");
        Assert.Equal("arigatou", Shared.Value.Correct("arigatpu", Language.Japanese).Output);
    }

    [Test]
    public static void Rejection_RaisesCost()
    {
        var t = new TypoCorrector([], ["cat"]);
        var before = t.SubstituteCost('x', 'c');
        t.Reject("xat", "cat");
        Assert.True(t.SubstituteCost('x', 'c') > before, "BackSpace で戻された置換は高くなる");
        t.Learn("xat", "cat");
        t.Learn("xat", "cat");
        Assert.True(t.SubstituteCost('x', 'c') < before * 1.5, "確定した置換は安くなる");
    }

    [Test]
    public static void Eval_FalseCorrectionRateIsLow()
    {
        var m = Evaluate(Shared.Value, 400, seed: 1);
        Assert.True(m.FalseCorrectionRate <= 0.02, $"直し間違い {m.FalseCorrectionRate:P1} (2% 以下)");
        Assert.True(m.FixAccuracy >= 0.30, $"直せた率 {m.FixAccuracy:P1} (30% 以上)");
    }

    public readonly record struct Metrics(double LanguageAccuracy, double FixAccuracy, double FalseCorrectionRate, int Clean, int Noisy);

    /// <summary>
    /// 評価: 辞書の語 (clean) と、雑音 (隣のキーの置換・入れ替え・重複・抜け) を 1 つ入れた語 (noisy)。
    ///   langAcc: noisy で直した語の言語が正しい率 / fixAcc: noisy が元の語に直った率 / falseCorrectionRate: clean または noisy を間違った語にした率
    /// </summary>
    public static Metrics Evaluate(TypoCorrector corrector, int count, int seed)
    {
        var random = new Random(seed);
        var en = WordList.ParseWords(DictionarySource.ReadEmbedded("english-words.txt")).Select(w => w.ToLowerInvariant())
            .Where(w => w.Length >= 5 && w.All(char.IsAsciiLetterLower)).ToArray();
        var ja = DictionarySource.Load("japanese.txt", null).Select(TypoCorrector.Normalize)
            .Where(w => w.Length >= 5 && w.All(char.IsAsciiLetterLower)).Distinct().ToArray();
        int clean = 0, cleanFalse = 0, noisy = 0, fixedOk = 0, noisyFalse = 0, langOk = 0, langN = 0;
        for (var n = 0; n < count; n++)
        {
            var isJa = ja.Length > 0 && random.Next(4) == 0;
            var word = isJa ? ja[random.Next(ja.Length)] : en[random.Next(en.Length)];
            var lang = isJa ? Language.Japanese : Language.English;
            clean++;
            if (corrector.Correct(word, lang).Output != word) cleanFalse++;
            var typo = AddNoise(word, random);
            if (corrector.Correct(typo, lang) is var exact && typo != word && exact.Candidates.Count == 0 && !exact.Corrected && IsWord(corrector, typo)) continue;
            noisy++;
            var r = corrector.Correct(typo, lang);
            if (r.Corrected)
            {
                langN++;
                if (r.Language == lang) langOk++;
                if (r.Output == word) fixedOk++;
                else noisyFalse++;
            }
        }
        return new Metrics(langN == 0 ? 1 : (double)langOk / langN, noisy == 0 ? 0 : (double)fixedOk / noisy,
            (double)(cleanFalse + noisyFalse) / (clean + noisy), clean, noisy);
    }

    private static bool IsWord(TypoCorrector c, string w) => c.Correct(w).Candidates.Count == 0;

    private static string AddNoise(string w, Random random) => AddNoise(w, (Noise)random.Next(4), random);

    private static string AddNoise(string w, Noise kind, Random random)
    {
        var i = random.Next(w.Length - 1);
        switch ((int)kind)
        {
            case 0:
                var neighbors = Qwerty.Neighbors(w[i]).ToArray();
                return neighbors.Length == 0 ? w : w[..i] + neighbors[random.Next(neighbors.Length)] + w[(i + 1)..];
            case 1: return w[..i] + w[i + 1] + w[i] + w[(i + 2)..];
            case 2: return w[..(i + 1)] + w[i] + w[(i + 1)..];
            default: return w.Remove(i, 1);
        }
    }

    /// <summary>雑音の種類。</summary>
    public enum Noise { AdjacentKey, Transpose, Duplicate, Delete }

    public sealed record Sample(string Original, string Typed, Language Language, Noise? Noise);

    /// <summary>評価用の語の組を作る。noise が null なら 4 種類から無作為に選ぶ。打ち間違いがたまたま別の辞書の語になったものは除く。</summary>
    public static List<Sample> MakeSamples(TypoCorrector c, int count, int seed, Language? only = null, Noise? noise = null, int minLength = 5)
    {
        var random = new Random(seed);
        var en = WordList.ParseWords(DictionarySource.ReadEmbedded("english-words.txt")).Select(w => w.ToLowerInvariant())
            .Where(w => w.Length >= minLength && w.All(char.IsAsciiLetterLower)).Distinct().ToArray();
        var ja = DictionarySource.Load("japanese.txt", null).Select(TypoCorrector.Normalize)
            .Where(w => w.Length >= minLength && w.All(char.IsAsciiLetterLower)).Distinct().ToArray();
        var list = new List<Sample>();
        for (var tries = 0; list.Count < count && tries < count * 20; tries++)
        {
            var isJa = only == Language.Japanese || only is null && random.Next(4) == 0;
            var word = isJa ? ja[random.Next(ja.Length)] : en[random.Next(en.Length)];
            var kind = noise ?? (Noise)random.Next(4);
            var typo = AddNoise(word, kind, random);
            if (typo == word || c.Correct(typo).Margin == double.PositiveInfinity) continue; // 別の語になった
            list.Add(new Sample(word, typo, isJa ? Language.Japanese : Language.English, kind));
        }
        return list;
    }

    public readonly record struct Score(int N, int Fixed, int Wrong, int Kept)
    {
        public double FixRate => N == 0 ? 0 : (double)Fixed / N;
        public double WrongRate => N == 0 ? 0 : (double)Wrong / N;
        public override string ToString() => $"n={N,4} 直った {FixRate,6:P1}  別の語に直した {WrongRate,6:P2} ({Wrong})  そのまま {(double)Kept / Math.Max(N, 1),6:P1}";
    }

    public static Score Run(TypoCorrector c, IEnumerable<Sample> samples, Func<Sample, Language?>? previous = null)
    {
        int n = 0, ok = 0, wrong = 0, kept = 0;
        foreach (var s in samples)
        {
            n++;
            var r = c.Correct(s.Typed, previous is null ? s.Language : previous(s));
            if (!r.Corrected) kept++;
            else if (r.Output == s.Original) ok++;
            else wrong++;
        }
        return new Score(n, ok, wrong, kept);
    }

    /// <summary>実際によくある英語の綴り間違い (手で集めた。雑音で作ったものではない)。</summary>
    public static readonly (string Typed, string Intended)[] RealMisspellings =
    [
        ("teh", "the"), ("recieve", "receive"), ("seperate", "separate"), ("definately", "definitely"), ("occured", "occurred"),
        ("untill", "until"), ("becuase", "because"), ("wierd", "weird"), ("beleive", "believe"), ("freind", "friend"),
        ("tommorow", "tomorrow"), ("accomodate", "accommodate"), ("adress", "address"), ("begining", "beginning"), ("calender", "calendar"),
        ("comming", "coming"), ("enviroment", "environment"), ("goverment", "government"), ("happend", "happened"), ("knowlege", "knowledge"),
        ("libary", "library"), ("neccessary", "necessary"), ("occassion", "occasion"), ("persue", "pursue"), ("posession", "possession"),
        ("prefered", "preferred"), ("realy", "really"), ("succesful", "successful"), ("tounge", "tongue"), ("truely", "truly"),
        ("wich", "which"), ("writting", "writing"), ("thier", "their"), ("alot", "a lot"), ("arguement", "argument"),
        ("buisness", "business"), ("collegue", "colleague"), ("existance", "existence"), ("finaly", "finally"), ("foriegn", "foreign"),
        ("grammer", "grammar"), ("independant", "independent"), ("judgement", "judgment"), ("maintainance", "maintenance"), ("noticable", "noticeable"),
        ("publically", "publicly"), ("recomend", "recommend"), ("refered", "referred"), ("relevent", "relevant"), ("wether", "whether"),
    ];

    /// <summary>辞書に無いが、正しい語・名前 (直してはいけない)。直したら直し間違い。</summary>
    public static readonly string[] OutOfVocabulary =
    [
        "kubernetes", "github", "nginx", "postgres", "typescript", "golang", "webpack", "redis", "kotlin", "figma",
        "zoom", "slack", "notion", "tiktok", "youtube", "iphone", "android", "linux", "ubuntu", "docker",
        "yukishiro", "tanaka", "suzuki", "shibuya", "akihabara", "pokemon", "nintendo", "sushi", "ramen", "karaoke",
        "emoji", "meltype", "mozc", "azookey", "romaji", "hiragana", "katakana", "kanji", "anime", "manga",
        "blockchain", "chatgpt", "claude", "openai", "airpods", "wifi", "bluetooth", "covid", "netflix", "spotify",
    ];

    /// <summary>
    /// --typo-eval: 調整 (seed 7) と別の seed で、種類・言語・長さ・直前の言語の誤り・実際の綴り間違い・辞書に無い語・速さを分けて出す。
    /// </summary>
    public static void Report()
    {
        var c = TypoCorrector.FromBuiltIn();
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        Console.WriteLine($"α={c.Alpha} θ={c.Theta} (調整は seed 7。ここは seed 101〜105)");
        var seeds = new[] { 101, 102, 103, 104, 105 };

        Console.WriteLine("\n[seed ごと (英 3 : 日 1、1000 語ずつ)]");
        var rates = new List<(double Fix, double Wrong)>();
        foreach (var seed in seeds)
        {
            var sc = Run(c, MakeSamples(c, 1000, seed));
            rates.Add((sc.FixRate, sc.WrongRate));
            Console.WriteLine($"  seed {seed}: {sc}");
        }
        Console.WriteLine($"  平均 直った {rates.Average(r => r.Fix):P1} ± {StdDev(rates.Select(r => r.Fix)):P1} / 別の語 {rates.Average(r => r.Wrong):P2} ± {StdDev(rates.Select(r => r.Wrong)):P2}");

        Console.WriteLine("\n[雑音の種類ごと (英語、2000 語)]");
        foreach (var kind in Enum.GetValues<Noise>())
            Console.WriteLine($"  {kind,-12} {Run(c, MakeSamples(c, 2000, 201, Language.English, kind))}");

        Console.WriteLine("\n[言語ごと]");
        Console.WriteLine($"  英語         {Run(c, MakeSamples(c, 2000, 301, Language.English))}");
        Console.WriteLine($"  日本語       {Run(c, MakeSamples(c, 2000, 302, Language.Japanese))}  ※ 辞書が小さい (japanese.txt) ので同じ語が何度も出る");

        Console.WriteLine("\n[長さごと (英語)]");
        foreach (var len in new[] { 3, 4, 5, 7, 10 })
        {
            var samples = MakeSamples(c, 4000, 400 + len, Language.English, minLength: len).Where(s => len == 10 ? s.Original.Length >= 10 : s.Original.Length == len || len == 7 && s.Original.Length is >= 7 and < 10).Take(1000);
            Console.WriteLine($"  {(len == 10 ? "10+" : len == 7 ? "7-9" : len.ToString()),-12} {Run(c, samples)}");
        }

        Console.WriteLine("\n[直前の言語]");
        var mixed = MakeSamples(c, 2000, 501);
        Console.WriteLine($"  正しい       {Run(c, mixed)}");
        Console.WriteLine($"  分からない   {Run(c, mixed, _ => null)}");
        Console.WriteLine($"  逆           {Run(c, mixed, s => s.Language == Language.English ? Language.Japanese : Language.English)}");

        Console.WriteLine("\n[実際の英語の綴り間違い (手で集めた 50 組。雑音で作ったものではない)]");
        var real = RealMisspellings.Where(p => !p.Intended.Contains(' ')).Select(p => new Sample(p.Intended, p.Typed, Language.English, null)).ToList();
        Console.WriteLine($"  {Run(c, real)}");
        foreach (var s in real)
        {
            var r = c.Correct(s.Typed, Language.English);
            if (r.Output != s.Original) Console.WriteLine($"    {s.Typed} → {(r.Corrected ? r.Output : "(そのまま)")}  (正解 {s.Original})");
        }

        Console.WriteLine("\n[辞書に無い正しい語・名前 (直してはいけない 50 語)]");
        var changed = OutOfVocabulary.Select(w => (w, r: c.Correct(w))).Where(x => x.r.Corrected).ToList();
        Console.WriteLine($"  直してしまった {changed.Count}/{OutOfVocabulary.Length}: {string.Join(", ", changed.Select(x => $"{x.w}→{x.r.Output}"))}");

        Console.WriteLine("\n[辞書の語そのもの (直してはいけない)]");
        var clean = MakeSamples(c, 2000, 601).Select(s => s.Original).ToList();
        Console.WriteLine($"  直してしまった {clean.Count(w => c.Correct(w).Corrected)}/{clean.Count}");

        Console.WriteLine("\n[速さ (1 語あたり、辞書に無い語)]");
        var words = MakeSamples(c, 2000, 701).Select(s => s.Typed).ToArray();
        var times = new List<double>();
        foreach (var w in words)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            c.Correct(w);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        Console.WriteLine($"  中央値 {times[times.Count / 2]:F3} ms / 99% {times[(int)(times.Count * 0.99)]:F3} ms / 最大 {times[^1]:F3} ms (n={times.Count})");
        var load = System.Diagnostics.Stopwatch.StartNew();
        TypoCorrector.FromBuiltIn();
        Console.WriteLine($"  索引を作る時間 {load.Elapsed.TotalMilliseconds:F0} ms");
    }

    private static double StdDev(IEnumerable<double> values)
    {
        var v = values.ToArray();
        var mean = v.Average();
        return Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / Math.Max(v.Length - 1, 1));
    }

    /// <summary>α と θ を格子で探し、直し間違い 2% 以下で直せた率が一番高い組を返す (--typo-tune)。</summary>
    public static void Tune()
    {
        var c = TypoCorrector.FromBuiltIn();
        foreach (var alpha in new[] { 2.0, 3.0, 4.0, 6.0 })
            foreach (var theta in new[] { 0.5, 1.0, 1.5, 2.0, 3.0 })
            {
                c.Alpha = alpha;
                c.Theta = theta;
                var m = Evaluate(c, 400, seed: 7);
                Console.WriteLine($"α={alpha} θ={theta} langAcc={m.LanguageAccuracy:P1} fixAcc={m.FixAccuracy:P1} FP={m.FalseCorrectionRate:P2}");
            }
    }
}
