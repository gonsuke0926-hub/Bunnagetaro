using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Bunnagetaro5.Services;

namespace Bunnagetaro5.Pages;

/// <summary>設定ページの「位置の反映（内蔵OCR）」（SettingsPage の partial class。修正J100）。
/// プリセットの「読み取った位置に並べる」を ON にした時に使う細かい値。全体で1つ・上級者モードの時だけ出す（人の判断 2026-11-25）</summary>
public partial class SettingsPage
{
    private readonly List<NumCtl> _layoutNums = new();
    private CheckBox _layoutTextBgChk = null!;

    private void BuildLayoutParamsSection()
    {
        var sec = new StackPanel();
        sec.Children.Add(Section("位置の反映（PP-DocLayoutV3）"));
        sec.Children.Add(Subtle("プリセットで「読み取った位置に並べる（PP-DocLayoutV3）」を ON にした時に使います"));

        _layoutNums.Clear();
        var stack = new StackPanel();
        // 修正J103・J104・J107: 区画（段落）の所だけに背景＝マスク（既定 ON。切ると今まで通り解答欄全体に背景）
        _layoutTextBgChk = new CheckBox { Content = "区画の所だけに背景を付ける", VerticalAlignment = VerticalAlignment.Center, Name = "LayoutTextBgChk" };
        _layoutTextBgChk.IsChecked = _cfg.Settings.LayoutTextBg;
        _layoutTextBgChk.Checked += (_, _) => { _cfg.Settings.LayoutTextBg = true; SaveSoon(); };
        _layoutTextBgChk.Unchecked += (_, _) => { _cfg.Settings.LayoutTextBg = false; SaveSoon(); };
        stack.Children.Add(new SettingRow("背景の範囲", "切ると解答欄全体に背景", _layoutTextBgChk).AsGrouped());
        for (int i = 0; i < LayoutParams.Specs.Length; i++)
        {
            var s = LayoutParams.Specs[i];
            var n = AddNum(null, s.Name, s.Key, s.Lo, s.Hi, s.IsInt, s.Def, () => _cfg.Root, null, "Layout" + i + "Box");


            _layoutNums.Add(n);
            stack.Children.Add(MakeDivider());
            stack.Children.Add(n.Row.AsGrouped());
        }

        var reset = new Button { Content = "既定に戻す", Name = "LayoutReset" };
        reset.Click += (_, _) => ResetLayoutParams();
        var d = LayoutParams.Default;
        var resetRow = new SettingRow("既定に戻す",
            $"既定: 区画の所だけ / {d.ScoreThresh:0.##} / {d.BatchChars} / {d.LabelWords} / {d.LabelChars}", reset);
        stack.Children.Add(MakeDivider());
        stack.Children.Add(resetRow.AsGrouped());

        sec.Children.Add(MakeGroupCard(stack));
        BodyPanel.Children.Add(MakeExpertWrap(sec));
    }

    private void ResetLayoutParams()
    {
        foreach (var s in LayoutParams.Specs)
            _cfg.Root[s.Key] = s.IsInt ? JsonValue.Create((int)s.Def) : JsonValue.Create(s.Def);
        foreach (var n in _layoutNums)
        {
            n.Load(_cfg.Root);
            n.Box.BorderBrush = SettingRow.NormalBorderBrush();
            n.Invalid = false;
        }
        _layoutTextBgChk.IsChecked = true;   // 修正J103
        SaveSoon();
    }
}
