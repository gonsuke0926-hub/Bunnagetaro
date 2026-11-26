using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Pages;

/// <summary>設定ページのAI接続（SettingsPage の partial class）。</summary>
public partial class SettingsPage
{
    // ---------------- AI接続 ----------------

    private void BuildAiSection()
    {
        BodyPanel.Children.Add(Section("AI接続"));

        var urlRow = new SettingRow("Ollama URL", "例: http://localhost:11434", MakeUrlBox());

        var connCtl = new StackPanel { Orientation = Orientation.Horizontal };
        _testBtn = new Button { Content = "接続テスト", Margin = new Thickness(0, 0, 6, 0), Name = "ConnTestButton" };
        _fetchBtn = new Button { Content = "モデル一覧を取得", Name = "FetchModelsButton" };
        _testBtn.Click += TestConn_Click;
        _fetchBtn.Click += FetchModels_Click;
        connCtl.Children.Add(_testBtn);
        connCtl.Children.Add(_fetchBtn);
        _connRow = new SettingRow("接続", "", connCtl);
        BodyPanel.Children.Add(MakeGroupCard(urlRow, _connRow));
        BodyPanel.Children.Add(Subtle(
            "既定プリセットの想定モデル：\n" +
            "・翻訳：pfnet/plamo-3-translate（サイズ・量子化はPCに合わせて選択／GGUF）\n" +
            "・OCR・数式：glm-ocr\n" +
            "・表：PaddlePaddle/PaddleOCR-VL-1.6（GGUF）\n" +
            "※ 別のモデルを使う場合は、プリセット欄の「モデル」と「名前／指示（プロンプト）」を合わせて変更してください。"));
    }

    private sealed class UrlDeletableConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is string s && s != ConfigStore.DefaultBaseUrl ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private ComboBox MakeUrlBox()
    {
        _urlBox = new ComboBox
        {
            IsEditable = true,
            Width = 280,
            HorizontalAlignment = HorizontalAlignment.Right,
            Name = "UrlBox",
        };
        var itemStyle = new Style(typeof(ComboBoxItem));
        itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _urlBox.ItemContainerStyle = itemStyle;
        _urlBox.ItemTemplate = MakeUrlItemTemplate();

