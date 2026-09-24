using System.Numerics;

namespace BPSR_ZDPS.Features.FactorEnergy;

public enum BuffEdge { Snapshot, Add, Change, Remove }
public sealed record FactorBuff(int Instance, int Id, int Layer = 1, long DurationMs = 0, int? SourceConfigId = null);
public sealed record EnergyRow(int ItemId, string Name, uint Count, uint? Threshold, bool Counting, bool Calibrated, long? FreezeRemainingMs);

/// <summary>
/// Deterministic, encounter-independent counters. The caller serializes access and supplies a monotonic clock.
/// Sources share their residuals across slots: slot resets never reset a source's hit/resource remainder.
/// </summary>
public sealed class FactorEngine
{
    sealed class SourceState(SourceConfig config)
    {
        public readonly SourceConfig Config = config;
        public ulong Residual;
        public long? PreviousResource;
        public long? NextTick, Expires;
        public int? BuffInstance;
        public bool Moving;
        public Vector3? Position, StagedPosition;
        public double Meters;
        public readonly Dictionary<(long Skill, long Target), uint> Hits = [];
    }
    sealed class SlotState(SelectedSlot definition)
    {
        public readonly SelectedSlot Definition = definition;
        public uint Count;
        public bool Counting = true, Calibrated;
        public long? FreezeUntil;
    }
    List<SourceState> sources = [];
    List<SlotState> slots = [];
    readonly Dictionary<int, long> attributes = [];
    readonly Dictionary<int, FactorBuff> buffs = [];
    readonly List<int> skillQueue = [];
    string signature = "";
    long clock;
    bool inBatch;
    long batchTime;

    public FactorSelection? Selection { get; private set; }
    public int PendingSkillCount => skillQueue.Count;

    public bool Configure(FactorSelection selection, long now, bool force = false)
    {
        Selection = selection;
        if (!force && signature == selection.Signature) return false;
        signature = selection.Signature;
        sources = selection.Sources.SelectMany(x => x.Sources).Select(x => new SourceState(x)).ToList();
        slots = selection.Slots.Select(x => new SlotState(x)).ToList();
        if (force) { attributes.Clear(); buffs.Clear(); skillQueue.Clear(); }
        clock = now - 1;
        return true;
    }

    public EnergyRow[] Snapshot(long now) => slots.Select(s => new EnergyRow(s.Definition.ItemId, s.Definition.Name,
        s.Count, s.Definition.Config.Threshold, s.Counting, s.Calibrated,
        s.FreezeUntil is { } deadline ? Math.Max(0, deadline - now) : null)).ToArray();

    // Wire events win over timers at exactly the same timestamp. Timers strictly before the packet
    // run first; timers at the packet time (including the initial tick) run after its complete batch.
    public void BeginBatch(long now)
    {
        if (inBatch) throw new InvalidOperationException("Nested factor packet batch.");
        Advance(now - 1);
        inBatch = true;
        batchTime = now;
    }

    void PrepareEvent(long now) { if (!inBatch) Advance(now - 1); }

    void Add(ulong value)
    {
        foreach (var slot in slots.Where(x => x.Counting))
            slot.Count = (uint)Math.Min(uint.MaxValue, value + slot.Count);
    }

    void Apply(SlotState slot, string action, long now, bool armTimer = true)
    {
        if (action is "reset" or "resetAndFreeze" or "resetAndFreezeKeepCounting" or "resetAndStartCount")
        {
            slot.Count = 0;
            slot.Calibrated = true;
        }
        if (action is "freeze" or "resetAndFreeze") slot.Counting = false;
        if (action is "resetAndFreezeKeepCounting" or "resetAndStartCount" or "startCount") slot.Counting = true;
        if (action is "resetAndStartCount" or "startCount") slot.FreezeUntil = null;
        if (armTimer && action is ("freeze" or "resetAndFreeze" or "resetAndFreezeKeepCounting"))
        {
            if (slot.Definition.Config.FreezeDurationMs is { } duration)
            {
                slot.FreezeUntil = duration > 0 ? now + duration : null;
                if (duration == 0) Apply(slot, slot.Definition.Config.OnFreezeExpire, now, false);
            }
        }
    }

