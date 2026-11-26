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

/// <summary>設定ページの自動トリガ（SettingsPage の partial class）。</summary>
public partial class SettingsPage
{
    // ================= 自動トリガ =================

    private void BuildTrigSection()
    {
        BodyPanel.Children.Add(Section("自動トリガ"));

        _modeChk = new CheckBox { Content = "自動トリガ", VerticalAlignment = VerticalAlignment.Center, Name = "ModeChk" };
        _modeChk.IsChecked = _cfg.Settings.Mode == "auto";
        _modeChk.Checked += (_, _) => { if (_filling) return; WriteSelf(() => _cfg.Settings.Mode = "auto"); SaveSoon(); };
        _modeChk.Unchecked += (_, _) => { if (_filling) return; WriteSelf(() => _cfg.Settings.Mode = "manual"); SaveSoon(); };
        var trigOnRow = new SettingRow("自動トリガ", "画面の変化が止まったら自動でぶん投げます", _modeChk);

        _trigNums.Clear();
        var t1 = AddNum(null, "画面をのぞく間隔（秒）", "trig_poll_interval", 0.2, 10.0, false, TrigParams.DefaultInterval, () => _cfg.Root, null);
        var t2 = AddNum(null, "動いたと見る変化の割合", "trig_change_ratio", 0.0, 1.0, false, 0.01, () => _cfg.Root, null);
        var t3 = AddNum(null, "静止だと見る変化の割合", "trig_stable_ratio", 0.0, 0.5, false, 0.005, () => _cfg.Root, null);
        var t4 = AddNum(null, "静止が続く回数", "trig_stable_samples", 1, 100, true, 2, () => _cfg.Root, null);
        var t5 = AddNum(null, "自動トリガの最短間隔（秒）", "trig_cooldown", 0.0, 60.0, false, TrigParams.DefaultCooldown, () => _cfg.Root, null);
        _trigNums.AddRange([t1, t2, t3, t4, t5]);

        var expStack = new StackPanel();
        expStack.Children.Add(t1.Row.AsGrouped());
        expStack.Children.Add(MakeDivider());
        expStack.Children.Add(t2.Row.AsGrouped());
        expStack.Children.Add(MakeDivider());
        expStack.Children.Add(t3.Row.AsGrouped());
        expStack.Children.Add(MakeDivider());
        expStack.Children.Add(t4.Row.AsGrouped());
        expStack.Children.Add(MakeDivider());
        expStack.Children.Add(t5.Row.AsGrouped());

        BodyPanel.Children.Add(MakeGroupCard(trigOnRow, MakeExpertWrap(expStack, withTopDivider: true)));
        BodyPanel.Children.Add(MakeExpertWrap(Subtle("動き・静止の判定しきい値")));
    }
}

