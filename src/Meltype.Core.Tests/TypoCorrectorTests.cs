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

    private static string AddNoise(string w, Random random)
    {
        var i = random.Next(w.Length - 1);
        switch (random.Next(4))
        {
            case 0:
                var neighbors = Qwerty.Neighbors(w[i]).ToArray();
                return neighbors.Length == 0 ? w : w[..i] + neighbors[random.Next(neighbors.Length)] + w[(i + 1)..];
            case 1: return w[..i] + w[i + 1] + w[i] + w[(i + 2)..];
            case 2: return w[..(i + 1)] + w[i] + w[(i + 1)..];
            default: return w.Remove(i, 1);
        }
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
