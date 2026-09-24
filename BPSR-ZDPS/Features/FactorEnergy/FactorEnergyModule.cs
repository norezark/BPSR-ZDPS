using System.Text.Json;
using BPSR_ZDPSLib;
using BPSR_ZDPSLib.ServiceMethods;
using Serilog;

namespace BPSR_ZDPS.Features.FactorEnergy;

public sealed class FactorEnergyOptions
{
    public bool Enabled { get; set; } = true;
    public bool ShowWindow { get; set; } = true;
    public bool TopMost { get; set; } = true;
    public float Opacity { get; set; } = 0.9f;
    public float TextScale { get; set; } = 1f;
}

public sealed record FactorView(bool Capturing, bool Enabled, bool HasSnapshot, int Season, long LocalUuid,
    string? Error, EnergyRow[] Rows, SourceTemplate[] Sources, int[] UnknownItems, long Notifications, long SkillRequests);

public static class FactorEnergyModule
{
    static readonly object Gate = new();
    static readonly string OptionsPath = Path.Combine(AppContext.BaseDirectory, "FactorEnergy.settings.json");
    public static FactorEnergyOptions Options { get; } = LoadOptions();
    static NetCap? capture;
    static FactorProtocol? protocol;
    static System.Threading.Timer? timer;
    static string? loadError;

    static FactorEnergyOptions LoadOptions()
    {
        try
        {
            if (File.Exists(OptionsPath)) return JsonSerializer.Deserialize<FactorEnergyOptions>(File.ReadAllText(OptionsPath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { Log.Warning(ex, "Cannot load illusion energy settings; using defaults"); }
        return new();
    }

    public static void SaveOptions()
    {
        try
        {
            File.WriteAllText(OptionsPath + ".tmp", JsonSerializer.Serialize(Options, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(OptionsPath + ".tmp", OptionsPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Log.Warning(ex, "Cannot save illusion energy settings"); }
    }

    public static void Attach(NetCap netCap)
    {
        lock (Gate)
        {
            Detach();
            try
            {
                var catalog = new FactorCatalog(Path.Combine(AppContext.BaseDirectory, "Data", "FactorEnergy"));
                protocol = new FactorProtocol(catalog, (id, level) =>
                    HelperMethods.DataTables.SkillFightLevels.Data.TryGetValue(((long)id * 100 + level).ToString(), out var skill)
                        ? skill.SkillEffectId : null);
                protocol.Reset(Environment.TickCount64);
                loadError = null;
                capture = netCap;
                capture.NotifyObserved += Notify;
                capture.ProxyObserved += Proxy;
                timer = new System.Threading.Timer(_ => Tick(), null, 50, 50);
                Log.Information("Illusion energy tracking attached ({Sources} source templates, {Slots} output templates)", catalog.Sources.Length, catalog.Slots.Length);
            }
            catch (Exception ex)
            {
                loadError = ex.Message;
                Log.Error(ex, "Cannot initialize illusion energy tracking");
            }
        }
    }

    public static void Detach()
    {
        lock (Gate)
        {
            timer?.Dispose(); timer = null;
            if (capture != null)
            {
                capture.NotifyObserved -= Notify;
                capture.ProxyObserved -= Proxy;
            }
            capture = null;
            protocol?.Reset(Environment.TickCount64);
        }
    }

    public static void SetEnabled(bool value)
    {
        lock (Gate)
        {
            Options.Enabled = value;
            protocol?.Reset(Environment.TickCount64);
        }
        SaveOptions();
    }

    static void Failed(Exception ex)
    {
        // A bad packet must not break DPS processing or leave believable but stale counters on screen.
        protocol?.Invalidate(ex.Message, Environment.TickCount64);
        Log.Warning(ex, "Illusion energy tracking needs a new character snapshot");
    }

    static void Notify(NotifyId id, ReadOnlySpan<byte> bytes, ExtraPacketData _)
    {
        if (id.ServiceId != (ulong)EServiceId.WorldNtf) return;
        lock (Gate)
        {
            if (!Options.Enabled || protocol == null) return;
            try { protocol.Notify((WorldNtf)id.MethodId, bytes, Environment.TickCount64); }
            catch (Exception ex) { Failed(ex); }
        }
    }

    static void Proxy(ProxyId id, ReadOnlySpan<byte> bytes, ExtraPacketData _)
    {
        if (id.ServiceId != (uint)EProxyServiceId.World || id.MethodId != (uint)WorldProxy.UseSlot) return;
        lock (Gate)
        {
            if (!Options.Enabled || protocol == null) return;
            try { protocol.UseSlot(bytes, Environment.TickCount64); }
            catch (Exception ex) { Failed(ex); }
        }
    }

    static void Tick()
    {
        lock (Gate)
        {
            if (!Options.Enabled || capture == null || protocol == null) return;
            try { protocol.Engine.Advance(Environment.TickCount64); }
            catch (Exception ex) { Failed(ex); }
        }
    }

    public static FactorView Snapshot()
    {
        lock (Gate)
        {
            return new(capture != null, Options.Enabled, protocol?.HasSnapshot ?? false, protocol?.SeasonId ?? 0,
                protocol?.LocalUuid ?? 0, loadError ?? protocol?.Error, protocol?.Engine.Snapshot(Environment.TickCount64) ?? [],
                protocol?.Engine.Selection?.Sources ?? [], protocol?.Engine.Selection?.UnknownItems ?? [],
                protocol?.Notifications ?? 0, protocol?.SkillRequests ?? 0);
        }
    }
}
