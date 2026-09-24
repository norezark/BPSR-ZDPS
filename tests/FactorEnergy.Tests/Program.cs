using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using BPSR_ZDPS.Features.FactorEnergy;
using BPSR_ZDPSLib;
using BPSR_ZDPSLib.ServiceMethods;
using Google.Protobuf;
using Zproto;
using WorldNtf = BPSR_ZDPSLib.ServiceMethods.WorldNtf;

var catalog = new FactorCatalog(Path.Combine(AppContext.BaseDirectory, "Data"));
int passed = 0, failed = 0;
void Test(string name, Action test)
{
    try { test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex}"); }
}
void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}");
}
void Check(bool actual, string message = "Assertion failed") { if (!actual) throw new Exception(message); }
FactorEngine NewEngine(SourceConfig[] sources, params SlotConfig[] slots)
{
    if (slots.Length == 0) slots = [new SlotConfig() { Threshold = 100 }];
    var engine = new FactorEngine();
    engine.Configure(new([1], [new("test", "test", [1], sources)],
        slots.Select((s, i) => new SelectedSlot(i + 1, "test", s)).ToArray(), []), 0, true);
    return engine;
}
uint Count(FactorEngine e, int index = 0) => e.Snapshot(0)[index].Count;
SourceConfig Cast(uint increment = 25) => new() { Kind = "skillCast", SkillBaseIds = [1714], Increment = increment };

Test("catalog: all nine classes, 84 templates / 86 sources / 39 outputs / 390 costs", () =>
{
    Equal(84, catalog.Sources.Length); Equal(86, catalog.Sources.Sum(x => x.Sources.Length));
    Equal(39, catalog.Slots.Length); Equal(390, catalog.Costs.Count);
    Equal(9, catalog.Sources.Select(x => x.Id.Split("_x")[0].Replace("_s3", "")).Distinct().Count());
    foreach (var slot in catalog.Slots)
    foreach (int id in slot.ItemIds)
        Equal(catalog.Costs[id], catalog.Select([id]).Slots.Single().Config.Threshold!.Value);
    Equal(720u, catalog.Select([20021731]).Slots.Single().Config.Threshold!.Value);
    Equal(610u, catalog.Select([20021733]).Slots.Single().Config.Threshold!.Value);
});

// Exercise every shipped rule rather than only a hand-picked class. Each event has independently stated expected energy.
foreach (var template in catalog.Sources)
foreach (var source in template.Sources)
Test($"definition: {template.Id}/{source.Kind}", () =>
{
    var e = NewEngine([source]);
    var buff = new FactorBuff(1, source.BuffId, (int)source.UnitsRequired + 1, 100_000, source.SourceConfigId);
    switch (source.Kind)
    {
        case "skillCast": e.SkillStarted(source.SkillBaseIds[0], 0); break;
        case "skillDurationTick": e.SkillStarted(source.SkillBaseId, 0); e.Advance(0); break;
        case "damageBySkillKey":
        case "damageBySkillKeyOnce":
        case "damageTaken":
            for (int i = 0; i < source.HitsRequired; i++)
                e.Damage(source.SkillKeys.FirstOrDefault(), 101, source.RequiredTypeFlags ?? 0,
                    source.Kind != "damageTaken", source.Kind == "damageTaken");
            e.EndBatch(); break;
        case "fightResourceSpent": e.Resource(source.ResourceId, source.UnitsRequired); e.Resource(source.ResourceId, 0); break;
        case "buffAdded": e.Buff(BuffEdge.Add, buff, 0); break;
        case "buffLayerSpent": e.Buff(BuffEdge.Snapshot, buff, 0); e.BuffChanged(1, 1, null, 0); break;
        case "buffDurationTick":
            if (source.AttrCondition is { } c) e.Attribute(c.AttrId, c.RequiredValue);
            e.Buff(BuffEdge.Add, buff, 0); e.Advance(0); break;
        case "movementDistance":
            e.Buff(BuffEdge.Add, buff, 0); e.Position(source.AttrId, Vector3.Zero); e.EndBatch();
            e.Position(source.AttrId, new((float)source.MetersRequired, 0, 0)); e.EndBatch(); break;
    }
    Equal(source.Increment, Count(e));
});

