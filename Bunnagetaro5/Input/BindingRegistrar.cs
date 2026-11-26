using Bunnagetaro5.Services;

namespace Bunnagetaro5.Input;

/// <summary>設定ページの「登録」欄の見え方（SettingsPage が実装する）。欄の表示を書き換えるだけ</summary>
internal interface IBindFields
{
    void SetBindField(string which, string text);
    void SyncBindFields(string kkeyLabel, string hotkeyLabel, string snipLabel);
    void SetBindNotice(string which, string text);
}

/// <summary>BindingRegistrar が、クイックぶん投げ側・ぶん投げ側へお願いすること</summary>
internal interface IBindingHost
{
    /// <summary>有効なキー起動（検証済みトークン。""=未設定）</summary>
    string QuickKeyTok { get; set; }
    /// <summary>有効なぶん投げホットキー（検証済みトークン。""=未設定）</summary>
    string HotkeyTok { get; set; }
    /// <summary>修正J58: 有効な範囲ドラッグのホットキー（検証済みトークン。""=未設定）</summary>
    string SnipHotkeyTok { get; set; }
    void QuickApply(bool fromUi = false);
    void EnsureQuickPoll();
    /// <summary>長押しドラッグ起動の最中か（ホットキー発火は無視する）</summary>
    bool QuickSessionActive { get; }
    /// <summary>キャプチャ窓が開いていて生きているか</summary>
    bool CaptureAlive();
    /// <summary>いま開いているキャプチャ窓を手動でぶん投げる</summary>
    void ThrowManual();
}

/// <summary>
/// 入力の「登録」「解除」とホットキー発火（手順24 / 7.9・6.4。Python版 tw_binding.py）。
/// 設定ページの「登録」を押すと、次に押されたキー／マウスボタンを1個だけフックが掴んで
/// （他のアプリへ渡さず）その欄に入る。Esc・同じ「登録」をもう一度・8秒押されない で中止。
/// 「起動するボタン」欄は選択肢セグメントで登録ボタンが無いので、which="quick" の経路は無い。
/// </summary>
internal sealed class BindingRegistrar
{
    private readonly ConfigStore _cfg;
    private readonly QuickInputWatcher _quickIn;
    private readonly Action<string> _setStatus;
    private readonly IBindingHost _host;

    /// <summary>設定ページ（欄の表示。後から付く）</summary>
    public IBindFields? Fields { get; set; }

    private string _which = "";   // 待ち中の欄: ""=なし / "kquick" / "hot" / "snip"
    /// <summary>登録待ちを始めた時刻（MinValue=待ってない）</summary>
    internal DateTime CatchAt { get; set; } = DateTime.MinValue;
    private const double BindCatchTimeoutSec = 8.0;      // Python版 QUICK_CATCH_TIMEOUT

    public BindingRegistrar(ConfigStore cfg, QuickInputWatcher quickIn, Action<string> setStatus, IBindingHost host)
    {
        _cfg = cfg;
        _quickIn = quickIn;
        _setStatus = setStatus;
        _host = host;
    }

    /// <summary>登録待ち中か（待っているあいだは cfg の読み直しを止め、巡回を張っておく）</summary>
    public bool Catching => CatchAt != DateTime.MinValue;

    /// <summary>待ち中の欄（""=なし）</summary>
    internal string Which => _which;

    private static string BindKind(string which)
        => which switch { "hot" or "snip" => "キー / ボタン", "kquick" => "キー", _ => "ボタン" };

    private string BindTitle(string? which = null)
        => (which ?? _which) switch { "hot" => "ぶん投げホットキー", "snip" => "範囲ドラッグのキー", "kquick" => "キー起動", _ => "クイックぶん投げ" };

    /// <summary>
    /// 巡回（QuickPoll）の「登録」待ち分岐。待っていなければ false（巡回は他の分岐へ進む）。
    /// 待っていれば押された入力 cap を欄へ反映、8秒押されなければ中止して true
    /// </summary>
    public bool HandlePoll(string? cap)
    {
        if (!Catching) return false;
        if (cap is not null)
        {
            Done(cap);
        }
        else if ((DateTime.UtcNow - CatchAt).TotalSeconds > BindCatchTimeoutSec)
        {
            Abort($"{BindTitle()}: 登録する入力が押されないので中止しました");
        }
        return true;
    }

