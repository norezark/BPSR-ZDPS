using System.Globalization;
using System.Numerics;
using BPSR_ZDPSLib.ServiceMethods;
using Google.Protobuf;
using Zproto;
using WorldNtf = BPSR_ZDPSLib.ServiceMethods.WorldNtf;

namespace BPSR_ZDPS.Features.FactorEnergy;

/// <summary>Independent adapter; never mutates the DPS encounter or its protobuf/blob state.</summary>
public sealed class FactorProtocol(FactorCatalog catalog, Func<int, int, int?> skillEffect)
{
    public FactorEngine Engine { get; } = new();
    public long LocalUuid { get; private set; }
    public int SeasonId { get; private set; }
    public bool HasSnapshot { get; private set; }
    public string? Error { get; private set; }
    public long Notifications { get; private set; }
    public long SkillRequests { get; private set; }
    SeasonCultivateLineData? season;
    int[] resourceIds = [];

    public void Reset(long now)
    {
        LocalUuid = 0; SeasonId = 0; HasSnapshot = false; Error = null; season = null; resourceIds = [];
        Engine.Configure(catalog.Select([]), now, true);
    }

    public void Invalidate(string message, long now)
    {
        Reset(now);
        Error = message;
    }

    public void Notify(WorldNtf method, ReadOnlySpan<byte> bytes, long now)
    {
        if (method is not (WorldNtf.SyncContainerData or WorldNtf.SyncContainerDirtyData or WorldNtf.SyncNearDeltaInfo
            or WorldNtf.SyncToMeDeltaInfo or WorldNtf.SyncNearEntities or WorldNtf.SyncServerSkillEnd or WorldNtf.EnterScene)) return;
        Notifications++;
        Engine.BeginBatch(now);
        try
        {
        switch (method)
        {
            case WorldNtf.SyncContainerData:
                var data = WorldNtfCsharp.Types.SyncContainerData.Parser.ParseFrom(bytes).VData;
                if (data == null) return;
                LocalUuid = data.CharId > 0 ? (data.CharId << 16) | (10L << 6) : 0;
                season = data.SeasonCultivateLineData?.Clone() ?? new();
                HasSnapshot = true; Error = null; resourceIds = [];
                Configure(now, true);
                break;
            case WorldNtf.SyncContainerDirtyData:
                if (!HasSnapshot || season == null) return;
                var stream = WorldNtfCsharp.Types.SyncContainerDirtyData.Parser.ParseFrom(bytes).VData;
                if (stream != null && SeasonDirtyReader.Merge(stream.Buffer.Span, season, out var merged))
                {
                    season = merged;
                    Configure(now, false);
                }
                break;
            case WorldNtf.SyncToMeDeltaInfo:
                var mine = WorldNtfCsharp.Types.SyncToMeDeltaInfo.Parser.ParseFrom(bytes).DeltaInfo;
                if (mine == null) return;
                long uuid = mine.Uuid != 0 ? mine.Uuid : mine.BaseDelta?.Uuid ?? 0;
                if (uuid != 0)
                {
                    if (LocalUuid != 0 && uuid != LocalUuid) Reset(now);
                    LocalUuid = uuid;
                }
                if (HasSnapshot)
                    foreach (var outer in Wire.Messages(bytes, 1))
                        foreach (var delta in Wire.Messages(outer, 1)) Delta(delta, true, now);
                break;
            case WorldNtf.SyncNearDeltaInfo:
                if (HasSnapshot)
                    foreach (var delta in Wire.Messages(bytes, 1)) Delta(delta, false, now);
                break;
            case WorldNtf.SyncNearEntities:
                if (!HasSnapshot || LocalUuid == 0) return;
                var entities = WorldNtfCsharp.Types.SyncNearEntities.Parser.ParseFrom(bytes);
                foreach (var entity in entities.Appear.Where(x => x.Uuid == LocalUuid))
                {
                    Attributes(entity.Attrs, true);
                    if (entity.BuffInfos != null) Engine.BuffSnapshot(entity.BuffInfos.BuffInfos.Select(FromBuff));
                    BuffEffects(entity.BuffEffect, entity.Uuid, now);
                }
                if (entities.Disappear.Any(x => x.Uuid == LocalUuid)) Engine.LocalDisappeared();
                break;
            case WorldNtf.SyncServerSkillEnd:
                if (HasSnapshot) Engine.SkillCompleted(WorldNtfCsharp.Types.SyncServerSkillEnd.Parser.ParseFrom(bytes).SkillUuid, now);
                break;
            case WorldNtf.EnterScene:
                Engine.LocalDisappeared(); resourceIds = [];
                break;
        }
        }
        finally { Engine.EndBatch(); }
    }

