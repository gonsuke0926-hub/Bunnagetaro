namespace Bunnagetaro5.Services;

/// <summary>
/// 修正J10d: 翻訳で書き起こし文を上書きする時、行の訳が終わって残りの英文が外れる所
/// （次の行へ移った時・書き終わり）で、残りをパッと消さず、日本語の末へ吸い込ませる。
/// 残りは左（日本語の末の側）から、上書きと同じ刻みで食われる（J10e）。
/// 画面だけの話で、答えの文（コピー・ログ）は外れた後のもの。ここは文字の計算だけ（テストできる形）
/// </summary>
public static class SuckText
{
    /// <summary>吸い込み中の1行ぶん。Line 行目が Base のままの間だけ、後ろに Suffix の残りを付けて見せる。
    /// 食い方は訳の上書きと同じ刻み（StepMs ごとに StepChars 字ずつ。J10e）</summary>
    public sealed class Ghost
    {
        public int Line;
        public string Base = "";
        public string Suffix = "";
        public long StartMs;
        public double StepMs = DefaultStepMs;
        public double StepChars = DefaultStepChars;

        public int Eaten(long now)
        {
            if (now <= StartMs) return 0;
            double steps = Math.Floor((now - StartMs) / StepMs);
            return (int)Math.Min(Suffix.Length, Math.Floor(steps * StepChars));
        }
        public long EndMs => StartMs + (long)Math.Ceiling(Math.Ceiling(Suffix.Length / StepChars) * StepMs);
    }

    /// <summary>まだ一度も刻みを見ていない時だけの刻み（アプリを立ち上げて最初の翻訳の、最初の流しの前だけ。
    /// 40ms ごとに6字＝150字/秒。流しの間引きと同じ 40ms）</summary>
    public const double DefaultStepMs = 40;
    public const double DefaultStepChars = 6;
    /// <summary>安全のための上限だけ。ふだんは訳の刻みのまま食べる（J10e: 3秒の上限をやめた。
    /// とても遅いモデルで長い残りが、いつまでも残らないようにするためだけのもの）</summary>
    public const int MaxMs = 10000;
    /// <summary>刻みの間がこれより長い時は数えない（モデルが一瞬止まった所で遅く覚えない）</summary>
    public const int MaxGapMs = 500;

    /// <summary>前の文から今の文で、行の後ろが外れた所（今の行が前の行の頭そのもので、前の方が長い）</summary>
    public static List<(int Line, string Base, string Suffix)> Removed(string prev, string next)
    {
        var res = new List<(int, string, string)>();
        if (prev.Length == 0) return res;
        var a = prev.Split('\n');
        var b = next.Split('\n');
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            var nl = b[i];
            if (nl.Length > 0 && a[i].Length > nl.Length && a[i].StartsWith(nl, StringComparison.Ordinal))
                res.Add((i, nl, a[i][nl.Length..]));
        }
        return res;
    }

    /// <summary>今の文に、吸い込み中の残りを付けた「見せる文」。食べ終わった・行が変わったものは ghosts から外す</summary>
    public static string Compose(string target, List<Ghost> ghosts, long now)
    {
        if (ghosts.Count == 0) return target;
        var lines = target.Split('\n');
        ghosts.RemoveAll(g =>
            g.Line >= lines.Length || lines[g.Line] != g.Base || g.Eaten(now) >= g.Suffix.Length);
        if (ghosts.Count == 0) return target;
        foreach (var g in ghosts)
            lines[g.Line] += g.Suffix[g.Eaten(now)..];
        return string.Join("\n", lines);
    }

    /// <summary>2つの文字列の後ろの共通の長さ</summary>
    public static int CommonSuffix(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length), k = 0;
        while (k < n && a[a.Length - 1 - k] == b[b.Length - 1 - k]) k++;
        return k;
    }

    /// <summary>
    /// 上書きの刻みを流れから覚える。今書いている行の、後ろの英文の残り（前後で共通の後ろ）が
    /// 1回の流しで減った字数と、流しの間の時間を数える（訳1字ごとに英文が比率ぶん食われる）
    /// </summary>
    public sealed class RateMeter
    {
        private int _line = -1, _rem;
        private long _t;
        private double _eaten, _ms;
        private int _steps, _gaps;

        /// <summary>前のぶん投げで覚えた刻み（次のぶん投げの、まだ刻みを見ていない間に使う）</summary>
        private (double StepMs, double StepChars)? _carry;

        /// <summary>新しいぶん投げ。今の流れの数えは0から、覚えた刻みは持ち越す</summary>
        public void Reset()
        {
            if (Current is { } c) _carry = c;
            _line = -1; _rem = 0; _eaten = 0; _ms = 0; _steps = 0; _gaps = 0;
        }

        public void Observe(string prev, string next, long now)
        {
            if (prev.Length == 0) { _line = -1; return; }
            var a = prev.Split('\n');
            var b = next.Split('\n');
            int n = Math.Min(a.Length, b.Length), i = 0;
            while (i < n && a[i] == b[i]) i++;
            if (i >= n) { _line = -1; return; }
            // 行の後ろが外れた（行の訳が終わった）所は食われた字数ではない
            if (a[i].StartsWith(b[i], StringComparison.Ordinal)) { _line = -1; return; }
            int rem = CommonSuffix(a[i], b[i]);
            if (rem >= b[i].Length) { _line = -1; return; }   // 前に足しただけ等、上書きではない
            if (i == _line && now > _t && now - _t <= MaxGapMs)
            {
                // 修正J118: 残りが実際に減った回（rem < _rem）だけ時間と歩数を数える。
                // 減らなかった回の待ち時間まで _ms に足すと _ms / _steps が膨らみ、短い文で極端に遅く食われていた
                if (rem < _rem)
                {
                    _ms += now - _t;
                    _gaps++;
                    _eaten += _rem - rem;
                    _steps++;
                }
            }
            _line = i;
            _rem = rem;
            _t = now;
        }

        /// <summary>覚えた速さ（字/秒）。まだ手がかりが少ない時は null</summary>
        public double? Cps => _ms >= 150 && _eaten > 0 ? _eaten / (_ms / 1000.0) : null;
        /// <summary>今の流れで見た刻み（1回でも食われていれば）</summary>
        private (double StepMs, double StepChars)? Current =>
            _steps >= 1 && _ms > 0 ? (_ms / _steps, _eaten / _steps) : null;

        /// <summary>覚えた刻み（1回に食う字数・その間の ms）。今の流れで見ていればそれ、
        /// まだなら前のぶん投げで覚えたもの。どちらも無ければ null</summary>
        public (double StepMs, double StepChars)? Step => Current ?? _carry;
    }

    /// <summary>この長さの残りを食う刻み。覚えた刻み（無ければ既定）で、MaxMs を超える時だけ1回の字数を増やす</summary>
    public static (double StepMs, double StepChars) EffectiveStep((double StepMs, double StepChars)? learned, int suffixLen)
    {
        var (ms, ch) = learned is { } l && l.StepMs > 0 && l.StepChars > 0 ? l : (DefaultStepMs, DefaultStepChars);
        double steps = Math.Max(1, Math.Floor(MaxMs / ms));
        ch = Math.Max(ch, suffixLen / steps);
        return (ms, Math.Max(ch, 1));
    }
}
