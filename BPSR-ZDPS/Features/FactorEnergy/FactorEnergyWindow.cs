using System.Numerics;
using Hexa.NET.ImGui;

namespace BPSR_ZDPS.Features.FactorEnergy;

public static class FactorEnergyWindow
{
    static readonly ImGuiWindowClassPtr WindowClass = ImGui.ImGuiWindowClass();
    static readonly Vector4 Amber = new(1f, 0.77f, 0.3f, 1f);
    static readonly Vector4 Green = new(0.4f, 0.9f, 0.7f, 1f);

    // Factor descriptions may contain percent signs; never pass them as native printf format strings.
    static void Wrapped(string text)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    static void Colored(Vector4 color, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    public static void Open()
    {
        FactorEnergyModule.Options.ShowWindow = true;
        FactorEnergyModule.SaveOptions();
    }

    public static void Draw()
    {
        var options = FactorEnergyModule.Options;
        if (!options.ShowWindow) return;
        WindowClass.ClassId = ImGuiP.ImHashStr("FactorEnergyWindowClass");
        WindowClass.ViewportFlagsOverrideSet = ImGuiViewportFlags.NoTaskBarIcon | (options.TopMost ? ImGuiViewportFlags.TopMost : 0);
        WindowClass.ViewportFlagsOverrideClear = options.TopMost ? 0 : ImGuiViewportFlags.TopMost;
        ImGui.SetNextWindowClass(WindowClass);
        ImGui.SetNextWindowSize(new Vector2(440, 300), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowBgAlpha(Math.Clamp(options.Opacity, 0.2f, 1f));
        bool open = options.ShowWindow;
        bool visible = ImGui.Begin("Illusion Energy / 虚妄エネルギー###FactorEnergy", ref open, ImGuiWindowFlags.NoDocking);
        if (visible)
        {
            ImGui.PushFont(null, ImGui.GetFontSize() * Math.Clamp(options.TextScale, 0.75f, 2f));
            var state = FactorEnergyModule.Snapshot();
            if (!state.Enabled) Wrapped("計測は無効です。下の設定から有効にできます。");
            else if (state.Error != null)
            {
                Colored(Amber, "同期エラー / 再同期待ち");
                Wrapped("ゲームに再ログインしてください。ZDPSのDPS計測は継続します。");
                Wrapped(state.Error);
            }
            else if (!state.Capturing) Wrapped("ZDPSのキャプチャ開始を待っています。");
            else if (!state.HasSnapshot)
                Wrapped("キャラクター情報の同期待ち。ZDPSを起動した状態でゲームに再ログインしてください。");
            else if (state.Season >= 4)
                Wrapped($"シーズン {state.Season} の因子方式は未対応です。この定義はS1〜S3用です。");
            else if (state.Rows.Length == 0)
                Wrapped("対応する装着中の出力因子がありません。ゲーム内の虚妄因子構成を確認してください。");
            else
            {
                if (state.Sources.Length == 0) Colored(Amber, "対応するエネルギー獲得因子がありません。");
                foreach (var row in state.Rows)
                {
                    ImGui.PushID(row.ItemId);
                    Wrapped(row.Name);
                    string count = $"{row.Count:N0} / {row.Threshold?.ToString("N0") ?? "?"}";
                    if (!row.Calibrated) count = "推定 " + count;
                    float fraction = row.Threshold is > 0 ? Math.Min(1f, (float)row.Count / row.Threshold.Value) : 0f;
                    ImGui.ProgressBar(fraction, new Vector2(-1, 22), count);
                    if (!row.Counting)
                        Colored(Amber, row.FreezeRemainingMs is { } remaining ? $"加算停止  {remaining / 1000.0:F1}s" : "加算停止 / バフ終了待ち");
                    else if (row.FreezeRemainingMs is { } until)
                        Colored(Green, $"効果中 / 加算継続  {until / 1000.0:F1}s");
                    ImGui.PopID();
                    ImGui.Spacing();
                }
                if (state.Rows.Any(x => !x.Calibrated))
                    Wrapped("途中から計測した値は推定です。因子のリセットイベントを検出すると、その因子の基準が揃います。");
            }
            if (state.UnknownItems.Length > 0)
            {
                Colored(Amber, "未対応の装着アイテムがあります");
                Wrapped(string.Join(", ", state.UnknownItems));
            }
            if (ImGui.CollapsingHeader("獲得因子・受信状況"))
            {
                foreach (var source in state.Sources) Wrapped(source.Name);
                ImGui.Text($"Season: {state.Season}  UID: {state.LocalUuid >> 16}");
                ImGui.Text($"Notifications: {state.Notifications}  Skill requests: {state.SkillRequests}");
                Wrapped("通信イベントからの推定値です。ゲーム画面と差がある場合は因子構成とこの受信状況を確認してください。");
            }
            if (ImGui.CollapsingHeader("設定"))
            {
                bool enabled = options.Enabled;
                if (ImGui.Checkbox("エネルギーを計測", ref enabled)) FactorEnergyModule.SetEnabled(enabled);
                bool topMost = options.TopMost;
                if (ImGui.Checkbox("最前面に表示", ref topMost)) { options.TopMost = topMost; FactorEnergyModule.SaveOptions(); }
                float opacity = options.Opacity;
                if (ImGui.SliderFloat("背景の濃さ", ref opacity, 0.2f, 1f)) { options.Opacity = opacity; FactorEnergyModule.SaveOptions(); }
                float scale = options.TextScale;
                if (ImGui.SliderFloat("文字サイズ", ref scale, 0.75f, 2f)) { options.TextScale = scale; FactorEnergyModule.SaveOptions(); }
            }
            ImGui.PopFont();
        }
        ImGui.End();
        if (open != options.ShowWindow) { options.ShowWindow = open; FactorEnergyModule.SaveOptions(); }
    }
}