Test("once: multi-target, repeated hits, distinct keys, and batch reset", () =>
{
    var e = NewEngine([new() { Kind = "damageBySkillKeyOnce", SkillKeys = [123, 456], Increment = 10 }]);
    e.Damage(123, 1, 0, true, false); e.Damage(123, 2, 0, true, false); e.Damage(123, 1, 0, true, false);
    e.Damage(456, 2, 0, true, false); Equal(0u, Count(e)); e.EndBatch(); Equal(30u, Count(e));
    e.Damage(123, 1, 0, true, false); e.EndBatch(); Equal(40u, Count(e));
});
Test("once: deferred credit follows a same-batch reset", () =>
{
    var e = NewEngine([new() { Kind = "damageBySkillKeyOnce", SkillKeys = [1], Increment = 12 }],
        new SlotConfig() { ResetBuffId = 99, OnBuffAdd = "reset" });
    e.Damage(1, 2, 0, true, false); e.Buff(BuffEdge.Add, new(1, 99), 0); e.EndBatch(); Equal(12u, Count(e));
});
Test("crit/lucky bitmask requires all bits, unrelated actors do not count", () =>
{
    var e = NewEngine([new() { Kind = "damageBySkillKey", SkillKeys = [1], Increment = 7, RequiredTypeFlags = 5 }]);
    foreach (int flags in new[] { 0, 1, 4 }) e.Damage(1, 2, flags, true, false);
    e.Damage(1, 2, 5, false, false); Equal(0u, Count(e));
    e.Damage(1, 2, 7, true, false); Equal(7u, Count(e));
});
Test("independent slot freezes, reset calibration, no automatic wrap at threshold", () =>
{
    var e = NewEngine([Cast()], new SlotConfig() { Threshold = 20, ResetBuffId = 99, OnBuffAdd = "freeze", OnBuffRemove = "resetAndStartCount" },
        new SlotConfig() { Threshold = 20, ResetBuffId = 98, OnBuffAdd = "reset" });
    e.SkillStarted(1714, 0); Check(!e.Snapshot(0)[0].Calibrated); Equal(25u, Count(e));
    e.Buff(BuffEdge.Add, new(1, 99), 1); e.SkillStarted(1714, 2); Equal(25u, Count(e)); Equal(50u, Count(e, 1));
    e.BuffRemoved(1, 3); Equal(0u, Count(e)); Check(e.Snapshot(3)[0].Calibrated); e.SkillStarted(1714, 4); Equal(25u, Count(e));
});
Test("late timer advancement splits energy before and after timed slot reset", () =>
{
    var e = NewEngine([new() { Kind = "buffDurationTick", BuffId = 5, TickIntervalMs = 1000, Increment = 10 }],
        new SlotConfig() { ResetBuffId = 6, OnBuffAdd = "freeze", FreezeDurationMs = 2500 });
    e.Buff(BuffEdge.Add, new(1, 5), 0); e.Buff(BuffEdge.Add, new(2, 6), 0);
    e.Advance(4000); Equal(20u, Count(e)); Check(e.Snapshot(4000)[0].Counting);
});
Test("resetAndFreezeKeepCounting resets again at expiry", () =>
{
    var e = NewEngine([Cast()], new SlotConfig() { ResetBuffId = 1, OnBuffAdd = "resetAndFreezeKeepCounting", FreezeDurationMs = 1000 });
    e.SkillStarted(1714, 0); e.Buff(BuffEdge.Add, new(1, 1), 0); e.SkillStarted(1714, 500);
    Equal(25u, Count(e)); e.Advance(1000); Equal(0u, Count(e));
});
Test("snapshots do not trigger resets, ticks or gains", () =>
{
    var e = NewEngine([new() { Kind = "buffDurationTick", BuffId = 1, TickIntervalMs = 1000, Increment = 12 }],
        new SlotConfig() { ResetBuffId = 1, OnBuffAdd = "freeze" });
    e.BuffSnapshot([new(1, 1)]); e.Advance(5000); Equal(0u, Count(e)); Check(e.Snapshot(0)[0].Counting);
    e.BuffChanged(1, 1, 2000, 5000); e.Advance(6000); Equal(24u, Count(e));
});
Test("buff expiry excludes the final tick; same-instance refresh preserves cadence", () =>
{
    var e = NewEngine([new() { Kind = "buffDurationTick", BuffId = 1, TickIntervalMs = 1000, Increment = 10 }]);
    e.Buff(BuffEdge.Add, new(1, 1, DurationMs: 2000), 0);
    e.BuffChanged(1, null, 2000, 1500); e.Advance(3500); Equal(40u, Count(e));
    e.Buff(BuffEdge.Add, new(2, 1, DurationMs: 2000), 4000);
    e.BuffRemoved(1, 4100); e.Advance(6000); Equal(60u, Count(e));
});
Test("buff layer removal is not consumption; partial changes preserve layer", () =>
{
    var e = NewEngine([new() { Kind = "buffLayerSpent", BuffId = 1, UnitsRequired = 10, Increment = 12 }]);
    e.BuffSnapshot([new(1, 1, 12)]); e.BuffChanged(1, null, 2000, 0); Equal(0u, Count(e));
    e.BuffChanged(1, 5, null, 0); e.BuffChanged(1, 2, null, 0); Equal(12u, Count(e));
    e.BuffRemoved(1, 0); Equal(12u, Count(e));
});
Test("resource baseline, gains, partial spends and frozen-slot residual", () =>
{
    var e = NewEngine([new() { Kind = "fightResourceSpent", ResourceId = 1, UnitsRequired = 5, Increment = 12 }],
        new SlotConfig() { ResetBuffId = 1, OnBuffAdd = "freeze", OnBuffRemove = "resetAndStartCount" });
    e.Resource(1, 10); e.Resource(1, 7); e.Resource(1, 15); Equal(0u, Count(e));
    e.Buff(BuffEdge.Add, new(1, 1), 0); e.Resource(1, 14); e.BuffRemoved(1, 0);
    e.Resource(1, 13); Equal(12u, Count(e));
});
Test("movement uses last position per batch, 3D distance and teleport rejection", () =>
{
    var e = NewEngine([new() { Kind = "movementDistance", BuffId = 1, AttrId = 52, MetersRequired = 5, Increment = 10 }]);
    e.Buff(BuffEdge.Add, new(1, 1), 0); e.Position(52, Vector3.Zero); e.EndBatch();
    e.Position(52, new(100, 0, 0)); e.Position(52, new(3, 4, 0)); e.EndBatch(); Equal(10u, Count(e));
    e.Position(52, new(100, 100, 100)); e.EndBatch(); Equal(10u, Count(e));
    e.Position(52, new(100, 100, 105)); e.EndBatch(); Equal(20u, Count(e));
    e.BuffRemoved(1, 0); e.Position(52, new(100, 100, 110)); e.EndBatch(); Equal(20u, Count(e));
});
Test("duration skills wait for previous completion and stop at completion", () =>
{
    var e = NewEngine([new() { Kind = "skillDurationTick", SkillBaseId = 1241, TickIntervalMs = 500, Increment = 10 }]);
    e.SkillStarted(1000, 0); e.SkillStarted(1241, 100); e.Advance(1000); Equal(0u, Count(e));
    e.SkillCompleted(1000, 1000); e.Advance(1500); Equal(20u, Count(e));
    e.SkillCompleted(1241, 1600); e.Advance(5000); Equal(20u, Count(e)); Equal(0, e.PendingSkillCount);
    e.SkillStarted(1238, 5000); Equal(0, e.PendingSkillCount);
});
Test("selection changes reset counters; duplicate/unknown selection updates do not", () =>
{
    var e = new FactorEngine(); e.Configure(catalog.Select([20020001, 20021731]), 0, true);
    e.SkillStarted(1714, 0); e.Configure(catalog.Select([20021731, 20020001, 20020001, 9999]), 1); Equal(25u, Count(e));
    e.Configure(catalog.Select([20020001, 20021733]), 2); Equal(0u, Count(e)); Equal(610u, e.Snapshot(2)[0].Threshold!.Value);
});
Test("reset sourceConfigId gate", () =>
{
    var e = NewEngine([Cast()], new SlotConfig() { ResetBuffId = 9901, ResetSourceConfigId = 3052430, OnBuffAdd = "resetAndFreeze" });
    e.SkillStarted(1714, 0); e.Buff(BuffEdge.Add, new(1, 9901, SourceConfigId: 1), 0); Equal(25u, Count(e));
    e.Buff(BuffEdge.Add, new(2, 9901, SourceConfigId: 3052430), 0); Equal(0u, Count(e)); Check(!e.Snapshot(0)[0].Counting);
});