    public void UseSlot(ReadOnlySpan<byte> bytes, long now)
    {
        if (!HasSnapshot || LocalUuid == 0 || Error != null) return;
        var request = World.Types.UseSlot.Parser.ParseFrom(bytes).VRequest;
        if (request?.UseType != EUseSlotType.UseSlotTypeSkill || request.ExtraData.IsEmpty) return;
        var skill = UseSkillParam.Parser.ParseFrom(request.ExtraData);
        if (skill.Skillid <= 0) return;
        SkillRequests++;
        Engine.BeginBatch(now);
        try { Engine.SkillStarted(skill.Skillid, now); }
        finally { Engine.EndBatch(); }
    }

    void Configure(long now, bool force)
    {
        var active = season!.SeasonCultivateLineMap.OrderByDescending(x => x.Key)
            .FirstOrDefault(x => x.Value.CultivateLineMap.TryGetValue(800522, out var line) && line.CultivateLineDataMap.Count != 0);
        SeasonId = active.Key;
        int[] items = [];
        if (active.Value != null && SeasonId < 4)
        {
            var line = active.Value.CultivateLineMap[800522];
            var areas = line.CultivateLineAreaList.Count > 0
                ? line.CultivateLineAreaList.ToHashSet()
                : line.CultivateLineDataMap.Where(x => x.Value.IsActive).Select(x => x.Key).ToHashSet();
            items = line.CultivateLineDataMap.Where(x => areas.Contains(x.Key))
                .SelectMany(x => x.Value.CultivateMiddleNodeMap.Values).Select(x => x.ItemId).ToArray();
        }
        Engine.Configure(catalog.Select(items), now, force);
    }

    void Delta(ReadOnlySpan<byte> raw, bool toMe, long now)
    {
        var delta = AoiSyncDelta.Parser.ParseFrom(raw);
        long target = delta.Uuid != 0 ? delta.Uuid : toMe ? LocalUuid : 0;
        if (target == LocalUuid && LocalUuid != 0) Attributes(delta.Attrs, false);
        // Preserve wire presence: proto3 generated properties alone lose absent-vs-zero damage values.
        foreach (var skill in Wire.Messages(raw, AoiSyncDelta.SkillEffectsFieldNumber))
        foreach (var hit in Wire.Messages(skill, SkillEffect.DamagesFieldNumber))
        {
            var fields = Wire.Fields(hit);
            var d = SyncDamageInfo.Parser.ParseFrom(hit);
            if (!fields.Contains(SyncDamageInfo.OwnerIdFieldNumber)) continue;
            if (fields.Contains(SyncDamageInfo.ValueFieldNumber)) { if (d.Value < 0) continue; }
            else if (!fields.Contains(SyncDamageInfo.LuckyValueFieldNumber) || d.LuckyValue < 0) continue;
            long owner = d.TopSummonerId != 0 ? d.TopSummonerId : d.AttackerUuid;
            bool outgoing = LocalUuid != 0 && owner == LocalUuid;
            bool incoming = toMe && target == LocalUuid && owner != LocalUuid && d.Type != EDamageType.Heal;
            if (outgoing || incoming) Engine.Damage(DamageKey(d), target, d.TypeFlag, outgoing, incoming);
        }
        // Buff edges follow damage; once-per-target damage sources commit at the end of the whole notification.
        BuffEffects(delta.BuffEffect, target, now);
    }