    /// <summary>「登録」ボタン: 次に来る入力を掴まえる待機に入る。待っているあいだに
    /// 同じ欄の「登録」をもう一度押すと中止、別の欄のを押せば乗り換え（Python版 _bind_start）</summary>
    public void Start(string which)
    {
        Fields?.SetBindNotice("", "");
        if (_which.Length > 0)
        {
            if (_which == which)
            {
                Abort($"{BindTitle()}: 登録をやめました");
                return;
            }
            Stop();
        }
        _which = which;
        try
        {
            _quickIn.StartCatch();
        }
        catch (Exception e)
        {
            _which = "";
            AppLog.Error("input catch start failed", e);
            _setStatus($"{BindTitle(which)}: 入力を登録できません（ログ参照）");
            return;
        }
        CatchAt = DateTime.UtcNow;
        Fields?.SetBindField(which, $"登録する{BindKind(which)}を押してください");
        _setStatus($"{BindTitle(which)}: 登録する{BindKind(which)}を押してください"
                   + $"（Escまたはもう一度「登録」で中止、{(int)BindCatchTimeoutSec}秒で自動中止）");
        // クイックぶん投げOFFでも、掴んだ入力をUIへ返す巡回だけは張っておく
        _host.EnsureQuickPoll();
    }

    /// <summary>登録待ちの状態だけ下ろす（割り当ての適用は呼び手がやる。Python版 _bind_stop）</summary>
    private void Stop()
    {
        _which = "";
        CatchAt = DateTime.MinValue;
        try { _quickIn.StopCatch(); }
        catch (Exception e) { AppLog.Error("input catch stop failed", e); }
        SyncFields();
    }

    /// <summary>割り当て欄をいまの設定表示へ戻す（登録待ち表示の消し忘れ防止。
    /// 「起動するボタン」の欄は選択肢セグメントなのでApply側が既に追従している）</summary>
    public void SyncFields()
        => Fields?.SyncBindFields(Binding.Label(_host.QuickKeyTok), Binding.Label(_host.HotkeyTok),
            Binding.Label(_host.SnipHotkeyTok));

    private void Abort(string msg)
    {
        Stop();
        _setStatus(msg);
        // 登録のために張ったフックを、設定どおり（剥がす／現在の割り当て）へ戻す
        _host.QuickApply();
    }

    /// <summary>フックが掴んだ入力（正規形バインド文字列）をいま待っている欄へ割り当てる。esc は中止（Python版 _bind_done）</summary>
    internal void Done(string raw)
    {
        var tok = Binding.Normalize(raw);
        if (tok == "esc")
        {
            Abort($"{BindTitle()}: 登録をやめました");
            return;
        }
        switch (_which)
        {
            case "hot": HotkeyBindDone(tok); break;
            case "snip": SnipBindDone(tok); break;
            case "kquick": KquickBindDone(tok); break;
            default:
                // 「起動するボタン」は選択肢セグメントで登録入口が無い（6.4）ため、
                // ここは通常来ない。来ても値は変えず待機だけ下ろす
                Stop();
                break;
        }
    }

    /// <summary>キー起動の欄へ拾った入力を割り当てる（キーだけ。マウスボタンは却下。Python版 _kquick_bind_done）</summary>
    private void KquickBindDone(string tok)
    {
        string why = Binding.Validate(tok);
        Stop();
        if (why.Length == 0 && !Binding.IsKey(tok))
            why = "mouse";
        string msg;
        if (why == "bare")
            msg = "そのキー単独だと普通の打鍵を握ってしまうので設定できません"
                + "（F1〜F12などの単独か、Ctrl/Alt/Shiftと組み合わせてください）";
        else if (why == "win")
            msg = "Windowsキー付きはOSのショートカットを潰すため設定できません";
        else if (why == "mouse")
            msg = "キーで開始するにはキーを押してください（マウスボタンで四角を描く起動は"
                + "「起動するボタン」の欄で登録できます）";
        else if (why.Length > 0)
        {
            msg = "その入力は登録できません（ログ参照）";
            AppLog.Info($"quick_key bind rejected: '{tok}' reason={why}");
        }
        else if (tok == _host.HotkeyTok)
        {
            // 同じキーを「四角の開始」と「その場でぶん投げる」の両方にはできない
            msg = "そのキーはぶん投げホットキーに使用中です"
                + "（ぶん投げホットキーを解除するか、別のキーへ登録してください）";
        }
        else if (tok.Length > 0 && tok == _host.SnipHotkeyTok)
        {
            msg = "そのキーは範囲ドラッグのキーに使用中です"
                + "（範囲ドラッグのキーを解除するか、別のキーへ登録してください）";
        }
        else
        {
            QuickKeySet(tok);
            return;
        }
        // 却下: 値は変えない。登録のために張ったフックだけ設定どおりへ戻す
        _host.QuickApply();
        Fields?.SetBindNotice("kquick", msg);
        _setStatus("キー起動: " + msg);
    }