SeasonCultivateLineData Season(int seasonId, params int[] items)
{
    var area = new CultivateAreaData { IsActive = true };
    for (int i = 0; i < items.Length; i++) area.CultivateMiddleNodeMap[i + 1] = new() { ItemId = items[i] };
    var subtype = new CultivateLineSubTypeData(); subtype.CultivateLineDataMap[1] = area; subtype.CultivateLineAreaList.Add(1);
    var line = new CultivateLineData(); line.CultivateLineMap[800522] = subtype;
    var data = new SeasonCultivateLineData(); data.SeasonCultivateLineMap[seasonId] = line;
    return data;
}
byte[] Container(SeasonCultivateLineData data, long charId = 42) => new WorldNtfCsharp.Types.SyncContainerData
    { VData = new CharSerialize { CharId = charId, SeasonCultivateLineData = data } }.ToByteArray();
FactorProtocol Protocol(params int[] items)
{
    var p = new FactorProtocol(catalog, (id, level) => id == 1714 && level == 1 ? 171400 : null);
    p.Notify(WorldNtf.SyncContainerData, Container(Season(3, items)), 0); return p;
}
byte[] Skill(int id) => new World.Types.UseSlot { VRequest = new UseSlotRequest
    { UseType = EUseSlotType.UseSlotTypeSkill, ExtraData = new UseSkillParam { Skillid = id }.ToByteString() } }.ToByteArray();

