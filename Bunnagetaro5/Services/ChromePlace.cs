namespace Bunnagetaro5.Services;

/// <summary>
/// バー（chrome）の置き場所を決める純粋な計算。窓の実体には触れない。
/// 単位はすべて Win32 物理px（DPI スケーリング 125〜150% 環境でのズレを防ぐため、
/// 計測も配置も Win32 基準に統一）。
/// </summary>
public static class ChromePlace
{
    /// <summary>全部が画面の外へ出て掴めなくなる時に最低限見せるハンドルの高さ（物理px）</summary>
    public const int MinVisibleHandle = 24;

    /// <summary>四角(x,y,w,h)と矩形(l,t,r,b)が重なるか</summary>
    public static bool Intersects(int x, int y, int w, int h, int l, int t, int r, int b)
        => x < r && x + w > l && y < b && y + h > t;

    /// <summary>
    /// バーを (x,y) 幅w高さh で置けるか。
    /// 仮想デスクトップに収まり、キャプチャ窓の内側（client）に重ならないこと
    /// </summary>
    public static bool Fits(int x, int y, int w, int h, Native.RECT vd, Native.RECT client)
    {
        if (x < vd.Left || y < vd.Top || x + w > vd.Right || y + h > vd.Bottom)
            return false;
        if (Intersects(x, y, w, h, client.Left, client.Top, client.Right, client.Bottom))
            return false;
        return true;
    }

    /// <summary>
    /// バーの左上を選ぶ。
    ///  1. バー単独ドラッグ中は、その場所が収まるなら今の位置を守る
    ///  2. 分離モードで記憶位置あり → 記憶位置 → 同じXで窓の下（窓の上へは回さない）
    ///  3. 分離モードでオフセットあり（位置記憶が外れていても試す） → 窓＋オフセット
    ///  4. 連結（上記が通らない時）の既定 → client左端基準で窓の下。
    ///     下に収まらない時（窓が画面の下端など）も窓の上へは移らず、画面の中に入るよう縦にずらすだけ
    ///     （窓の下側に収まらないなら窓の下辺に重なる。client と重なってもよい最後の手）
    /// 窓の上に置くのは、ユーザーが今バーを掴んで置いた位置（1.）と記憶位置・オフセット（2.3.）だけ。
    /// 戻り値は今は null にならない（型は互換のため残す）
    /// </summary>
    /// <param name="winL/winT/winR/winB">キャプチャ窓の実外枠（GetWindowRect）</param>
    /// <param name="client">キャプチャ窓の内側の画面矩形（ClientRectScreen）</param>
    /// <param name="barW/barH">バーの外枠サイズ（物理px）</param>
    /// <param name="pos">バーの位置記憶（_chrome_pos）。物理px左上</param>
    /// <param name="offset">バーと窓の左上の差（_chrome_offset）</param>
    /// <param name="linked">くっつけるON</param>
    /// <param name="vd">仮想デスクトップ矩形</param>
    /// <param name="dragPos">バー単独ドラッグ中の狙った位置（収まっているときだけ渡す）</param>
    public static (int X, int Y)? Pick(
        int winL, int winT, int winR, int winB,
        Native.RECT client,
        int barW, int barH,
        (int X, int Y)? pos,
        (int X, int Y)? offset,
        bool linked,
        Native.RECT vd,
        (int X, int Y)? dragPos = null)
    {
        if (dragPos is { } dp &&
            Fits(dp.X, dp.Y, barW, barH, vd, client))
            return dp;

        if (!linked && pos is { } p)
        {
            if (Fits(p.X, p.Y, barW, barH, vd, client)) return (p.X, p.Y);
            if (Fits(p.X, winB, barW, barH, vd, client)) return (p.X, winB);
        }
        // pos を覚えていて全部外れても、オフセット候補を次に試す
        if (!linked && offset is { } o &&
            Fits(winL + o.X, winT + o.Y, barW, barH, vd, client))
            return (winL + o.X, winT + o.Y);

        // ここが「はみ出すときは窓の下側へ回す」の既定経路。
        // Xは client の左端を基準に仮想デスクトップへ収める
        int x = Math.Min(Math.Max(client.Left, vd.Left), vd.Right - barW);
        // 縦は窓の下に固定し、画面の下へはみ出してもよい。
        // 全部が画面の外へ出て掴めなくなる時だけハンドルが最低限（MinVisibleHandle）見える所で止める
        int minHandle = Math.Min(MinVisibleHandle, barH);
        int y = Math.Max(vd.Top, Math.Min(winB, vd.Bottom - minHandle));
        return (x, y);
    }
}
