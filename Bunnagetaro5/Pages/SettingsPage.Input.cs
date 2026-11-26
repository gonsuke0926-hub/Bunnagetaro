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

/// <summary>設定ページのクイックぶん投げ・ホットキー・入力の登録（SettingsPage の partial class）。</summary>
public partial class SettingsPage
{
    // ================= クイックぶん投げ =================

    private void BuildQuickSection()
    {
        BodyPanel.Children.Add(Section("クイックぶん投げ"));

        _quickOnChk = new CheckBox { Content = "クイックぶん投げを使う", VerticalAlignment = VerticalAlignment.Center, Name = "QuickOnChk" };
        _quickOnChk.IsChecked = _cfg.Settings.QuickEnabled;
        _quickOnChk.Checked += (_, _) => { _cfg.Settings.QuickEnabled = true; NotifyInputChanged(); SaveSoon(); };
        _quickOnChk.Unchecked += (_, _) => { _cfg.Settings.QuickEnabled = false; NotifyInputChanged(); SaveSoon(); };
        var quickOnRow = new SettingRow(
            "使う",
            "長押しドラッグで範囲指定して即ぶん投げ（選んだボタンは他アプリに届きません）",
            _quickOnChk);

        _quickBtnCb = new ComboBox { Width = 210, Name = "QuickBtnCombo" };
        _quickBtnCb.ItemsSource = QuickButtonLabels.Select(x => x.Lab).ToList();
        string qb = _cfg.Settings.QuickButton;
        int qi = Array.FindIndex(QuickButtonLabels, x => x.Val == qb);
        _quickBtnCb.SelectedIndex = qi < 0 ? 0 : qi;   // 一覧に無い値は既定へ
        _quickBtnCb.SelectionChanged += (_, _) =>
        {
            int i = _quickBtnCb.SelectedIndex;
            if (i < 0 || i >= QuickButtonLabels.Length) return;
            if (S(_cfg.Settings.ReadNode("quick_button")) == QuickButtonLabels[i].Val) return;
            _cfg.Settings.QuickButton = QuickButtonLabels[i].Val;
            NotifyInputChanged();   // 割り当て直すボタンはこの場でフックへ載せ替える
            SaveSoon();
        };
        var quickBtnRow = new SettingRow("起動するボタン", "", _quickBtnCb);

        _quickKeyBox = new TextBox { Width = 150, IsReadOnly = true, Text = Binding.Label(_cfg.Settings.QuickKey), Name = "QuickKeyBox" };
        var kqReg = new Button { Content = "登録", Margin = new Thickness(6, 0, 0, 0), Name = "QuickKeyReg" };
        kqReg.Click += (_, _) => RegRequest?.Invoke("kquick");
        var kqDel = new Button { Content = "解除", Margin = new Thickness(6, 0, 0, 0), Name = "QuickKeyDel" };
        kqDel.Click += (_, _) => ClearRequest?.Invoke("kquick");   // 本体側で消す（Applyでフックも即外れる）
        var kqPanel = new StackPanel { Orientation = Orientation.Horizontal };
        kqPanel.Children.Add(_quickKeyBox);
        kqPanel.Children.Add(kqReg);
        kqPanel.Children.Add(kqDel);
        var kqRow = new SettingRow("キーで開始", "キー長押しドラッグで範囲指定", kqPanel);
        _quickKeyNotice = MakeBindNotice("QuickKeyNotice");
        kqRow.SetBottom(_quickKeyNotice);

        _quickNums.Clear();
        // 最小幅・最小高さもQuickApplyで読み直す値なので、変わった場で本体へ知らせる（A1）
        var qW = AddNum(null, "四角の最小幅（px）", "quick_min_w", 30, 4000, true, 30,
            () => _cfg.Root, () => NotifyInputChanged(), "QuickWBox");
        var qH = AddNum(null, "四角の最小高さ（px）", "quick_min_h", 30, 4000, true, 30,
            () => _cfg.Root, () => NotifyInputChanged(), "QuickHBox");
        _quickNums.Add(qW);
        _quickNums.Add(qH);

        var qExp = new StackPanel();
        qExp.Children.Add(kqRow.AsGrouped());
        qExp.Children.Add(MakeDivider());
        qExp.Children.Add(qW.Row.AsGrouped());
        qExp.Children.Add(MakeDivider());
        qExp.Children.Add(qH.Row.AsGrouped());

        BodyPanel.Children.Add(MakeGroupCard(quickOnRow, quickBtnRow, MakeExpertWrap(qExp, withTopDivider: true)));

        var qNotes = new StackPanel();
        qNotes.Children.Add(DangerNote(
            "ONのあいだ、起動に使う入力は他のアプリには渡りません（ホイールクリックの自動スクロール、戻る/進む、そのキーの通常の動作が使えなくなります）"));
        qNotes.Children.Add(Subtle(
            "四角が最小幅・最小高さ未満のまま離すと実行せず、窓は元の位置・サイズへ戻ります（既定30x30）\n窓が未開でも使えます"));
        BodyPanel.Children.Add(MakeExpertWrap(qNotes));
    }

    // ================= ホットキー =================