Test("protobuf integration: snapshot, outbound request, observed attribute doesn't double count, container resets", () =>
{
    var p = Protocol(20020001, 20021731); Equal((42L << 16) | 640, p.LocalUuid);
    p.UseSlot(Skill(1714), 1); Equal(25u, Count(p.Engine));
    var delta = new AoiSyncDelta { Uuid = p.LocalUuid, Attrs = new() };
    delta.Attrs.Attrs.Add(new Attr { Id = (int)EAttrType.AttrSkillId, RawData = ByteString.CopyFrom([0xB2, 0x0D]) });
    var notification = new WorldNtfCsharp.Types.SyncNearDeltaInfo(); notification.DeltaInfos.Add(delta);
    p.Notify(WorldNtf.SyncNearDeltaInfo, notification.ToByteArray(), 2); Equal(25u, Count(p.Engine));
    p.Notify(WorldNtf.SyncContainerData, Container(Season(3, 20020001, 20021731)), 3); Equal(0u, Count(p.Engine));
});
Test("protobuf integration: buff freeze and removal", () =>
{
    var p = Protocol(20020001, 20021731); p.UseSlot(Skill(1714), 1);
    var effect = new BuffEffect { HostUuid = p.LocalUuid, BuffUuid = 5 };
    effect.LogicEffect.Add(new BuffEffectLogicInfo { EffectType = EBuffEffectLogicPbType.BuffEffectAddBuff,
        RawData = new BuffInfo { BaseId = 3050401, BuffUuid = 5, Layer = 1 }.ToByteString() });
    var delta = new AoiSyncDelta { Uuid = p.LocalUuid, BuffEffect = new() }; delta.BuffEffect.BuffEffects.Add(effect);
    var near = new WorldNtfCsharp.Types.SyncNearDeltaInfo(); near.DeltaInfos.Add(delta);
    p.Notify(WorldNtf.SyncNearDeltaInfo, near.ToByteArray(), 2); p.UseSlot(Skill(1714), 3); Equal(25u, Count(p.Engine));
    effect.LogicEffect.Clear(); effect.Type = EBuffEventType.BuffEventRemove;
    p.Notify(WorldNtf.SyncNearDeltaInfo, near.ToByteArray(), 4); Equal(0u, Count(p.Engine));
});
Test("damage key uses skill effect table / level-one fallback / damage source", () =>
{
    var p = Protocol();
    Equal(117140002L, p.DamageKey(new() { OwnerId = 1714, OwnerLevel = 99, HitEventId = 2 }));
    Equal(312090200L, p.DamageKey(new() { OwnerId = 120902, DamageSource = (EDamageSource)3 }));
    Equal(222003L, p.DamageKey(new() { OwnerId = 220, DamageSource = (EDamageSource)2, HitEventId = 3 }));
});
Test("newer season and character switch cannot retain stale factors", () =>
{
    var p = Protocol(20020001, 20021731);
    var data = Season(3, 20020001, 20021731); data.SeasonCultivateLineMap[4] = Season(4, 123).SeasonCultivateLineMap[4];
    p.Notify(WorldNtf.SyncContainerData, Container(data), 1); Equal(4, p.SeasonId); Equal(0, p.Engine.Snapshot(1).Length);
    var mine = new WorldNtfCsharp.Types.SyncToMeDeltaInfo { DeltaInfo = new() { Uuid = (43L << 16) | 640 } };
    p.Notify(WorldNtf.SyncToMeDeltaInfo, mine.ToByteArray(), 2); Check(!p.HasSnapshot);
});

// Build actual dirty-stream bytes, including nested map updates, removals, and unrelated siblings.
byte[] Ints(params int[] values)
{
    using var stream = new MemoryStream(); using var w = new BinaryWriter(stream);
    foreach (int value in values) w.Write(value); return stream.ToArray();
}
byte[] Join(params byte[][] parts) => parts.SelectMany(x => x).ToArray();
byte[] Obj(params byte[][] parts) { var body = Join(parts); return Join(Ints(-2, body.Length), body, Ints(-3)); }
byte[] MapUpdate(int key, byte[] value) => Join(Ints(1, 0, 0, key), value);
byte[] AreaPatch(byte[] area) => Obj(Ints(101), Obj(Ints(1), MapUpdate(3, Obj(Ints(1), MapUpdate(800522,
    Obj(Ints(1), MapUpdate(1, area)))))));
