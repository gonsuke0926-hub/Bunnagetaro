using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Bunnagetaro5.Services;

/// <summary>
/// アプリのアイコン（FIX_ROUND1 A6）。Assets\app.ico をリソースとして取り込んであり、
/// メイン窓・ダイアログ・画像の原寸表示の Icon に設定する（タスクバーと Alt+Tab に出る）。
/// exe のファイルアイコンは csproj の ApplicationIcon で付く。
/// </summary>
public static class AppIcon
{
    private static ImageSource? _png256;

    /// <summary>窓の Icon に入れる。ico の中で一番大きい絵（256px）を使い、小さい所は OS が縮める。</summary>
    public static ImageSource Window()
    {
        var dec = new IconBitmapDecoder(
            new Uri("pack://application:,,,/Bunnagetaro;component/Assets/app.ico"),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        // 一番大きいフレーム（256px）。WPFは窓の Icon が複数サイズを持つ BitmapFrame なら
        // 場面ごとに合うものを選ぶので、フレーム集合ごと渡す
        // 毎回作る（窓は数個だけ。デコーダ由来のフレームは Freeze できず、窓を作るスレッドで使うのが安全）
        return dec.Frames.OrderByDescending(f => f.PixelWidth).First();
    }

    /// <summary>バージョン情報などに大きく出す絵（256px の PNG）</summary>
    public static ImageSource Png256()
    {
        if (_png256 is not null) return _png256;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri("pack://application:,,,/Bunnagetaro;component/Assets/app_256.png");
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        _png256 = bmp;
        return bmp;
    }
}