    /// <summary>キー起動の設定をメモリ・ディスク・欄・フックへ反映（""=解除。Python版 _quick_key_set）</summary>
    private void QuickKeySet(string tok)
    {
        _cfg.Settings.QuickKey = tok;
        _host.QuickKeyTok = tok;
        SyncFields();
        Fields?.SetBindNotice("kquick", "");
        _cfg.Save();   // 「登録」は「保存」を待たずディスクへ書く
        _host.QuickApply();
        _setStatus($"キー起動: {Binding.Label(tok)} {(tok.Length > 0 ? "設定しました" : "解除しました")}");
    }

    /// <summary>「解除」ボタン。登録待ちなら先に下ろしてから消す（Python版
    /// _quick_key_clear / _hotkey_clear）。Applyを通るのでフックからも即外れる</summary>
    public void Clear(string which)
    {
        if (which == "kquick")
        {
            if (_which.Length > 0) { Stop(); _host.QuickApply(); }
            QuickKeySet("");
        }
        else if (which == "hot")
        {
            if (_which.Length > 0) { Stop(); _host.QuickApply(); }
            HotkeySet("");
        }
        else if (which == "snip")
        {
            if (_which.Length > 0) { Stop(); _host.QuickApply(); }
            SnipSet("");
        }
    }

    /// <summary>ぶん投げホットキーの欄へ拾った入力を割り当てる（キーでもマウスボタンでも届く。Python版 _hotkey_bind_done）</summary>
    private void HotkeyBindDone(string tok)
    {
        string why = Binding.Validate(tok);
        Stop();
        string msg;
        if (why == "bare")
            msg = "そのキー単独だと普通の打鍵を握ってしまうので設定できません"
                + "（F1〜F12などの単独か、Ctrl/Alt/Shiftと組み合わせてください）";
        else if (why == "win")
            msg = "Windowsキー付きはOSのショートカットを潰すため設定できません";
        else if (why.Length > 0)
        {
            msg = "その入力は登録できません（ログ参照）";
            AppLog.Info($"hotkey bind rejected: '{tok}' reason={why}");
        }
        else if (tok == _host.QuickKeyTok)
        {
            // 同じキーを「四角の開始」と「その場でぶん投げる」の両方にはできない
            // （押すたびにぶん投げが走る）
            msg = "そのキーはクイックぶん投げのキー起動に使用中です"
                + "（キー起動を解除するか、別の入力へ登録してください）";
        }
        else if (tok.Length > 0 && tok == _host.SnipHotkeyTok)
        {
            msg = "その入力は範囲ドラッグのキーに使用中です"
                + "（範囲ドラッグのキーを解除するか、別の入力へ登録してください）";
        }
        else if (tok is "mbutton" or "xbutton1" or "xbutton2")
        {
            // クイックぶん投げの起動に割り当て済みのボタンは長押しドラッグが本体なので
            // 発火待ちにできない（フック側も黙って落とすので、理由をはっきり出す）
            string need = tok switch { "mbutton" => "wheel", "xbutton1" => "back", _ => "forward" };
            bool en = _cfg.Settings.QuickEnabled;
            string qb = _cfg.Settings.QuickButton;
            if (qb is not ("off" or "wheel" or "back" or "forward" or "both")) qb = "wheel";
            if (en && (qb == need || (qb == "both" && need != "wheel")))
                msg = "そのボタンはクイックぶん投げの起動に使用中です"
                    + "（クイックぶん投げをOFFにするか、別の入力へ登録してください）";
            else
            {
                HotkeySet(tok);
                return;
            }
        }
        else
        {
            HotkeySet(tok);
            return;
        }
        // 却下: 値は変えない。登録のために張ったフックだけ設定どおりへ戻す
        _host.QuickApply();
        Fields?.SetBindNotice("hot", msg);
        _setStatus("ぶん投げホットキー: " + msg);
    }