Test("dirty partial map merge preserves nodes and applies all scalar fields", () =>
{
    var data = Season(3, 20020001, 20021731);
    var patch = AreaPatch(Obj(Ints(1, -4, 2), MapUpdate(2, Obj(Ints(1, 20021733))), Ints(3, -4, 4, 7, 5), [1]));
    Check(SeasonDirtyReader.Merge(patch, data, out var merged));
    var area = merged.SeasonCultivateLineMap[3].CultivateLineMap[800522].CultivateLineDataMap[1];
    Equal(20020001, area.CultivateMiddleNodeMap[1].ItemId); Equal(20021733, area.CultivateMiddleNodeMap[2].ItemId);
    Equal(7, area.ActivateEffectScore); Check(area.IsActive);
    Equal(20021731, data.SeasonCultivateLineMap[3].CultivateLineMap[800522].CultivateLineDataMap[1].CultivateMiddleNodeMap[2].ItemId);
});
Test("dirty map removals, additions, full-map marker and inactive selection", () =>
{
    var data = Season(3, 20020001, 20021731);
    var patch = AreaPatch(Obj(Ints(2, 0, 1, 1, 2, 3), Obj(Ints(1, 20021733))));
    Check(SeasonDirtyReader.Merge(patch, data, out var merged));
    var nodes = merged.SeasonCultivateLineMap[3].CultivateLineMap[800522].CultivateLineDataMap[1].CultivateMiddleNodeMap;
    Check(!nodes.ContainsKey(2)); Equal(20021733, nodes[3].ItemId); Equal(2, nodes.Count);
    var full = AreaPatch(Obj(Ints(2, -1, 1, 1), Obj(Ints(1, 20020002))));
    Check(SeasonDirtyReader.Merge(full, merged, out var updated));
    Equal(2, updated.SeasonCultivateLineMap[3].CultivateLineMap[800522].CultivateLineDataMap[1].CultivateMiddleNodeMap.Count);
});
Test("dirty malformed stream is rejected without mutating baseline", () =>
{
    var data = Season(3, 20020001, 20021731); string before = data.ToString();
    var patch = AreaPatch(Obj(Ints(2), MapUpdate(2, Obj(Ints(1, 20021733)))));
    for (int i = 0; i < patch.Length; i++)
    {
        bool rejected = false;
        try { SeasonDirtyReader.Merge(patch.AsSpan(0, i), data, out _); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, $"Accepted truncated patch at {i}"); Equal(before, data.ToString());
    }
});
Test("dirty packet integration updates the threshold, leaves identical selections intact", () =>
{
    var p = Protocol(20020001, 20021731); p.UseSlot(Skill(1714), 1);
    byte[] Packet(int item) => new WorldNtfCsharp.Types.SyncContainerDirtyData { VData = new BufferStream
        { Buffer = ByteString.CopyFrom(AreaPatch(Obj(Ints(2), MapUpdate(2, Obj(Ints(1, item)))))) } }.ToByteArray();
    p.Notify(WorldNtf.SyncContainerDirtyData, Packet(20021731), 2); Equal(25u, Count(p.Engine));
    p.Notify(WorldNtf.SyncContainerDirtyData, Packet(20021733), 3); Equal(0u, Count(p.Engine)); Equal(610u, p.Engine.Snapshot(3)[0].Threshold!.Value);
});

Test("NetCap observation hook preserves registered notify and outbound handlers", () =>
{
    var cap = new NetCap(); int observed = 0, handled = 0, proxyObserved = 0, proxyHandled = 0;
    cap.NotifyObserved += (id, bytes, meta) => observed++;
    cap.ProxyObserved += (id, bytes, meta) => proxyObserved++;
    cap.RegisterWorldNotifyHandler(WorldNtf.SyncServerSkillEnd, (bytes, meta) => handled++);
    cap.RegisterProxyHandler((uint)EProxyServiceId.World, (uint)WorldProxy.UseSlot, (bytes, uid, meta) => proxyHandled++);
    // Private parsers accept spans, so use typed delegates (reflection Invoke cannot box a span).
    var parseNotify = cap.GetType().GetMethod("ParseNotify", BindingFlags.NonPublic | BindingFlags.Instance)!
        .CreateDelegate<Action<ReadOnlySpan<byte>, bool, DateTime>>(cap);
    var notify = new byte[16]; BinaryPrimitives.WriteUInt64BigEndian(notify, (ulong)EServiceId.WorldNtf);
    BinaryPrimitives.WriteUInt32BigEndian(notify.AsSpan(12), (uint)WorldNtf.SyncServerSkillEnd);
    parseNotify(notify, false, DateTime.UtcNow); Equal(1, observed); Equal(1, handled);
    var parseCall = cap.GetType().GetMethod("ParseCall", BindingFlags.NonPublic | BindingFlags.Instance)!
        .CreateDelegate<Action<ReadOnlySpan<byte>, bool, DateTime>>(cap);
    var call = new byte[20]; BinaryPrimitives.WriteUInt64BigEndian(call, (uint)EProxyServiceId.World);
    BinaryPrimitives.WriteUInt32BigEndian(call.AsSpan(16), (uint)WorldProxy.UseSlot);
    parseCall(call, false, DateTime.UtcNow); Equal(1, proxyObserved); Equal(1, proxyHandled);
});