    // Advance in chronological order so a delayed UI/timer callback cannot move pre-reset ticks past a reset.
    // Buff expiry is exclusive, matching the donor's TickSchedule.
    public void Advance(long now)
    {
        if (now < clock) return;
        while (true)
        {
            long next = long.MaxValue;
            foreach (var s in slots) if (s.FreezeUntil is { } f) next = Math.Min(next, f);
            foreach (var s in sources)
                if (s.NextTick is { } t && (s.Expires == null || t < s.Expires)) next = Math.Min(next, t);
            if (next > now || next == long.MaxValue) break;
            foreach (var s in slots.Where(x => x.FreezeUntil == next))
            {
                s.FreezeUntil = null;
                Apply(s, s.Definition.Config.OnFreezeExpire, next, false);
            }
            foreach (var s in sources.Where(x => x.NextTick == next && (x.Expires == null || next < x.Expires)))
            {
                var condition = s.Config.AttrCondition;
                if (condition == null || (attributes.TryGetValue(condition.AttrId, out var value) && value == condition.RequiredValue))
                    Add(s.Config.Increment);
                s.NextTick = next + s.Config.TickIntervalMs;
            }
        }
        clock = now;
    }

    public void SkillStarted(int skillId, long now)
    {
        PrepareEvent(now);
        foreach (var s in sources.Where(x => x.Config.Kind == "skillCast" && x.Config.SkillBaseIds.Contains(skillId))) Add(s.Config.Increment);
        if (skillId is 1215 or 1238 or 1237) return;
        skillQueue.Add(skillId);
        if (skillQueue.Count == 1) StartDuration(skillId, now);
    }

    void StartDuration(int skillId, long now)
    {
        foreach (var s in sources.Where(x => x.Config.Kind == "skillDurationTick" && x.Config.SkillBaseId == skillId))
            s.NextTick = now;
    }

    public void SkillCompleted(int skillId, long now)
    {
        PrepareEvent(now);
        int index = skillQueue.IndexOf(skillId);
        if (index < 0) return;
        skillQueue.RemoveAt(index);
        if (index != 0) return;
        foreach (var s in sources.Where(x => x.Config.Kind == "skillDurationTick")) s.NextTick = null;
        if (skillQueue.Count > 0) StartDuration(skillQueue[0], now);
    }

    public void Damage(long key, long target, int flags, bool outgoing, bool incoming)
    {
        foreach (var s in sources)
        {
            var c = s.Config;
            if (c.RequiredTypeFlags is { } required && (flags & required) != required) continue;
            bool matched = outgoing && c.Kind is ("damageBySkillKey" or "damageBySkillKeyOnce") && c.SkillKeys.Contains(key);
            matched |= incoming && c.Kind == "damageTaken" && (c.SkillKeys.Length == 0 || c.SkillKeys.Contains(key));
            if (!matched) continue;
            if (c.Kind == "damageBySkillKeyOnce")
                s.Hits[(key, target)] = s.Hits.GetValueOrDefault((key, target)) + 1;
            else
            {
                s.Residual++;
                Add((s.Residual / c.HitsRequired) * c.Increment);
                s.Residual %= c.HitsRequired;
            }
        }
    }

    public void Attribute(int attrId, long value) => attributes[attrId] = value;

    public void Resource(int id, long value, bool snapshot = false)
    {
        foreach (var s in sources.Where(x => x.Config.Kind == "fightResourceSpent" && x.Config.ResourceId == id))
        {
            if (!snapshot && s.PreviousResource is { } previous && previous > value && value >= 0)
            {
                s.Residual += (ulong)(previous - value);
                Add(s.Residual / s.Config.UnitsRequired * s.Config.Increment);
                s.Residual %= s.Config.UnitsRequired;
            }
            s.PreviousResource = value;
        }
    }

    public void Position(int attrId, Vector3 position, bool snapshot = false)
    {
        if (snapshot || !float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) return;
        foreach (var s in sources.Where(x => x.Config.Kind == "movementDistance" && x.Config.AttrId == attrId && x.Moving))
            s.StagedPosition = position;
    }

