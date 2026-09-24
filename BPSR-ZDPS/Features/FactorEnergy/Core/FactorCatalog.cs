using System.Text.Json;

namespace BPSR_ZDPS.Features.FactorEnergy;

public sealed record SourceConfig
{
    public string Kind { get; init; } = "";
    public int[] SkillBaseIds { get; init; } = [];
    public int SkillBaseId { get; init; }
    public long[] SkillKeys { get; init; } = [];
    public int BuffId { get; init; }
    public int? SourceConfigId { get; init; }
    public int ResourceId { get; init; }
    public int AttrId { get; init; }
    public uint Increment { get; init; }
    public uint UnitsRequired { get; init; } = 1;
    public uint HitsRequired { get; init; } = 1;
    public int? RequiredTypeFlags { get; init; }
    public long TickIntervalMs { get; init; }
    public double MetersRequired { get; init; }
    public AttributeCondition? AttrCondition { get; init; }
}

public sealed record AttributeCondition(int AttrId, long RequiredValue);
public sealed record SlotConfig
{
    public uint? Threshold { get; init; }
    public int ResetBuffId { get; init; }
    public int? ResetSourceConfigId { get; init; }
    public string OnBuffAdd { get; init; } = "noOp";
    public string OnBuffChange { get; init; } = "noOp";
    public string OnBuffRemove { get; init; } = "noOp";
    public long? FreezeDurationMs { get; init; }
    public string OnFreezeExpire { get; init; } = "resetAndStartCount";
}

public sealed record SourceTemplate(string Id, string Name, int[] ItemIds, SourceConfig[] Sources);
public sealed record SlotTemplate(string Id, string Name, int[] ItemIds, SlotConfig Slot);
public sealed record SelectedSlot(int ItemId, string Name, SlotConfig Config);
public sealed record FactorSelection(int[] Items, SourceTemplate[] Sources, SelectedSlot[] Slots, int[] UnknownItems)
{
    // Grade changes affect thresholds; an unchanged dirty update must not reset counters.
    public string Signature => string.Join(",", Items.Except(UnknownItems));
}

public sealed class FactorCatalog
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    static readonly HashSet<string> Kinds = ["skillCast", "skillDurationTick", "damageBySkillKey",
        "damageBySkillKeyOnce", "damageTaken", "fightResourceSpent", "buffAdded", "buffLayerSpent",
        "buffDurationTick", "movementDistance"];
    static readonly HashSet<string> Actions = ["reset", "freeze", "resetAndFreeze", "resetAndFreezeKeepCounting",
        "resetAndStartCount", "startCount", "noOp"];
    public SourceTemplate[] Sources { get; }
    public SlotTemplate[] Slots { get; }
    public IReadOnlyDictionary<int, uint> Costs { get; }

    public FactorCatalog(string directory)
    {
        Dictionary<string, string> Names(string file, string key)
        {
            var path = Path.Combine(directory, "ja-JP", file);
            if (!File.Exists(path)) return [];
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.EnumerateArray().ToDictionary(x => x.GetProperty(key).GetString()!, x => x.GetProperty("name").GetString()!);
        }
        var sourceNames = Names("counter_source_templates.json", "sourceId");
        var slotNames = Names("counter_slot_templates.json", "slotTemplateId");
        using var sources = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "counter_source_templates.json")));
        Sources = sources.RootElement.EnumerateArray().Select(x =>
        {
            var id = x.GetProperty("sourceId").GetString()!;
            var raw = x.GetProperty("source");
            var entries = raw.ValueKind == JsonValueKind.Array ? raw.EnumerateArray().ToArray() : [raw];
            var configs = entries.Select(entry =>
            {
                var p = entry.EnumerateObject().Single();
                if (!Kinds.Contains(p.Name)) throw new InvalidDataException($"Unsupported factor source: {p.Name}");
                var config = p.Value.Deserialize<SourceConfig>(Json)! with { Kind = p.Name };
                if (config.UnitsRequired == 0 || config.HitsRequired == 0 ||
                    (p.Name.EndsWith("Tick") && config.TickIntervalMs <= 0) ||
                    (p.Name == "movementDistance" && config.MetersRequired <= 0))
                    throw new InvalidDataException($"Invalid source configuration: {id}");
                return config;
            }).ToArray();
            return new SourceTemplate(id, sourceNames.GetValueOrDefault(id, x.GetProperty("name").GetString()!),
                x.GetProperty("itemIds").Deserialize<int[]>()!, configs);
        }).ToArray();
        using var slots = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "counter_slot_templates.json")));
        Slots = slots.RootElement.EnumerateArray().Select(x =>
        {
            var id = x.GetProperty("slotTemplateId").GetString()!;
            var slot = x.GetProperty("slot").Deserialize<SlotConfig>(Json)!;
            if (!new[] { slot.OnBuffAdd, slot.OnBuffChange, slot.OnBuffRemove, slot.OnFreezeExpire }.All(Actions.Contains))
                throw new InvalidDataException($"Invalid slot actions: {id}");
            return new SlotTemplate(id, slotNames.GetValueOrDefault(id, x.GetProperty("name").GetString()!),
                x.GetProperty("itemIds").Deserialize<int[]>()!, slot);
        }).ToArray();
        Costs = JsonSerializer.Deserialize<Dictionary<int, uint>>(File.ReadAllText(Path.Combine(directory, "season_cultivate_factor_costs.json")))!;
        if (Slots.SelectMany(x => x.ItemIds).Any(id => !Costs.TryGetValue(id, out var cost) || cost == 0))
            throw new InvalidDataException("Missing factor energy cost.");
    }

    public FactorSelection Select(IEnumerable<int> itemIds)
    {
        var items = itemIds.Where(x => x > 0).Distinct().Order().ToArray();
        var sources = Sources.Where(x => x.ItemIds.Intersect(items).Any()).ToArray();
        var slots = items.Select(id => (id, template: Slots.FirstOrDefault(x => x.ItemIds.Contains(id))))
            .Where(x => x.template != null).Select(x => new SelectedSlot(x.id, x.template!.Name,
                x.template.Slot with { Threshold = Costs[x.id] })).ToArray();
        var known = sources.SelectMany(x => x.ItemIds).Concat(slots.Select(x => x.ItemId)).ToHashSet();
        return new(items, sources, slots, items.Where(x => !known.Contains(x)).ToArray());
    }
}
