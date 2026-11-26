using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Bunnagetaro5.Views;

/// <summary>
/// ☰メニュー。右端から幅280が180msでスライドして出る。閉じるときは逆向き。
/// 出ている間は黒0.2の覆い。覆いクリック・Escで閉じる。
/// 項目は「閉じ始めてから」ページを切り替える（ItemInvoked は閉じ終わりに1度だけ）。途中で逆向きなら今の位置から戻る。
/// 何を開くかは知らない。押された項目の Tag（log/cfg/help/folder/about）を ItemInvoked で伝えるだけ。
/// </summary>
public partial class Drawer : UserControl
{
    private const double DrawerWFull = 280;
    private const int DrawerAnimMs = 180;
    private bool _drawerOpen;
    private bool _drawerAnimating;
    private int _drawerSeq;   // 開閉アニメの回番号（古い完了イベントで状態を壊さない）

    /// <summary>項目クリックで予約した動作。閉じ終わり（FinishDrawer）で一度だけ走る。開き直されたら捨てる</summary>
    private string? _afterDrawerClose;

    /// <summary>項目が選ばれた（閉じ終わってから）。引数は項目の Tag</summary>
    public event Action<string>? ItemInvoked;

    public Drawer()
    {
        InitializeComponent();
        SizeChanged += (_, _) => OnDrawerResize();
    }

    public bool IsOpen => _drawerOpen;

    /// <summary>今いるページの項目にだけ左端のAccent縦線（幅3）を出す。
    /// 「バーの使い方」は今すぐ別のページではないので縦線は出さない</summary>
    public void SetCurrentPage(string page)
    {
        MarkLog.Visibility = page == "log" ? Visibility.Visible : Visibility.Collapsed;
        MarkCfg.Visibility = page == "cfg" ? Visibility.Visible : Visibility.Collapsed;
        MarkHelp.Visibility = Visibility.Collapsed;
    }

    /// <summary>アイコンフォントが無い環境では、アイコンを消して文字だけの項目にする</summary>
    public void UseNoIconFont()
    {
        foreach (var t in new[] { IconLog, IconCfg, IconHelp, IconFolder, IconAbout })
            t.Visibility = Visibility.Collapsed;
        DrawerCloseButton.Content = "☰";
    }

    public void Toggle()
    {
        if (_drawerOpen) Close();
        else Open();
    }

    private void DrawerClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Scrim_Click(object sender, MouseButtonEventArgs e) => Close();

    public void Open()
    {
        if (_drawerOpen) return;
        _afterDrawerClose = null;   // 閉じ待ちの動作は開き直しで捨てる
        _drawerOpen = true;
        DrawerLayer.Visibility = Visibility.Visible;
        DrawerPanel.Width = DrawerWidth();
        // 出した同じフレームで時間ベースのアニメを始めると、ドロワー・覆い・中身の初回
        // レイアウト／描画で最初の数フレームが食われてガクッと跳ぶ。先にレイアウトを済ませ、
        // 次の描画フレームから動き出す（その間は右の外にいて見えない）
        DrawerLayer.UpdateLayout();
        _drawerAnimating = true;
        int id = ++_drawerSeq;
        EventHandler? once = null;
        once = (_, _) =>
        {
            CompositionTarget.Rendering -= once;
            if (id != _drawerSeq || !_drawerOpen) return;   // 待っている間に閉じられた
            AnimateDrawer(0);
        };
        CompositionTarget.Rendering += once;
    }

    public void Close()
    {
        if (!_drawerOpen) return;
        _drawerOpen = false;
        AnimateDrawer(DrawerPanel.Width);
    }

    /// <summary>窓が狭い時は幅を縮める（min(280, max(200, 窓幅-40))）。</summary>
    private double DrawerWidth() => Math.Min(DrawerWFull, Math.Max(200, (Window.GetWindow(this)?.ActualWidth ?? ActualWidth) - 40));

    private void AnimateDrawer(double targetX)
    {
        double current = DrawerX.X;   // アニメ最中なら今見えている位置
        double dist = Math.Abs(targetX - current);
        int id = ++_drawerSeq;
        if (dist < 0.5)
        {
            FinishDrawer(id, targetX);
            return;
        }
        // 途中から逆向きに動く時は残りの距離ぶんの時間
        double durMs = Math.Max(40.0, DrawerAnimMs * dist / Math.Max(1.0, DrawerPanel.Width));
        var anim = new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(durMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        _drawerAnimating = true;
        // 動いている間だけ画面の拡大率で作ったビットマップキャッシュに載せて、移動を軽くする
        // （RenderAtScale を窓のDPIに合わせるので 125/150% でも文字がぼやけない。終わったら外す）
        DrawerPanel.CacheMode = new BitmapCache(VisualTreeHelper.GetDpi(this).DpiScaleX);
        anim.Completed += (_, _) =>
        {
            if (id != _drawerSeq) return;   // もっと新しい開閉が始まっている
            _drawerAnimating = false;
            FinishDrawer(id, targetX);
        };
        DrawerX.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void FinishDrawer(int id, double targetX)
    {
        if (id != _drawerSeq) return;
        DrawerX.BeginAnimation(TranslateTransform.XProperty, null);
        DrawerX.X = targetX;
        DrawerPanel.CacheMode = null;
        // 隠れきったら覆いごと消す（押せない域を残さない）
        if (!_drawerOpen && targetX >= DrawerPanel.Width - 0.5)
        {
            DrawerLayer.Visibility = Visibility.Collapsed;
            // 項目の動作は閉じ終わってから（重いページ切替で引っ込みの途中が固まらない）
            var tag = _afterDrawerClose;
            _afterDrawerClose = null;
            if (tag is not null)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ItemInvoked?.Invoke(tag));
        }
    }

    private void OnDrawerResize()
    {
        if (DrawerLayer.Visibility != Visibility.Visible) return;
        double w = DrawerWidth();
        if (Math.Abs(DrawerPanel.Width - w) < 0.5) return;
        DrawerPanel.Width = w;
        if (!_drawerAnimating)
            DrawerX.X = _drawerOpen ? 0 : w;
    }

    private void DrawerItem_Click(object sender, MouseButtonEventArgs e)
    {
        var tag = (string)((FrameworkElement)sender).Tag;
        // 閉じる動きが終わってから実行（途中で重いページ切替が走ると引っ込みが固まる）
        _afterDrawerClose = tag;
        Close();
    }
}