    public void EndBatch()
    {
        foreach (var s in sources)
        {
            if (s.Hits.Count > 0)
            {
                Add(s.Hits.GroupBy(x => x.Key.Skill).Aggregate(0UL, (n, g) => n + g.Max(x => x.Value)) * s.Config.Increment);
                s.Hits.Clear();
            }
            if (s.StagedPosition is not { } position) continue;
            s.StagedPosition = null;
            if (s.Moving && s.Position is { } previous)
            {
                var distance = Vector3.Distance(position, previous);
                if (distance > 50) s.Meters = 0; // teleport / map change
                else
                {
                    s.Meters += distance;
                    var triggers = (ulong)(s.Meters / s.Config.MetersRequired);
                    s.Meters -= triggers * s.Config.MetersRequired;
                    Add(triggers * s.Config.Increment);
                }
            }
            s.Position = position;
        }
        if (inBatch)
        {
            inBatch = false;
            Advance(batchTime);
        }
    }

    public void BuffSnapshot(IEnumerable<FactorBuff> values)
    {
        buffs.Clear();
        foreach (var buff in values) buffs[buff.Instance] = buff;
    }

    public void BuffChanged(int instance, int? layer, long? durationMs, long now)
    {
        if (!buffs.TryGetValue(instance, out var buff)) return;
        Buff(BuffEdge.Change, buff with { Layer = layer ?? buff.Layer, DurationMs = durationMs ?? buff.DurationMs }, now, durationMs != null);
    }

    public void BuffRemoved(int instance, long now)
    {
        if (buffs.TryGetValue(instance, out var buff)) Buff(BuffEdge.Remove, buff, now);
    }

    public void Buff(BuffEdge edge, FactorBuff buff, long now, bool durationUpdated = true)
    {
        PrepareEvent(now);
        buffs.TryGetValue(buff.Instance, out var old);
        if (edge == BuffEdge.Remove) buffs.Remove(buff.Instance); else buffs[buff.Instance] = buff;
        if (edge == BuffEdge.Snapshot) return;
        foreach (var s in sources.Where(x => x.Config.BuffId == buff.Id))
        {
            var c = s.Config;
            switch (c.Kind)
            {
                case "buffAdded" when edge == BuffEdge.Add && (c.SourceConfigId == null || c.SourceConfigId == buff.SourceConfigId):
                    Add(c.Increment); break;
                case "buffLayerSpent" when edge == BuffEdge.Change && old != null && old.Layer > buff.Layer:
                    s.Residual += (ulong)(old.Layer - buff.Layer);
                    Add(s.Residual / c.UnitsRequired * c.Increment);
                    s.Residual %= c.UnitsRequired;
                    break;
                case "buffDurationTick":
                    if (edge == BuffEdge.Remove)
                    {
                        if (s.BuffInstance == buff.Instance) { s.BuffInstance = null; s.NextTick = null; s.Expires = null; }
                    }
                    else if (edge == BuffEdge.Add || s.BuffInstance != buff.Instance)
                    {
                        s.BuffInstance = buff.Instance;
                        s.NextTick = now;
                        s.Expires = buff.DurationMs > 0 ? now + buff.DurationMs : null;
                    }
                    else if (durationUpdated) s.Expires = buff.DurationMs > 0 ? now + buff.DurationMs : null;
                    break;
                case "movementDistance":
                    if (edge != BuffEdge.Change || !s.Moving)
                    {
                        s.Moving = edge != BuffEdge.Remove;
                        s.Position = null; s.Meters = 0;
                    }
                    break;
            }
        }
        foreach (var slot in slots)
        {
            var c = slot.Definition.Config;
            if (c.ResetBuffId != buff.Id || (c.ResetSourceConfigId != null && c.ResetSourceConfigId != buff.SourceConfigId)) continue;
            Apply(slot, edge switch { BuffEdge.Add => c.OnBuffAdd, BuffEdge.Change => c.OnBuffChange, _ => c.OnBuffRemove }, now);
        }
    }

    public void LocalDisappeared()
    {
        buffs.Clear(); skillQueue.Clear();
        foreach (var s in sources)
        {
            s.PreviousResource = null; s.NextTick = null;
            if (s.Config.Kind is "fightResourceSpent" or "buffLayerSpent") s.Residual = 0;
            s.BuffInstance = null; s.Expires = null; s.Moving = false;
            s.Position = s.StagedPosition = null; s.Meters = 0; s.Hits.Clear();
        }
    }
}