    public long DamageKey(SyncDamageInfo damage)
    {
        int source = (int)damage.DamageSource;
        int kind = source > 0 ? source == 2 ? 2 : 3 : 1;
        int effect = source > 0 ? damage.OwnerId : skillEffect(damage.OwnerId, damage.OwnerLevel)
            ?? skillEffect(damage.OwnerId, 1) ?? damage.OwnerId;
        return long.Parse(string.Concat(kind.ToString(CultureInfo.InvariantCulture), effect.ToString(CultureInfo.InvariantCulture),
            damage.HitEventId.ToString("D2", CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
    }

    void Attributes(AttrCollection? attributes, bool snapshot)
    {
        if (attributes == null) return;
        long[]? values = null;
        foreach (var attr in attributes.Attrs)
        {
            switch (attr.Id)
            {
                case 52:
                    if (Wire.OptionalPosition(attr.RawData.Span) is { } position)
                        Engine.Position(52, position, snapshot);
                    break;
                case 71: Engine.Attribute(71, Wire.OptionalVarint(attr.RawData.Span)); break;
                case 50001:
                    if (Wire.OptionalPacked(attr.RawData.Span) is { } ids)
                        resourceIds = ids.Where(x => x is >= int.MinValue and <= int.MaxValue).Select(x => (int)x).ToArray();
                    break;
                case 50002: values = Wire.OptionalPacked(attr.RawData.Span); break;
            }
        }
        if (values != null)
            for (int i = 0; i < Math.Min(resourceIds.Length, values.Length); i++) Engine.Resource(resourceIds[i], values[i], snapshot);
    }

    static FactorBuff FromBuff(BuffInfo info) => new(info.BuffUuid, info.BaseId, info.Layer, info.Duration, info.FightSourceInfo?.SourceConfigId);

    void BuffEffects(BuffEffectSync? sync, long fallback, long now)
    {
        if (sync == null || LocalUuid == 0) return;
        foreach (var effect in sync.BuffEffects)
        {
            long target = effect.HostUuid != 0 ? effect.HostUuid : sync.Uuid != 0 ? sync.Uuid : fallback;
            if (target != LocalUuid) continue;
            foreach (var logic in effect.LogicEffect)
            {
                if (logic.EffectType == EBuffEffectLogicPbType.BuffEffectAddBuff)
                {
                    var info = BuffInfo.Parser.ParseFrom(logic.RawData);
                    var buff = FromBuff(info) with { Instance = effect.BuffUuid };
                    if (!Wire.Fields(logic.RawData.Span).Contains(BuffInfo.LayerFieldNumber)) buff = buff with { Layer = 1 };
                    Engine.Buff(BuffEdge.Add, buff, now);
                }
                else if (logic.EffectType == EBuffEffectLogicPbType.BuffEffectBuffChange)
                {
                    var change = BuffChange.Parser.ParseFrom(logic.RawData);
                    var fields = Wire.Fields(logic.RawData.Span);
                    Engine.BuffChanged(effect.BuffUuid, fields.Contains(1) ? change.Layer : null,
                        fields.Contains(2) && change.Duration >= 0 ? change.Duration : null, now);
                }
            }
            if (effect.Type == EBuffEventType.BuffEventRemove) Engine.BuffRemoved(effect.BuffUuid, now);
        }
    }
}

/// <summary>Small protobuf wire helpers, used only where generated proto3 classes erase field presence.</summary>
public static class Wire
{
    public static HashSet<int> Fields(ReadOnlySpan<byte> bytes)
    {
        var fields = new HashSet<int>();
        using var input = new CodedInputStream(bytes.ToArray());
        uint tag;
        while ((tag = input.ReadTag()) != 0) { fields.Add((int)(tag >> 3)); input.SkipLastField(); }
        return fields;
    }
    public static List<byte[]> Messages(ReadOnlySpan<byte> bytes, int field)
    {
        var messages = new List<byte[]>();
        using var input = new CodedInputStream(bytes.ToArray());
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (tag == (uint)((field << 3) | 2)) messages.Add(input.ReadBytes().ToByteArray());
            else input.SkipLastField();
        }
        return messages;
    }
    // Attribute rawData is optional. A missing/undecodable integer means zero in the donor decoder;
    // an absent vector or resource array means no observation, not a broken character snapshot.
    public static long OptionalVarint(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return 0;
        try
        {
            using var input = new CodedInputStream(bytes.ToArray());
            return input.ReadInt64();
        }
        catch (InvalidProtocolBufferException) { return 0; }
    }

    public static Vector3? OptionalPosition(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return null;
        try
        {
            var fields = Fields(bytes);
            if (!fields.Contains(Zproto.Position.XFieldNumber) || !fields.Contains(Zproto.Position.YFieldNumber)
                || !fields.Contains(Zproto.Position.ZFieldNumber)) return null;
            var pos = Zproto.Position.Parser.ParseFrom(bytes);
            return new Vector3(pos.X, pos.Y, pos.Z);
        }
        catch (InvalidProtocolBufferException) { return null; }
    }

    public static long[]? OptionalPacked(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return null;
        try
        {
            using var input = new CodedInputStream(bytes.ToArray());
            if (input.ReadTag() != 10) return null;
            using var packed = new CodedInputStream(input.ReadBytes().ToByteArray());
            var values = new List<long>();
            while (!packed.IsAtEnd) values.Add(packed.ReadInt64());
            return values.ToArray();
        }
        catch (InvalidProtocolBufferException) { return null; }
    }
}