Test("donor scheduler parity: immediate first tick, exclusive expiry, refresh cadence", () =>
{
    var e = NewEngine([new() { Kind = "buffDurationTick", BuffId = 77, TickIntervalMs = 100, Increment = 1 }]);
    e.BeginBatch(1000); e.Buff(BuffEdge.Add, new(1, 77, DurationMs: 200), 1000); e.EndBatch(); Equal(1u, Count(e));
    e.BeginBatch(1050); e.BuffChanged(1, null, 300, 1050); e.EndBatch(); Equal(1u, Count(e));
    e.Advance(1250); Equal(3u, Count(e)); e.Advance(1350); Equal(4u, Count(e));
});
Test("buff removal and skill completion at a tick timestamp win over that tick", () =>
{
    var e = NewEngine([new() { Kind = "buffDurationTick", BuffId = 77, TickIntervalMs = 100, Increment = 1 }]);
    e.BeginBatch(0); e.Buff(BuffEdge.Add, new(1, 77), 0); e.EndBatch(); Equal(1u, Count(e));
    e.BeginBatch(100); e.BuffRemoved(1, 100); e.EndBatch(); Equal(1u, Count(e));
    var skill = NewEngine([new() { Kind = "skillDurationTick", SkillBaseId = 1241, TickIntervalMs = 500, Increment = 10 }]);
    skill.BeginBatch(0); skill.SkillStarted(1241, 0); skill.EndBatch(); Equal(10u, Count(skill));
    skill.BeginBatch(500); skill.SkillCompleted(1241, 500); skill.EndBatch(); Equal(10u, Count(skill));
});
Test("initial timer runs after all packet reset/freeze edges", () =>
{
    var e = NewEngine([new() { Kind = "buffDurationTick", BuffId = 1, TickIntervalMs = 100, Increment = 10 }],
        new SlotConfig { ResetBuffId = 2, OnBuffAdd = "freeze" });
    e.BeginBatch(0); e.Buff(BuffEdge.Add, new(1, 1), 0); e.Buff(BuffEdge.Add, new(2, 2), 0); e.EndBatch();
    Equal(0u, Count(e));
});
Test("protobuf damage: lucky flags, ownership, absent/negative amounts, multi-target once", () =>
{
    var p = Protocol(20020231, 20021731);
    var hit = new SyncDamageInfo { OwnerId = 120902, DamageSource = (EDamageSource)3, Value = 10, TypeFlag = 5, AttackerUuid = p.LocalUuid };
    void Send(params (long target, SyncDamageInfo hit)[] hits)
    {
        var near = new WorldNtfCsharp.Types.SyncNearDeltaInfo();
        foreach (var h in hits)
        {
            var delta = new AoiSyncDelta { Uuid = h.target, SkillEffects = new() };
            delta.SkillEffects.Damages.Add(h.hit); near.DeltaInfos.Add(delta);
        }
        p.Notify(WorldNtf.SyncNearDeltaInfo, near.ToByteArray(), 1);
    }
    Send((1, hit), (2, hit)); Equal(44u, Count(p.Engine));
    hit.AttackerUuid = 3; hit.TopSummonerId = p.LocalUuid; Send((1, hit)); Equal(88u, Count(p.Engine));
    hit.TopSummonerId = 0; Send((1, hit)); Equal(88u, Count(p.Engine));
    hit.AttackerUuid = p.LocalUuid; hit.Value = -1; Send((1, hit)); Equal(88u, Count(p.Engine));
    hit.Value = 0; Send((1, hit)); Equal(88u, Count(p.Engine)); // zero omitted by the proto3 serializer = no amount
    hit.LuckyValue = 5; Send((1, hit)); Equal(132u, Count(p.Engine));
    hit.TypeFlag = 1; Send((1, hit)); Equal(132u, Count(p.Engine));
});
Test("protobuf incoming hits only count in to-me, exclude healing and self damage", () =>
{
    var p = Protocol(20020621, 20021731);
    var hit = new SyncDamageInfo { OwnerId = 55, Value = 10, AttackerUuid = 100, TypeFlag = 2 };
    var delta = new AoiSyncDelta { Uuid = p.LocalUuid, SkillEffects = new() }; delta.SkillEffects.Damages.Add(hit);
    var near = new WorldNtfCsharp.Types.SyncNearDeltaInfo(); near.DeltaInfos.Add(delta);
    p.Notify(WorldNtf.SyncNearDeltaInfo, near.ToByteArray(), 1); Equal(0u, Count(p.Engine));
    var mine = new WorldNtfCsharp.Types.SyncToMeDeltaInfo { DeltaInfo = new() { Uuid = p.LocalUuid, BaseDelta = delta } };
    p.Notify(WorldNtf.SyncToMeDeltaInfo, mine.ToByteArray(), 2); Equal(3u, Count(p.Engine));
    hit.Type = EDamageType.Heal; p.Notify(WorldNtf.SyncToMeDeltaInfo, mine.ToByteArray(), 3); Equal(3u, Count(p.Engine));
    hit.Type = default; hit.AttackerUuid = p.LocalUuid; p.Notify(WorldNtf.SyncToMeDeltaInfo, mine.ToByteArray(), 4); Equal(3u, Count(p.Engine));
});
Test("protobuf resources: values before layout, partial deltas, grade change retains layout", () =>
{
    ByteString Packed(params long[] values)
    {
        using var body = new MemoryStream(); using (var w = new CodedOutputStream(body, true)) { foreach (long x in values) w.WriteInt64(x); }
        using var outer = new MemoryStream(); using (var w = new CodedOutputStream(outer, true)) { w.WriteTag(10); w.WriteBytes(ByteString.CopyFrom(body.ToArray())); }
        return ByteString.CopyFrom(outer.ToArray());
    }
    var p = Protocol(20020171, 20021731);
    var delta = new AoiSyncDelta { Uuid = p.LocalUuid, Attrs = new() };
    delta.Attrs.Attrs.Add(new Attr { Id = 50002, RawData = Packed(10) });
    delta.Attrs.Attrs.Add(new Attr { Id = 50001, RawData = Packed(14001) });
    var mine = new WorldNtfCsharp.Types.SyncToMeDeltaInfo { DeltaInfo = new() { Uuid = p.LocalUuid, BaseDelta = delta } };
    p.Notify(WorldNtf.SyncToMeDeltaInfo, mine.ToByteArray(), 1); Equal(0u, Count(p.Engine));
    delta.Attrs.Attrs.RemoveAt(1); delta.Attrs.Attrs[0].RawData = Packed(5);
    p.Notify(WorldNtf.SyncToMeDeltaInfo, mine.ToByteArray(), 2); Equal(12u, Count(p.Engine));
    var dirty = new WorldNtfCsharp.Types.SyncContainerDirtyData { VData = new BufferStream
        { Buffer = ByteString.CopyFrom(AreaPatch(Obj(Ints(2), MapUpdate(2, Obj(Ints(1, 20021733)))))) } };
    p.Notify(WorldNtf.SyncContainerDirtyData, dirty.ToByteArray(), 3);
    delta.Attrs.Attrs[0].RawData = Packed(10); p.Notify(WorldNtf.SyncToMeDeltaInfo, mine.ToByteArray(), 4);
    delta.Attrs.Attrs[0].RawData = Packed(5); p.Notify(WorldNtf.SyncToMeDeltaInfo, mine.ToByteArray(), 5); Equal(12u, Count(p.Engine));
});
Test("NetCap: FrameUp variants and nested compression preserve outbound payloads", () =>
{
    var cap = new NetCap(); int calls = 0;
    var request = Skill(1714);
    cap.ProxyObserved += (id, bytes, extra) => { Check(bytes.SequenceEqual(request)); calls++; };
    var parse = cap.GetType().GetMethod("ParseFrameUp", BindingFlags.NonPublic | BindingFlags.Instance)!
        .CreateDelegate<Action<ReadOnlySpan<byte>, bool, DateTime>>(cap);
    byte[] Inner(ushort flags, bool compressed)
    {
        using var compressor = new ZstdSharp.Compressor();
        byte[] body = compressed ? compressor.Wrap(request).ToArray() : request;
        int header = flags == 2 ? 16 : 20;
        var frame = new byte[6 + header + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)frame.Length);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(flags | (compressed ? 0x8000 : 0)));
        BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(6), (ulong)EProxyServiceId.World);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(6 + header - 4), (uint)WorldProxy.UseSlot);
        body.CopyTo(frame, 6 + header); return frame;
    }
    parse(Join(new byte[4], Inner(1, false)), false, DateTime.UtcNow);
    parse(Join(new byte[4], Inner(2, false)), false, DateTime.UtcNow);
    using var zstd = new ZstdSharp.Compressor();
    var nested = Join(Inner(1, true), Inner(1, false));
    parse(Join(new byte[4], zstd.Wrap(nested).ToArray()), true, DateTime.UtcNow);
    Equal(4, calls);
});