    private void BuildHotkeySection()
    {
        BodyPanel.Children.Add(Section("ホットキー"));

        _hotkeyBox = new TextBox { Width = 150, IsReadOnly = true, Text = Binding.Label(_cfg.Settings.Hotkey), Name = "HotkeyBox" };
        var hkReg = new Button { Content = "登録", Margin = new Thickness(6, 0, 0, 0), Name = "HotkeyReg" };
        hkReg.Click += (_, _) => RegRequest?.Invoke("hot");
        var hkDel = new Button { Content = "解除", Margin = new Thickness(6, 0, 0, 0), Name = "HotkeyDel" };
        hkDel.Click += (_, _) => ClearRequest?.Invoke("hot");   // 本体側で消す（Applyで発火待ちも即外れる）
        var hkPanel = new StackPanel { Orientation = Orientation.Horizontal };
        hkPanel.Children.Add(_hotkeyBox);
        hkPanel.Children.Add(hkReg);
        hkPanel.Children.Add(hkDel);
        var hkRow = new SettingRow("ぶん投げホットキー", "開いている枠をその場でぶん投げ", hkPanel);
        _hotkeyNotice = MakeBindNotice("HotkeyNotice");
        hkRow.SetBottom(_hotkeyNotice);
        // 修正J58: 範囲ドラッグのキー（ぶん投げホットキーのすぐ下。同じ「登録」「解除」・同じ表示）
        _snipKeyBox = new TextBox { Width = 150, IsReadOnly = true, Text = Binding.Label(_cfg.Settings.SnipHotkey), Name = "SnipKeyBox" };
        var skReg = new Button { Content = "登録", Margin = new Thickness(6, 0, 0, 0), Name = "SnipKeyReg" };
        skReg.Click += (_, _) => RegRequest?.Invoke("snip");
        var skDel = new Button { Content = "解除", Margin = new Thickness(6, 0, 0, 0), Name = "SnipKeyDel" };
        skDel.Click += (_, _) => ClearRequest?.Invoke("snip");
        var skPanel = new StackPanel { Orientation = Orientation.Horizontal };
        skPanel.Children.Add(_snipKeyBox);
        skPanel.Children.Add(skReg);
        skPanel.Children.Add(skDel);
        var skRow = new SettingRow("範囲ドラッグのキー", "押すと画面が少し暗くなり、ドラッグで選んだ範囲をぶん投げ", skPanel);
        _snipKeyNotice = MakeBindNotice("SnipKeyNotice");
        skRow.SetBottom(_snipKeyNotice);
        BodyPanel.Children.Add(MakeGroupCard(hkRow, skRow));
        BodyPanel.Children.Add(Subtle(
            "設定したキー・ボタンはこのアプリが使うので、他のアプリには渡りません\nよく打つキーを単独で当てると打鍵を取ってしまいます"));
    }

    // ================= 入力の「登録」（手順24 / 6.4） =================
    //
    // 欄は読み取り専用で、ページ自体はボタン押下を本体へそのまま流す。実際に
    // 入力を掴むのはMainWindow（低レベルフックで掴まないと他のアプリへ漏れる）。
    // 欄の待ち受け文言や反映後の表示は MainWindow 側が SetBindField/SyncBindFields
    // で書いてくる（Python版の _bind_field/_sync_bind_fields 相当）。

    /// <summary>「登録」の押下（"kquick" / "hot" / "snip"）→ MainWindow.BindStart</summary>
    internal Action<string>? RegRequest;
    /// <summary>「解除」の押下 → MainWindow.BindClear（cfg・フック・欄をまとめて消す）</summary>
    internal Action<string>? ClearRequest;

    /// <summary>登録待ちのあいだの欄文言（「登録するキーを押してください」等）</summary>
    public void SetBindField(string which, string text)
    {
        if (which == "kquick")
        {
            if (_quickKeyBox.Text != text) _quickKeyBox.Text = text;
        }
        else if (which == "hot")
        {
            if (_hotkeyBox.Text != text) _hotkeyBox.Text = text;
        }
        else if (which == "snip")
        {
            if (_snipKeyBox.Text != text) _snipKeyBox.Text = text;
        }
    }

    /// <summary>割り当て欄をいまの設定表示（binding_label 相当）へ戻す</summary>
    public void SyncBindFields(string kkeyLabel, string hotkeyLabel, string snipLabel)
    {
        if (_quickKeyBox.Text != kkeyLabel) _quickKeyBox.Text = kkeyLabel;
        if (_hotkeyBox.Text != hotkeyLabel) _hotkeyBox.Text = hotkeyLabel;
        if (_snipKeyBox.Text != snipLabel) _snipKeyBox.Text = snipLabel;
    }

    /// <summary>却下理由を登録欄のすぐ下へ1行の注意色で出す（空文字または which="" で消す）。</summary>
    public void SetBindNotice(string which, string text)
    {
        if (which is "" or "kquick" && _quickKeyNotice is not null)
        {
            _quickKeyNotice.Text = text;
            _quickKeyNotice.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
        if (which is "" or "hot" && _hotkeyNotice is not null)
        {
            _hotkeyNotice.Text = text;
            _hotkeyNotice.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
        if (which is "" or "snip" && _snipKeyNotice is not null)
        {
            _snipKeyNotice.Text = text;
            _snipKeyNotice.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private static TextBlock MakeBindNotice(string name) => new()
    {
        Name = name,
        FontSize = 12,
        Foreground = SettingRow.WarningBrush(),
        TextWrapping = TextWrapping.Wrap,
        Visibility = Visibility.Collapsed,
    };
}

