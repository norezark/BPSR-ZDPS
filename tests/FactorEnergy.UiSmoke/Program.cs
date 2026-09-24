using System.Numerics;
using System.Reflection;
using BPSR_ZDPS.Features.FactorEnergy;
using BPSR_ZDPSLib;
using Google.Protobuf;
using Hexa.NET.ImGui;
using Zproto;
using WorldNtf = BPSR_ZDPSLib.ServiceMethods.WorldNtf;

// Exercises the real ImGui window without opening a desktop window or starting network capture.
var context = ImGui.CreateContext();
ImGui.SetCurrentContext(context);
var io = ImGui.GetIO();
io.DisplaySize = new(1024, 768);
io.DeltaTime = 1f / 60;
io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
unsafe { io.IniFilename = null; }
typeof(FactorEnergyModule).Assembly.GetType("BPSR_ZDPS.Program")!
    .GetMethod("LoadFonts", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);

var catalog = new FactorCatalog(Path.Combine(AppContext.BaseDirectory, "Data", "FactorEnergy"));
var protocol = new FactorProtocol(catalog, (_, _) => null);
var flags = BindingFlags.NonPublic | BindingFlags.Static;
typeof(FactorEnergyModule).GetField("protocol", flags)!.SetValue(null, protocol);
// Creating NetCap initializes its parser only. Start() is deliberately never called.
typeof(FactorEnergyModule).GetField("capture", flags)!.SetValue(null, new NetCap());

void State(int seasonId, params int[] items)
{
    var area = new CultivateAreaData { IsActive = true };
    for (int i = 0; i < items.Length; i++) area.CultivateMiddleNodeMap[i] = new() { ItemId = items[i] };
    var subtype = new CultivateLineSubTypeData(); subtype.CultivateLineDataMap[1] = area;
    var line = new CultivateLineData(); line.CultivateLineMap[800522] = subtype;
    var season = new SeasonCultivateLineData(); season.SeasonCultivateLineMap[seasonId] = line;
    protocol.Notify(WorldNtf.SyncContainerData, new WorldNtfCsharp.Types.SyncContainerData
        { VData = new CharSerialize { CharId = 42, SeasonCultivateLineData = season } }.ToByteArray(), Environment.TickCount64);
}

int scenarios = 0;
void Draw(string name, bool visible = true)
{
    FactorEnergyModule.Options.ShowWindow = visible;
    for (int i = 0; i < 3; i++)
    {
        ImGui.NewFrame();
        FactorEnergyWindow.Draw();
        ImGui.Render();
        var draw = ImGui.GetDrawData();
        if (i == 2 && (draw.TotalVtxCount > 0) != visible) throw new Exception($"Unexpected UI geometry: {name}");
    }
    scenarios++;
    Console.WriteLine($"PASS UI {name}");
}

Draw("waiting for snapshot");
State(3, 20020001, 20021731, 20021771);
protocol.Engine.SkillStarted(1714, Environment.TickCount64);
Draw("multiple factors and Japanese descriptions");
protocol.Engine.Buff(BuffEdge.Add, new(1, 3050401), Environment.TickCount64);
Draw("frozen factor");
State(3, 20020001, 20021731, 999999);
Draw("unknown factor");
State(4, 20020001, 20021731);
Draw("unsupported season");
protocol.Invalidate("Synthetic sync error: 100% test", Environment.TickCount64);
Draw("sync error containing percent sign");
FactorEnergyModule.Options.Enabled = false;
Draw("disabled");
Draw("closed", false);
ImGui.DestroyContext(context);
Console.WriteLine($"{scenarios} UI scenarios passed (headless, no capture)");
