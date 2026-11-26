namespace Bunnagetaro5.Status;

/// <summary>
/// 状態欄の文言を決める（Python版 _tick_status / _set_status の移植部分）。窓には文字列だけを渡す。
/// 印のフラグ（Busy など）は、ぶん投げ・前提チェック・巡回が立てる。Tick は350msごとに呼ばれて今の文言を返す。
/// </summary>
public sealed class StatusModel
{
    private string _msg = "待機中";
    private DateTime _shownUtc = DateTime.UtcNow;
    private int _busyDotPhase;           // ぶん投げ中の点 0→1→2→3 と1周期

    /// <summary>巡回のたびに1進む（監視中...の点）</summary>
    public int TrigTicks { get; set; }
    /// <summary>ぶん投げ中</summary>
    public bool Busy { get; set; }
    /// <summary>本文より思考が先に来ている</summary>
    public bool Thinking { get; set; }
    /// <summary>前提チェック（AIサーバー未起動など）。空なら問題なし</summary>
    public string EnvProblem { get; set; } = "";
    /// <summary>モデルを裏で載せている</summary>
    public bool WarmRunning { get; set; }
    /// <summary>動作検知中</summary>
    public bool TrigDetect { get; set; }
    /// <summary>キャプチャ窓が開いている</summary>
    public bool CaptureOpen { get; set; }

    /// <summary>自動トリガが有効か（呼び手が決める）</summary>
    public Func<bool> TriggerEnabled { get; set; } = () => false;

    /// <summary>一言出す。12秒たつと監視中/待機中へ戻る（Python版 _set_status）</summary>
    public void Set(string msg)
    {
        _shownUtc = DateTime.UtcNow;
        _msg = msg;
    }

    // 他の文言に上書きされても、しばらく後ろに付けて残す一言（チャタリングの知らせ）
    private string _note = "";
    private DateTime _noteUntilUtc = DateTime.MinValue;

    /// <summary>後ろに付けて残す一言。sec 秒のあいだ、ぶん投げ中・完了の文言の後ろにも付く</summary>
    public void SetNote(string note, double sec = 20)
    {
        _note = note;
        _noteUntilUtc = DateTime.UtcNow.AddSeconds(sec);
    }

    private string WithNote(string s) =>
        _note.Length > 0 && DateTime.UtcNow < _noteUntilUtc ? s + "　" + _note : s;

    /// <summary>350msごとの文言（Python版 _tick_status）</summary>
    public string Tick() => WithNote(TickCore());

    private string TickCore()
    {
        if (Busy)
        {
            _busyDotPhase = (_busyDotPhase + 1) % 4;
            var dots = new string('.', _busyDotPhase);
            // 本文より先に思考が届いている段は「思考中」で動いている印を出す
            return Thinking ? "思考中" + dots : "ぶん投げ中" + dots;
        }
        _busyDotPhase = 0;
        var d = new string('.', TrigTicks % 4);
        if (EnvProblem.Length > 0)
            // 前提が足りていない間は、監視中／待機中の代わりにその理由だけを出す。
            // 点は付けない（動いている印に見える。AIサーバー未起動・モデル未導入・vision非対応とも。F1）
            return EnvProblem;
        if (WarmRunning)
            return "AIモデル準備中" + d;
        if (TrigDetect && TriggerEnabled())
            return "動作検知中" + d;
        if ((DateTime.UtcNow - _shownUtc).TotalSeconds > 12)
        {
            if (TriggerEnabled())
                return "監視中" + d;
            return "待機中";
        }
        return _msg;
    }
}