        ReloadUrlList();
        _fillingUrl = true;
        try { _urlBox.Text = _cfg.Settings.BaseUrl; }
        finally { _fillingUrl = false; }
        _urlBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitUrl(); e.Handled = true; }
        };
        BindCommitOnFocusLoss(_urlBox, CommitUrl);
        _urlBox.SelectionChanged += (_, _) =>
        {
            if (_fillingUrl) return;
            if (_urlBox.SelectedItem is string s && s.Length > 0)
            {
                _urlBox.Text = s;
                CommitUrl();
            }
        };
        return _urlBox;
    }

    private DataTemplate MakeUrlItemTemplate()
    {
        var dt = new DataTemplate(typeof(string));
        var grid = new FrameworkElementFactory(typeof(Grid));
        var col1 = new FrameworkElementFactory(typeof(ColumnDefinition));
        col1.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var col2 = new FrameworkElementFactory(typeof(ColumnDefinition));
        col2.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        grid.AppendChild(col1);
        grid.AppendChild(col2);

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.SetValue(Grid.ColumnProperty, 0);

        var del = new FrameworkElementFactory(typeof(Button));
        del.SetValue(FrameworkElement.NameProperty, "UrlRowDelButton");
        del.SetValue(ContentControl.ContentProperty, "×");
        del.SetValue(FrameworkElement.WidthProperty, 20.0);
        del.SetValue(FrameworkElement.HeightProperty, 20.0);
        del.SetValue(Control.FontSizeProperty, 11.0);
        del.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
        del.SetValue(FrameworkElement.ToolTipProperty, "履歴から削除");
        del.SetValue(FrameworkElement.StyleProperty, MakeSubtleDelButtonStyle());
        del.SetValue(Grid.ColumnProperty, 1);
        del.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding
        {
            Converter = new UrlDeletableConverter(),
        });
        del.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
            new RoutedEventHandler((s, e) =>
            {
                e.Handled = true;
                if (s is FrameworkElement fe && fe.DataContext is string u)
                    DeleteUrlFromHistory(u);
            }));

        grid.AppendChild(text);
        grid.AppendChild(del);
        dt.VisualTree = grid;
        return dt;
    }

    private static Style MakeSubtleDelButtonStyle()
    {
        var st = new Style(typeof(Button));
        st.Setters.Add(new Setter(Control.OverridesDefaultStyleProperty, true));
        st.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        st.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        st.Setters.Add(new Setter(Control.ForegroundProperty, SettingRow.SubtleBrush()));
        st.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55));
        st.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));

        var tpl = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bd.AppendChild(cp);
        tpl.VisualTree = bd;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 1.0));
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(20, 0, 0, 0))));
        tpl.Triggers.Add(hover);

        st.Setters.Add(new Setter(Control.TemplateProperty, tpl));
        return st;
    }

    internal void DeleteUrlFromHistory(string target)
    {
        string norm = ConfigStore.NormBase(target);
        if (norm == ConfigStore.DefaultBaseUrl) return;
        var list = (_cfg.Settings.BaseUrlList as JsonArray)?
            .Select(x => S(x)).Where(x => x.Trim().Length > 0).ToList() ?? new List<string>();
        if (!list.Remove(norm)) return;
        if (!list.Contains(ConfigStore.DefaultBaseUrl))
            list.Insert(0, ConfigStore.DefaultBaseUrl);

        var arr = new JsonArray();
        foreach (var u in list) arr.Add(JsonValue.Create(u));
        _cfg.Settings.BaseUrlList = arr;

        bool wasActive = _cfg.Settings.BaseUrl == norm;
        string next = wasActive ? list[0] : _cfg.Settings.BaseUrl;
        if (wasActive)
        {
            _cfg.Settings.BaseUrl = next;
            _fillingUrl = true;
            try { _urlBox.Text = next; }
            finally { _fillingUrl = false; }
        }
        ReloadUrlList();
        SaveSoon();
        if (wasActive) _ = RefreshModelsAsync(next);
    }

    private void ReloadUrlList()
    {
        var list = (_cfg.Settings.BaseUrlList as JsonArray)?
            .Select(x => S(x)).Where(x => x.Trim().Length > 0).ToList() ?? new List<string>();
        if (!list.Contains(ConfigStore.DefaultBaseUrl))
            list.Insert(0, ConfigStore.DefaultBaseUrl);
        string cur = _cfg.Settings.BaseUrl;
        if (cur.Length > 0 && !list.Contains(cur))
            list.Add(cur);

        string keep = _urlBox.Text;
        _fillingUrl = true;
        try
        {
            _urlBox.ItemsSource = list;
            _urlBox.Text = keep;
        }
        finally { _fillingUrl = false; }
    }

    private void RememberUrl(string norm)
    {
        var list = (_cfg.Settings.BaseUrlList as JsonArray)?
            .Select(x => S(x)).Where(x => x.Trim().Length > 0).ToList() ?? new List<string>();
        if (list.Contains(norm)) return;
        list.Add(norm);
        var arr = new JsonArray();
        foreach (var u in list) arr.Add(JsonValue.Create(u));
        _cfg.Settings.BaseUrlList = arr;
        ReloadUrlList();
        SaveSoon();
    }

    private bool _committingUrl;

    /// <summary>URL欄の確定（空→既定、localhost以外へ変える前に確認、壊れたURLは保存しない）。</summary>
    private void CommitUrl()
    {
        if (_committingUrl) return;
        _committingUrl = true;
        try
        {
            CommitUrlCore();
        }
        finally
        {
            _committingUrl = false;
        }
    }

    private void CommitUrlCore()
    {
        string url = _urlBox.Text.Trim();
        if (url.Length == 0) url = ConfigStore.DefaultBaseUrl;
        var norm = ConfigStore.NormBase(url);
        var host = ConfigStore.SafeHost(norm);
        var old = _cfg.Settings.BaseUrl;
        if (host is null)
        {
            ShowConnNote("URLが不正です（サーバー名・ポートが空/不正、または不正な文字を含む）。例: http://localhost:11434");
            return;
        }
        if (norm != old && host is not ("localhost" or "127.0.0.1" or "::1" or "[::1]"))
        {
            var owner = Window.GetWindow(this);
            var r = owner is null
                ? MessageBoxResult.Yes
                : MessageBox.Show(owner,
                    "AIサーバーがローカル以外（" + host + "）を向いています。\n" +
                    "スクリーンショットの内容がこのサーバーへ送られます。続けていいですか？",
                    "localhost以外のAIサーバー",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes)
            {
                _fillingUrl = true;
                try { _urlBox.Text = old; }
                finally { _fillingUrl = false; }
                ShowConnNote("URLの変更を取り消しました。URLをlocalhost（例: http://localhost:11434）にしてください");
                return;
            }
        }
        _fillingUrl = true;
        try { _urlBox.Text = norm; }
        finally { _fillingUrl = false; }
        RememberUrl(norm);
        if (norm != old)
        {
            _cfg.Settings.BaseUrl = norm;
            SaveSoon();
            _ = RefreshModelsAsync(norm);
            return;
        }
        ShowConnNote("");
    }

    private void ShowConnNote(string note) => _connRow.SetDesc(note);

    private async void TestConn_Click(object sender, RoutedEventArgs e)
    {
        _testBtn.IsEnabled = false;
        _fetchBtn.IsEnabled = false;
        ShowConnNote("接続テスト中…");
        try
        {
            var info = await OllamaClient.GetTagInfoAsync(_urlBox.Text.Trim());
            var names = new List<string>(info.Keys);
            var eff = _cfg.EffectiveConfig();
            var model = eff.TryGetPropertyValue("model", out var mn) ? ConfigStore.NodeToStr(mn) : "";
            // vision対応の判定（モデル情報を見る）
            if (!OllamaClient.ModelInstalled(model, names))
            {
                var heads = string.Join(", ",
                    names.Take(8).Select(n => n + " " + OllamaClient.FmtGb(info.TryGetValue(n, out var b) ? b : 0)));
                ShowConnNote("⚠ つながりました（モデル " + names.Count + " 個）。ただし " + model +
                             " は導入済みモデルにありません（" + (heads.Length > 0 ? heads : "なし") + "）。" +
                             "入れ方: ollama pull " + model);
            }
            else
            {
                ShowConnNote("つながりました（モデル " + names.Count + " 個）");
            }
        }
        catch (Exception ex)
        {
            ShowConnNote("❌ 接続失敗: " + ex.Message);
        }
        finally
        {
            _testBtn.IsEnabled = true;
            _fetchBtn.IsEnabled = true;
        }
    }

    private async void FetchModels_Click(object sender, RoutedEventArgs e)
        => await RefreshModelsAsync(_urlBox.Text.Trim());

    private async System.Threading.Tasks.Task RefreshModelsAsync(string url)
    {
        _connCts?.Cancel();
        _connCts?.Dispose();
        var cts = new System.Threading.CancellationTokenSource();
        _connCts = cts;

        _testBtn.IsEnabled = false;
        _fetchBtn.IsEnabled = false;
        ShowConnNote("取得中…");
        try
        {
            var info = await OllamaClient.GetTagInfoAsync(url, cts.Token);
            if (cts.IsCancellationRequested) return;
            var names = info.Keys.ToList();
            names.Sort(StringComparer.Ordinal);
            // 導入済み一覧をsettingsへ控える（同じ並びなら書かない）
            RememberModelList(names);
            ReloadModelList();
            SaveSoon();
            ShowConnNote("✅ 導入済み " + names.Count + "件 / " + OllamaClient.SizesLine(info, names));
            _setStatus("モデル一覧を更新（" + names.Count + "件）");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                ShowConnNote("取得失敗: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_connCts, cts))
            {
                _testBtn.IsEnabled = true;
                _fetchBtn.IsEnabled = true;
            }
        }
    }

    /// <summary>モデル欄の候補: 導入済み Ollama モデル一覧（model_list）。
    /// 内蔵の OCR は「ぶん投げ方」へ移したため AI モデル一覧には混ぜない</summary>
    private List<string> ModelChoices() =>
        (_cfg.Settings.ModelList as JsonArray)?
            .Select(x => S(x)).Where(x => x.Trim() != "" && !BuiltinOcr.IsBuiltin(S(x))).ToList() ?? new List<string>();

    /// <summary>設定ページの生成欄のモデル候補を、いま保存してある model_list で入れ直す
    /// （起動時の一覧読みが終わった時など）。欄の書きかけの文字は変えない</summary>
    public void ReloadModelList()
    {
        var ml = ModelChoices();
        string keep = _modelCb.Text;
        string keepVlm = _ocrVlmModelCb.Text;
        bool wasFilling = _filling;
        _filling = true;
        try
        {
            _modelCb.ItemsSource = ml;
            _modelCb.Text = keep;
            _ocrVlmModelCb.ItemsSource = ml;
            _ocrVlmModelCb.Text = keepVlm;
        }
        finally { _filling = wasFilling; }
    }

    /// <summary>導入済み一覧を model_list に控える。同じ並びなら書かない。変えたら true</summary>
    public bool RememberModelList(List<string> names)
    {
        if (_cfg.Settings.ModelList is JsonArray cur)
        {
            bool same = cur.Count == names.Count;
            if (same)
            {
                for (int i = 0; i < names.Count; i++)
                {
                    if (cur[i] is not null && cur[i]!.GetValueKind() == JsonValueKind.String &&
                        cur[i]!.GetValue<string>() == names[i]) continue;
                    same = false;
                    break;
                }
            }
            if (same) return false;
        }
        var arr = new JsonArray();
        foreach (var n in names) arr.Add(JsonValue.Create(n));
        _cfg.Settings.ModelList = arr;
        return true;
    }
}

