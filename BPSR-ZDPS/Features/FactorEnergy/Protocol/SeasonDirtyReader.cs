using System.Buffers.Binary;
using Google.Protobuf.Collections;
using Zproto;

namespace BPSR_ZDPS.Features.FactorEnergy;

/// <summary>The game's dirty stream is not protobuf. Apply patches to a clone, preserving untouched map entries.</summary>
public sealed class SeasonDirtyReader
{
    readonly byte[] data;
    int offset, limit;
    SeasonDirtyReader(ReadOnlySpan<byte> bytes) { data = bytes.ToArray(); limit = data.Length; }

    public static bool Merge(ReadOnlySpan<byte> bytes, SeasonCultivateLineData baseline, out SeasonCultivateLineData merged)
    {
        var reader = new SeasonDirtyReader(bytes);
        var candidate = baseline.Clone();
        bool touched = false;
        reader.Object(field =>
        {
            if (field == 101) { reader.Season(candidate); touched = true; }
            else if (field == 1) reader.Skip(8); // CharSerialize.charId
            else if (reader.Peek() == -2) reader.Object(_ => reader.offset = reader.limit);
            else reader.offset = reader.limit;
        });
        if (reader.offset != reader.data.Length) throw new InvalidDataException("Trailing dirty-stream bytes.");
        merged = touched ? candidate : baseline;
        return touched;
    }

    void Require(int count)
    {
        if (count < 0 || count > limit - offset) throw new InvalidDataException("Truncated dirty stream.");
    }
    int Peek() { Require(4); return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)); }
    int Int() { int value = Peek(); offset += 4; return value; }
    void Skip(int size) { Require(size); offset += size; }
    bool Bool() { Require(1); return data[offset++] != 0; }
    int Count(int value)
    {
        if (value < 0 || value > (limit - offset) / 4) throw new InvalidDataException("Invalid dirty-stream count.");
        return value;
    }

    void Object(Action<int> field)
    {
        if (Int() != -2) throw new InvalidDataException("Invalid dirty-object marker.");
        int size = Int();
        if (size == -3) return;
        Require(size);
        int parentLimit = limit;
        int end = checked(offset + size);
        if (end > parentLimit - 4) throw new InvalidDataException("Missing dirty-object terminator.");
        limit = end;
        while (offset < end)
        {
            int id = Int();
            if (id <= 0) throw new InvalidDataException("Invalid dirty-object field.");
            field(id);
        }
        limit = parentLimit;
        if (Int() != -3) throw new InvalidDataException("Invalid dirty-object terminator.");
    }

    void Map<T>(MapField<int, T> map, Action<T> merge) where T : new()
    {
        int first = Int();
        if (first == -4) return;
        int updates = Count(first == -1 ? Int() : first);
        int removes = first == -1 ? 0 : Count(Int());
        int adds = first == -1 ? 0 : Count(Int());
        void Entries(int count)
        {
            for (int i = 0; i < count; i++)
            {
                int key = Int();
                if (!map.TryGetValue(key, out var entry)) map[key] = entry = new T();
                merge(entry);
            }
        }
        Entries(updates);
        for (int i = 0; i < removes; i++) map.Remove(Int());
        Entries(adds);
    }
    void Unknown(int field) => throw new InvalidDataException($"Unknown season dirty field: {field}.");
    void Season(SeasonCultivateLineData value) => Object(f =>
    {
        if (f == 1) Map(value.SeasonCultivateLineMap, Line); else Unknown(f);
    });
    void Line(CultivateLineData value) => Object(f =>
    {
        if (f == 1) Map(value.CultivateLineMap, SubType); else Unknown(f);
    });
    void SubType(CultivateLineSubTypeData value) => Object(f =>
    {
        if (f == 1) Map(value.CultivateLineDataMap, Area);
        else if (f == 2)
        {
            int count = Count(Int());
            value.CultivateLineAreaList.Clear();
            for (int i = 0; i < count; i++) value.CultivateLineAreaList.Add(Int());
        }
        else Unknown(f);
    });
    void Area(CultivateAreaData value) => Object(f =>
    {
        switch (f)
        {
            case 1: Map(value.CultivateNormalNodeMap, Normal); break;
            case 2: Map(value.CultivateMiddleNodeMap, Middle); break;
            case 3: Map(value.CultivateBigNodeMap, Big); break;
            case 4: value.ActivateEffectScore = Int(); break;
            case 5: value.IsActive = Bool(); break;
            default: Unknown(f); break;
        }
    });
    void Normal(CultivateNormalNodeData value) => Object(f => { if (f == 1) value.ActiveLevel = Int(); else Unknown(f); });
    void Middle(CultivateMiddleNodeData value) => Object(f => { if (f == 1) value.ItemId = Int(); else Unknown(f); });
    void Big(CultivateBigNodeData value) => Object(f => { if (f == 1) value.FantasyId = Int(); else Unknown(f); });
}
