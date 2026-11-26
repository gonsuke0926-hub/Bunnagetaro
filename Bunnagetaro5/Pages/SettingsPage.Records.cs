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

/// <summary>設定ページの記録（SettingsPage の partial class）。</summary>
public partial class SettingsPage
{
    // ================= 記録 =================

    private void BuildRecordSection()
    {
        BodyPanel.Children.Add(Section("記録"));

        _saveShotsChk = new CheckBox
        {
            Content = "スクショの控えを残す",
            VerticalAlignment = VerticalAlignment.Center,
            Name = "SaveShotsChk",
            IsChecked = _cfg.Settings.SaveShots,
        };
        _saveShotsChk.Checked += (_, _) => { _cfg.Settings.SaveShots = true; SaveSoon(); };
        _saveShotsChk.Unchecked += (_, _) => { _cfg.Settings.SaveShots = false; SaveSoon(); };
        var saveShotsRow = new SettingRow(
            "スクショの控え",
            "撮るたびに shots フォルダへ保存します",
            _saveShotsChk);

        _shotsBox = new TextBox { Width = 250, Text = _cfg.Settings.ShotsDir, Name = "ShotsBox" };
        _shotsBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitShotsDir(); };
        BindCommitOnFocusLoss(_shotsBox, CommitShotsDir);
        var sdBtn = new Button { Content = "参照...", Margin = new Thickness(6, 0, 0, 0), Name = "ShotsPick" };
        sdBtn.Click += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(Window.GetWindow(this)).Handle;
            string? dir = FolderPicker.Pick(hwnd, "スクショ保存フォルダ");
            if (dir is not null) { _shotsBox.Text = dir; CommitShotsDir(); }
        };
        var sdPanel = new StackPanel { Orientation = Orientation.Horizontal };
        sdPanel.Children.Add(_shotsBox);
        sdPanel.Children.Add(sdBtn);
        var shotsRow = new SettingRow("撮影記録の保存先",
            "空なら既定: " + _cfg.Paths.DefaultShotsDir, sdPanel);

        var openBtn = new Button
        {
            Content = _cfg.Paths.ConfigJson,
            MaxWidth = 420,
            Name = "OpenConfigFolder",
        };
        openBtn.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _cfg.Paths.Root + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { AppLog.Warning("settings: 設定の保存先を開けない: " + ex.Message); }
        };
        var openRow = new SettingRow("設定の保存先", "クリックするとこのフォルダを開きます", openBtn);
        BodyPanel.Children.Add(MakeGroupCard(saveShotsRow, shotsRow, openRow));
    }

    private void CommitShotsDir()
    {
        string v = _shotsBox.Text.Trim();
        if (S(_cfg.Settings.ReadNode("shots_dir")) == v) return;
        _cfg.Settings.ShotsDir = v;
        SaveSoon();
    }
}