Test("regression: omitted move-state value is zero and does not lose character sync", () =>
{
    var p = Protocol(20020001, 20021731);
    p.UseSlot(Skill(1714), 1);
    var mine = new WorldNtfCsharp.Types.SyncToMeDeltaInfo { DeltaInfo = new()
        { Uuid = p.LocalUuid, BaseDelta = new() { Uuid = p.LocalUuid, Attrs = new() } } };
    // Real server notifications may omit rawData for a zero integer value.
    mine.DeltaInfo.BaseDelta.Attrs.Attrs.Add(new Attr { Id = 71 });
    p.Notify(WorldNtf.SyncToMeDeltaInfo, mine.ToByteArray(), 2);
    Check(p.HasSnapshot); Check(p.Error == null); Equal(25u, Count(p.Engine));
    p.UseSlot(Skill(1714), 3); Equal(50u, Count(p.Engine));
});

Test("regression: omitted zero changes movement state instead of keeping a stale moving value", () =>
{
    var p = Protocol(20020071, 20021731); // stationary buff tick: +12 every second when attr 71 == 0
    var delta = new AoiSyncDelta { Uuid = p.LocalUuid, Attrs = new(), BuffEffect = new() };
    delta.Attrs.Attrs.Add(new Attr { Id = 71, RawData = ByteString.CopyFrom([1]) });
    var buff = new BuffEffect { HostUuid = p.LocalUuid, BuffUuid = 9 };
    buff.LogicEffect.Add(new BuffEffectLogicInfo { EffectType = EBuffEffectLogicPbType.BuffEffectAddBuff,
        RawData = new BuffInfo { BaseId = 3051081, BuffUuid = 9, Duration = 5000 }.ToByteString() });
    delta.BuffEffect.BuffEffects.Add(buff);
    var mine = new WorldNtfCsharp.Types.SyncToMeDeltaInfo { DeltaInfo = new() { Uuid = p.LocalUuid, BaseDelta = delta } };
    p.Notify(WorldNtf.SyncToMeDeltaInfo, mine.ToByteArray(), 1); Equal(0u, Count(p.Engine));
    delta.BuffEffect = null; delta.Attrs.Attrs[0].RawData = ByteString.Empty;
    p.Notify(WorldNtf.SyncToMeDeltaInfo, mine.ToByteArray(), 1001); Equal(12u, Count(p.Engine));
    delta.Attrs.Attrs[0].RawData = ByteString.CopyFrom([0]);
    p.Notify(WorldNtf.SyncToMeDeltaInfo, mine.ToByteArray(), 2001); Equal(24u, Count(p.Engine));
    Check(p.HasSnapshot);
});
Test("optional attribute decoding matches donor defaults and ignores absent vectors/arrays", () =>
{
    Equal(0L, Wire.OptionalVarint([])); Equal(0L, Wire.OptionalVarint([0]));
    Equal(300L, Wire.OptionalVarint([0xac, 0x02])); Equal(0L, Wire.OptionalVarint([0x80]));
    Check(Wire.OptionalPacked([]) == null); Check(Wire.OptionalPacked([0x0a, 0x04, 1]) == null);
    Check(Wire.OptionalPacked([0x0a, 1, 0x80]) == null); Check(Wire.OptionalPacked([0x12, 0]) == null);
    Equal(0, Wire.OptionalPacked([0x0a, 0])!.Length);
    Check(Wire.OptionalPacked([0x0a, 3, 1, 0xac, 0x02])!.SequenceEqual([1L, 300L]));
    Check(Wire.OptionalPosition([]) == null); Check(Wire.OptionalPosition([0x80]) == null);
    Check(Wire.OptionalPosition(new Zproto.Position { X = 5, Y = 5 }.ToByteArray()) == null);
    // Explicit zero coordinates are valid, unlike omitted coordinates.
    using var raw = new MemoryStream();
    using (var w = new CodedOutputStream(raw, true))
    {
        w.WriteTag(13); w.WriteFloat(0); w.WriteTag(21); w.WriteFloat(0); w.WriteTag(29); w.WriteFloat(0);
    }
    Equal(Vector3.Zero, Wire.OptionalPosition(raw.ToArray())!.Value);
});
Test("optional malformed local attributes do not discard factors or following skill events", () =>
{
    var p = Protocol(20020001, 20021731); p.UseSlot(Skill(1714), 1);
    var delta = new AoiSyncDelta { Uuid = p.LocalUuid, Attrs = new() };
    foreach (int id in new[] { 71, 52, 50001, 50002 })
    {
        delta.Attrs.Attrs.Add(new Attr { Id = id });
        delta.Attrs.Attrs.Add(new Attr { Id = id, RawData = ByteString.CopyFrom([0x80]) });
    }
    var mine = new WorldNtfCsharp.Types.SyncToMeDeltaInfo { DeltaInfo = new() { Uuid = p.LocalUuid, BaseDelta = delta } };
    p.Notify(WorldNtf.SyncToMeDeltaInfo, mine.ToByteArray(), 2); Check(p.HasSnapshot); Equal(25u, Count(p.Engine));
    p.UseSlot(Skill(1714), 3); Equal(50u, Count(p.Engine));
});
Test("omitted resource arrays keep layout and baseline until the next valid observation", () =>
{
    var p = Protocol(20020171, 20021731);
    var delta = new AoiSyncDelta { Uuid = p.LocalUuid, Attrs = new() };
    delta.Attrs.Attrs.Add(new Attr { Id = 50001, RawData = ByteString.CopyFrom([0x0a, 2, 0xb1, 0x6d]) }); // resource 14001
    delta.Attrs.Attrs.Add(new Attr { Id = 50002, RawData = ByteString.CopyFrom([0x0a, 1, 10]) });
    var mine = new WorldNtfCsharp.Types.SyncToMeDeltaInfo { DeltaInfo = new() { Uuid = p.LocalUuid, BaseDelta = delta } };
    p.Notify(WorldNtf.SyncToMeDeltaInfo, mine.ToByteArray(), 1);
    delta.Attrs.Attrs[0].RawData = ByteString.Empty; delta.Attrs.Attrs[1].RawData = ByteString.Empty;
    p.Notify(WorldNtf.SyncToMeDeltaInfo, mine.ToByteArray(), 2); Equal(0u, Count(p.Engine));
    delta.Attrs.Attrs[1].RawData = ByteString.CopyFrom([0x0a, 1, 5]);
    p.Notify(WorldNtf.SyncToMeDeltaInfo, mine.ToByteArray(), 3); Equal(12u, Count(p.Engine)); Check(p.HasSnapshot);
});
Test("corrupted outer notification still fails instead of being mistaken for an optional attribute", () =>
{
    var p = Protocol(20020001, 20021731); bool rejected = false;
    try { p.Notify(WorldNtf.SyncToMeDeltaInfo, [0x0a, 0x7f], 1); }
    catch (InvalidProtocolBufferException) { rejected = true; }
    Check(rejected);
});

Console.WriteLine($"\n{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
