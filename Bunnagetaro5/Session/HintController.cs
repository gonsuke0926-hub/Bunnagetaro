using System.Windows.Threading;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Session;

/// <summary>
/// 窓を開いた直後の案内（A3・tw_overlay._show_hint / _hide_hint）を担当する。
/// CaptureSession から分割（段階4・整理）。
/// </summary>
internal sealed class HintController
{
    /// <summary>案内の文言（translator_window.HINT_TEXT を WPF の操作に合わせた）。
    /// クイックぶん投げは既定 OFF（G2）なので、既定ではボタンだけを案内する</summary>
    public const string HintText =
        "ぶん投げたい場所へ窓を重ねてください" + "\n" +
        "窓の中を掴むと動かせます" + "\n" +
        "「ぶん投げる」ボタンでぶん投げる";

    public const int HintMs = 7000;

    private readonly ConfigStore _cfg;
    private readonly Func<bool> _canShow;
    private readonly Func<bool> _canHide;
    private readonly Func<AnswerPanel?> _getPanel;
    private readonly Func<AnswerStrip?> _getStrip;

    /// <summary>いま出している案内の文（答えか案内かの見分けに使う）</summary>
    private string _hintShown = HintText;
    /// <summary>世代トークン。ぶん投げへ移ったり新しい案内が出たら進めて、古い消し予約を無効にする</summary>
    private int _hintTok;
    /// <summary>アプリ起動後に最初に開いた窓の案内だけ、最初の答えが出るまで消さない（v3.4）</summary>
    private bool _hintFirst = true;

    internal string HintShown => _hintShown;
    internal int HintTok => _hintTok;
    internal bool HintFirst { get => _hintFirst; set => _hintFirst = value; }

    internal HintController(ConfigStore cfg, Func<bool> canShow, Func<bool> canHide,
                            Func<AnswerPanel?> getPanel, Func<AnswerStrip?> getStrip)
    {
        _cfg = cfg;
        _canShow = canShow;
        _canHide = canHide;
        _getPanel = getPanel;
        _getStrip = getStrip;
    }

    /// <summary>クイックぶん投げが ON の時は、押したままドラッグするボタンも案内する</summary>
    public static string HintTextFor(bool quickOn, string quickButton)
    {
        if (!quickOn || quickButton == "off") return HintText;
        string btn = quickButton switch
        {
            "back" => "マウスの「戻る」ボタン",
            "forward" => "マウスの「進む」ボタン",
            "both" => "マウスの「戻る」か「進む」ボタン",
            _ => "ホイール",
        };
        return "ぶん投げたい場所へ窓を重ねてください" + "\n" +
               "窓の中を掴むと動かせます" + "\n" +
               "「ぶん投げる」ボタン / " + btn + "を押したままドラッグ でぶん投げる";
    }

    private bool IsHint(string text) => text.Trim() == _hintShown;

    public void ShowHint()
    {
        if (!_canShow()) return;
        var panel = _getPanel();
        var strip = _getStrip();
        // 訳文やストリーミング中の上へは重ねない
        if (panel is { IsVisible: true } || strip is { ShownUp: true }) return;
        _hintTok++;
        int tok = _hintTok;
        _hintShown = HintTextFor(_cfg.Settings.QuickEnabled, _cfg.Settings.QuickButton);
        if (_cfg.EffectiveResultPlace() == "inside") panel?.ApplyBody(_hintShown);
        else strip?.ApplyBody(_hintShown);
        if (_hintFirst)
        {
            // 最初の窓は、実際の答えに置き換わるまで残す（「ここに答えが出ます」の札）
            _hintFirst = false;
            return;
        }
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HintMs) };
        t.Tick += (_, _) => { t.Stop(); HideHint(tok); };
        t.Start();
    }

    /// <summary>
    /// 案内を下ろす。ぶん投げへ移っていたら何もしない。判定は「いま見えている文字が
    /// まだ案内そのものか」（世代トークンだけだと、案内の後に出た答えを消しに行く）
    /// </summary>
    public void HideHint(int tok)
    {
        if (tok != _hintTok || !_canHide()) return;
        _hintTok++;
        if (_getPanel() is { IsVisible: true } p)
        {
            // E4: 2回目以降の案内は薄れて消える（200ms）。隠れた時にまだ案内のままなら本文を空へ
            if (IsHint(p.ModelText))
                p.HideFade(WindowMotion.HintFadeMs, () => { if (IsHint(p.ModelText)) p.ResetRun(); });
            return;
        }
        if (_getStrip() is { } sp && IsHint(sp.ModelText))
            sp.ClearAndHideFade(_hintShown, WindowMotion.HintFadeMs);
    }

    /// <summary>ぶん投げの開始時。案内の自動消しを止める。
    /// 修正J81: 外の解答欄の案内は消さずに残し、最初の思考・答えが届いた時に入れ替える（<see cref="DropStripHintForAnswer"/>。
    /// 人の判断 2026-11-22: 外の時は案内を消さなくてよい。消すと、クイックで「出る→消える→また出る」になった）</summary>
    internal void DropForRun() => _hintTok++;

    /// <summary>キャプチャ窓を閉じた時、開いていた間の案内の消し予約を無効にする</summary>
    internal void DropOnClose() => _hintTok++;

    /// <summary>修正J81: 外の解答欄が案内のままなら、答え（思考・本文）を入れる前に本文を空にする（窓は出したまま）。
    /// 案内の文字が答えの続きや、消える行の吸い込みに混ざらないように</summary>
    internal void DropStripHintForAnswer(AnswerStrip strip)
    {
        if (IsHint(strip.ModelText)) strip.View.ClearBody();
    }
}