    /// <summary>ぶん投げホットキーの設定をメモリ・ディスク・欄・発火待ちへ反映（""=解除。Python版 _hotkey_set）</summary>
    private void HotkeySet(string tok)
    {
        _cfg.Settings.Hotkey = tok;
        _host.HotkeyTok = tok;
        SyncFields();
        Fields?.SetBindNotice("hot", "");
        _cfg.Save();
        _host.QuickApply();
        _setStatus($"ぶん投げホットキー: {Binding.Label(tok)} {(tok.Length > 0 ? "設定しました" : "解除しました")}");
    }

    /// <summary>範囲ドラッグのキーの欄へ拾った入力を割り当てる（修正J58。ぶん投げホットキーと同じ検証で、
    /// キーでもマウスボタンでも届く。ぶん投げホットキー・キー起動・クイックの起動ボタンと同じ入力は却下）</summary>
    private void SnipBindDone(string tok)
    {
        string why = Binding.Validate(tok);
        Stop();
        string msg;
        if (why == "bare")
            msg = "そのキー単独だと普通の打鍵を握ってしまうので設定できません"
                + "（F1〜F12などの単独か、Ctrl/Alt/Shiftと組み合わせてください）";
        else if (why == "win")
            msg = "Windowsキー付きはOSのショートカットを潰すため設定できません";
        else if (why.Length > 0)
        {
            msg = "その入力は登録できません（ログ参照）";
            AppLog.Info($"snip_hotkey bind rejected: '{tok}' reason={why}");
        }
        else if (tok == _host.HotkeyTok)
        {
            msg = "その入力はぶん投げホットキーに使用中です"
                + "（ぶん投げホットキーを解除するか、別の入力へ登録してください）";
        }
        else if (tok == _host.QuickKeyTok)
        {
            msg = "そのキーはクイックぶん投げのキー起動に使用中です"
                + "（キー起動を解除するか、別の入力へ登録してください）";
        }
        else if (tok is "mbutton" or "xbutton1" or "xbutton2")
        {
            // クイックぶん投げの起動に割り当て済みのボタンは長押しドラッグが本体なので発火待ちにできない
            string need = tok switch { "mbutton" => "wheel", "xbutton1" => "back", _ => "forward" };
            bool en = _cfg.Settings.QuickEnabled;
            string qb = _cfg.Settings.QuickButton;
            if (qb is not ("off" or "wheel" or "back" or "forward" or "both")) qb = "wheel";
            if (en && (qb == need || (qb == "both" && need != "wheel")))
                msg = "そのボタンはクイックぶん投げの起動に使用中です"
                    + "（クイックぶん投げをOFFにするか、別の入力へ登録してください）";
            else
            {
                SnipSet(tok);
                return;
            }
        }
        else
        {
            SnipSet(tok);
            return;
        }
        // 却下: 値は変えない。登録のために張ったフックだけ設定どおりへ戻す
        _host.QuickApply();
        Fields?.SetBindNotice("snip", msg);
        _setStatus("範囲ドラッグのキー: " + msg);
    }

    /// <summary>範囲ドラッグのキーの設定をメモリ・ディスク・欄・発火待ちへ反映（""=解除）</summary>
    private void SnipSet(string tok)
    {
        _cfg.Settings.SnipHotkey = tok;
        _host.SnipHotkeyTok = tok;
        SyncFields();
        Fields?.SetBindNotice("snip", "");
        _cfg.Save();
        _host.QuickApply();
        _setStatus($"範囲ドラッグのキー: {Binding.Label(tok)} {(tok.Length > 0 ? "設定しました" : "解除しました")}");
    }

    /// <summary>発火: いま開いているキャプチャ窓をその場でぶん投げる。窓が開いていなければ
    /// ぶん投げず状態欄だけ（モーダルな警告窓は出さない。Python版 _hotkey_fire）</summary>
    public void HotkeyFire(string tok)
    {
        if (_host.QuickSessionActive)
            return;   // 長押しドラッグ起動の最中（離したときにぶん投げられる）なので無視
        if (!_host.CaptureAlive())
        {
            _setStatus($"ぶん投げホットキー({Binding.Label(tok)}): キャプチャ窓が開いていません");
            return;
        }
        _host.ThrowManual();
    }
}
