using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace TranslocatorPath;

// ---- On-disk DTOs (System.Text.Json) -------------------------------------

public class TlGroupDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#35DDAC"; // #RRGGBB
    public bool Visible { get; set; } = true;
    // Persisted in the per-world file so chat-imported groups (which have no
    // backing drop-folder file to re-read) survive a reload/restart. Left at
    // default false in shared/export files; the receiver rebuilds import groups
    // from per-entry Origin, so the flag there is irrelevant.
    public bool Imported { get; set; }
}

public class TlEntryDto
{
    public int Sx { get; set; }
    public int Sy { get; set; }
    public int Sz { get; set; }
    public int Dx { get; set; }
    public int Dy { get; set; }
    public int Dz { get; set; }
    public string GroupId { get; set; } = TranslocatorStore.SelfGroupId;
    public string Origin { get; set; } = "";
}

public class TlSaveFile
{
    // Human-friendly header line written by Export; ignored on load.
    public string? Note { get; set; }
    public string SavegameId { get; set; } = "";
    public string Owner { get; set; } = "";
    // When all three are set, entry Sx/Sy/Sz and Dx/Dy/Dz are offsets from this
    // point (world spawn). Lets shared pastes use small readable numbers.
    public int? RefX { get; set; }
    public int? RefY { get; set; }
    public int? RefZ { get; set; }
    public List<TlGroupDto> Groups { get; set; } = new();
    public List<TlEntryDto> Entries { get; set; } = new();
}

// ---- Runtime model -------------------------------------------------------

public class TlGroup
{
    public string Id = "";
    public string Name = "";
    public bool Visible = true;
    public Vec4f Color = new(0.21f, 0.87f, 0.67f, 1f);
    public bool Imported;
}

public class TlEntry
{
    public BlockPos Src = null!;
    public BlockPos Dst = null!;
    public string GroupId = TranslocatorStore.SelfGroupId;
    public string Origin = "";
    public long ChunkIdx;
}

/// <summary>
/// Owns the translocator data: discovered + imported links, the groups they
/// belong to, per-world persistence, and the drop-folder share/import flow.
///
/// Layout under <c>%AppData%\VintagestoryData22\ModConfig\translocatorpath\</c>:
///   <c>world-&lt;savegameid&gt;.json</c>  this player's saved list for this world
///   <c>import\*.json</c>                 files from other players, auto-merged
///   <c>share\&lt;name&gt;.json</c>        export target you hand to others
/// </summary>
public class TranslocatorStore
{
    public const string SelfGroupId = "self";

    private readonly ICoreClientAPI _capi;
    private readonly object _lock = new();

    private readonly Dictionary<long, TlEntry> _entries = new();
    private readonly Dictionary<string, TlGroup> _groups = new();

    private string _baseDir = "";
    private string _importDir = "";
    private string _shareDir = "";
    private string _worldFile = "";
    private bool _dirty;
    private int _version;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] ImportPalette =
    {
        "#FF8C26", "#3FA0FF", "#E25BFF", "#FFE14B",
        "#7CFF5B", "#FF5B7A", "#26D7FF", "#C08CFF",
    };

    public TranslocatorStore(ICoreClientAPI capi) => _capi = capi;

    /// <summary>Monotonic change counter, bumped on every mutation that can
    /// affect a snapshot (entries, groups, visibility, colours). Callers that
    /// cache <see cref="SnapshotVisible"/> results compare against this to
    /// know when to re-fetch instead of snapshotting every frame.</summary>
    public int Version
    {
        get { lock (_lock) return _version; }
    }

    /// <summary>Mark state changed while holding <see cref="_lock"/>:
    /// schedules a save and invalidates cached snapshots.</summary>
    private void MarkDirtyLocked()
    {
        _dirty = true;
        _version++;
    }

    public static long PosKey(BlockPos p) =>
        ((long)p.X * 73856093) ^ ((long)p.Y * 19349663) ^ ((long)p.Z * 83492791);

    private static Vec4f ParseColor(string hex, float alpha = 1f)
    {
        try
        {
            hex = hex.TrimStart('#');
            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            return new Vec4f(r / 255f, g / 255f, b / 255f, alpha);
        }
        catch { return new Vec4f(0.21f, 0.87f, 0.67f, alpha); }
    }

    public static string ToHex(Vec4f c) =>
        $"{(int)(c.R * 255):X2}{(int)(c.G * 255):X2}{(int)(c.B * 255):X2}";

    public static Vec4f HexToColor(string hex) => ParseColor(hex);

    public static bool IsValidHex(string s)
    {
        s = s.TrimStart('#');
        return s.Length == 6 && s.All(Uri.IsHexDigit);
    }

    // 0x00RRGGBB layout (R in the high byte) to match ColorUtil /
    // GuiElementColorListPicker.
    public static int ColorToInt(Vec4f c) =>
        ((int)(c.R * 255) << 16) | ((int)(c.G * 255) << 8) | (int)(c.B * 255);

    public static Vec4f IntToColor(int v) =>
        new(((v >> 16) & 0xff) / 255f, ((v >> 8) & 0xff) / 255f, (v & 0xff) / 255f, 1f);

    private static string SafeId(string s) =>
        new(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    private string SavegameId => SafeId(_capi.World?.SavegameIdentifier ?? "unknown");
    private string PlayerName => _capi.World?.Player?.PlayerName ?? "player";

    // ---- lifecycle -------------------------------------------------------

    public void Init()
    {
        _baseDir = _capi.GetOrCreateDataPath("ModConfig/translocatorpath");
        _importDir = _capi.GetOrCreateDataPath("ModConfig/translocatorpath/import");
        _shareDir = _capi.GetOrCreateDataPath("ModConfig/translocatorpath/share");
        _worldFile = Path.Combine(_baseDir, $"world-{SavegameId}.json");

        EnsureSelfGroup();
        LoadWorldFile();
        ImportDropFolder();
    }

    private void EnsureSelfGroup()
    {
        if (!_groups.ContainsKey(SelfGroupId))
            _groups[SelfGroupId] = new TlGroup
            {
                Id = SelfGroupId, Name = "Discovered", Visible = true,
                Color = new Vec4f(0.21f, 0.87f, 0.67f, 1f), Imported = false,
            };
    }

    // ---- persistence -----------------------------------------------------

    private void LoadWorldFile()
    {
        if (!File.Exists(_worldFile)) return;
        try
        {
            var f = JsonSerializer.Deserialize<TlSaveFile>(File.ReadAllText(_worldFile));
            if (f == null) return;
            foreach (var g in f.Groups)
                _groups[g.Id] = new TlGroup
                {
                    Id = g.Id, Name = g.Name, Visible = g.Visible,
                    Color = ParseColor(g.Color), Imported = g.Imported,
                };
            EnsureSelfGroup();
            foreach (var e in f.Entries)
            {
                var src = new BlockPos(e.Sx, e.Sy, e.Sz);
                _entries[PosKey(src)] = new TlEntry
                {
                    Src = src,
                    Dst = new BlockPos(e.Dx, e.Dy, e.Dz),
                    GroupId = _groups.ContainsKey(e.GroupId) ? e.GroupId : SelfGroupId,
                    Origin = e.Origin ?? "",
                };
            }
        }
        catch (Exception ex)
        {
            _capi.Logger.Warning($"[translocatorpath] failed to read {_worldFile}: {ex.Message}");
        }
    }

    public void SaveIfDirty()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            WriteWorldFileLocked();
        }
    }

    /// <summary>Force a write even if not dirty (GUI "Save Now" button).</summary>
    public void ForceSave()
    {
        lock (_lock) WriteWorldFileLocked();
    }

    private void WriteWorldFileLocked()
    {
        _dirty = false;
        try
        {
            // Persist EVERYTHING, including imported groups/entries. Drop-folder
            // imports re-read harmlessly (dedup on key), but chat imports have no
            // file to re-read, so excluding them here is what made them vanish on
            // reload/restart.
            var f = new TlSaveFile
            {
                SavegameId = SavegameId,
                Owner = PlayerName,
                Groups = _groups.Values.Select(ToDto).ToList(),
                Entries = _entries.Values.Select(ToDto).ToList(),
            };
            File.WriteAllText(_worldFile, JsonSerializer.Serialize(f, JsonOpts));
        }
        catch (Exception ex)
        {
            _capi.Logger.Warning($"[translocatorpath] save failed: {ex.Message}");
        }
    }

    /// <summary>Discard in-memory state and reload from the world file +
    /// import folder. Live-scanned-but-unsaved entries are dropped; the layer
    /// should rescan afterwards to repopulate them.</summary>
    public void ReloadFromDisk()
    {
        lock (_lock)
        {
            _entries.Clear();
            _groups.Clear();
            EnsureSelfGroup();
            LoadWorldFile();
            _version++;
        }
        ImportDropFolder();
    }

    private bool IsImportedGroup(string gid) =>
        _groups.TryGetValue(gid, out var g) && g.Imported;

    private static TlGroupDto ToDto(TlGroup g) => new()
    {
        Id = g.Id, Name = g.Name, Visible = g.Visible, Color = "#" + ToHex(g.Color),
        Imported = g.Imported,
    };

    private static TlEntryDto ToDto(TlEntry e) => new()
    {
        Sx = e.Src.X, Sy = e.Src.Y, Sz = e.Src.Z,
        Dx = e.Dst.X, Dy = e.Dst.Y, Dz = e.Dst.Z,
        GroupId = e.GroupId, Origin = e.Origin,
    };

    // ---- discovery -------------------------------------------------------

    /// <summary>A translocator pair links both ways (A-&gt;B and B-&gt;A). The
    /// reverse is the entry keyed at <paramref name="dst"/> whose own Dst points
    /// back at <paramref name="src"/>. If it exists we already store this link
    /// canonically (from the other end) and must not add a mirror.</summary>
    private bool ReverseExistsLocked(BlockPos src, BlockPos dst)
    {
        return _entries.TryGetValue(PosKey(dst), out var rev)
               && rev.Dst.X == src.X && rev.Dst.Y == src.Y && rev.Dst.Z == src.Z;
    }

    public bool AddDiscovered(BlockPos src, BlockPos dst, long chunkIdx)
    {
        lock (_lock)
        {
            long key = PosKey(src);
            if (_entries.TryGetValue(key, out var ex))
            {
                ex.ChunkIdx = chunkIdx;
                ex.Dst = dst;
                _version++; // Dst may have changed; not persisted-worthy but snapshots must refresh
                return false;
            }
            // Already stored from the far end as B->A — keep just the one.
            if (ReverseExistsLocked(src, dst)) return false;

            _entries[key] = new TlEntry
            {
                Src = src, Dst = dst, GroupId = SelfGroupId, Origin = "", ChunkIdx = chunkIdx,
            };
            MarkDirtyLocked();
            return true;
        }
    }

    public void EvictChunk(long chunkIdx)
    {
        lock (_lock)
        {
            var gone = _entries.Where(kv => kv.Value.ChunkIdx == chunkIdx
                                            && string.IsNullOrEmpty(kv.Value.Origin))
                                .Select(kv => kv.Key).ToList();
            foreach (var k in gone) _entries.Remove(k);
            if (gone.Count > 0) _version++;
        }
    }

    // ---- import / export -------------------------------------------------

    public (int files, int added, int skippedWorld) ImportDropFolder()
    {
        int files = 0, added = 0, skippedWorld = 0;
        if (!Directory.Exists(_importDir)) return (0, 0, 0);

        lock (_lock)
        {
            foreach (var path in Directory.EnumerateFiles(_importDir, "*.json"))
            {
                TlSaveFile? f;
                try { f = JsonSerializer.Deserialize<TlSaveFile>(File.ReadAllText(path)); }
                catch (Exception ex)
                {
                    _capi.Logger.Warning($"[translocatorpath] bad import {Path.GetFileName(path)}: {ex.Message}");
                    continue;
                }
                if (f == null) continue;
                if (!string.IsNullOrEmpty(f.SavegameId) && f.SavegameId != SavegameId)
                {
                    skippedWorld++;
                    continue;
                }
                files++;
                added += IngestSaveFileLocked(f, Path.GetFileNameWithoutExtension(path));
            }
        }
        return (files, added, skippedWorld);
    }

    /// <summary>Parse a shared paste body and merge it. Returns (added, error)
    /// where error is null on success. Safe to call off the main thread.</summary>
    public (int added, string? error) IngestText(string json, string fallbackOwner)
    {
        TlSaveFile? f;
        try { f = JsonSerializer.Deserialize<TlSaveFile>(json); }
        catch (Exception ex) { return (0, "bad JSON: " + ex.Message); }
        if (f == null) return (0, "empty payload");
        if (!string.IsNullOrEmpty(f.SavegameId) && f.SavegameId != SavegameId)
            return (0, "different world");

        lock (_lock)
        {
            int added = IngestSaveFileLocked(f, fallbackOwner);
            if (added > 0) MarkDirtyLocked();
            return (added, null);
        }
    }

    private int IngestSaveFileLocked(TlSaveFile f, string fallbackOwner)
    {
        int added = 0;
        bool relative = f.RefX.HasValue && f.RefY.HasValue && f.RefZ.HasValue;
        int rx = f.RefX ?? 0, ry = f.RefY ?? 0, rz = f.RefZ ?? 0;
        string fileOwner = string.IsNullOrWhiteSpace(f.Owner)
            ? fallbackOwner : f.Owner.Trim();

        foreach (var e in f.Entries)
        {
            var src = new BlockPos(
                relative ? e.Sx + rx : e.Sx,
                relative ? e.Sy + ry : e.Sy,
                relative ? e.Sz + rz : e.Sz);
            var idst = new BlockPos(
                relative ? e.Dx + rx : e.Dx,
                relative ? e.Dy + ry : e.Dy,
                relative ? e.Dz + rz : e.Dz);
            long key = PosKey(src);
            if (_entries.TryGetValue(key, out var cur))
            {
                if (IsImportedGroup(cur.GroupId)) cur.Dst = idst;
                continue;
            }
            // Don't add a mirror of a link we already hold the other way.
            if (ReverseExistsLocked(src, idst)) continue;

            // Per-entry Origin wins over the file's owner, so when someone
            // re-shares entries they themselves imported, each entry stays
            // attributed back to whoever originally discovered it.
            string entryOwner = string.IsNullOrWhiteSpace(e.Origin)
                ? fileOwner : e.Origin.Trim();
            string entryGid = EnsureImportGroupLocked(entryOwner);

            _entries[key] = new TlEntry
            {
                Src = src, Dst = idst,
                GroupId = entryGid, Origin = entryOwner,
            };
            added++;
        }
        // Drop-folder imports don't mark dirty (they re-read every session),
        // but cached snapshots still need to see the new entries.
        if (added > 0) _version++;
        return added;
    }

    private string EnsureImportGroupLocked(string ownerName)
    {
        string gid = "import:" + SafeId(ownerName);
        if (!_groups.ContainsKey(gid))
        {
            string hex = ImportPalette[_groups.Count(g => g.Value.Imported) % ImportPalette.Length];
            _groups[gid] = new TlGroup
            {
                Id = gid, Name = ownerName, Visible = true,
                Color = ParseColor(hex), Imported = true,
            };
        }
        return gid;
    }

    private (int x, int y, int z) WorldSpawnInt()
    {
        var sp = _capi.World?.DefaultSpawnPosition;
        return sp == null ? (0, 0, 0) : ((int)sp.X, (int)sp.Y, (int)sp.Z);
    }

    /// <summary>Build the JSON body that goes to a share file or a paste.rs upload.
    /// Coordinates are written as offsets from world spawn so the paste reads with
    /// small spawn-relative numbers a human can eyeball. Imported entries are
    /// included with their original Origin so attribution survives a forward.</summary>
    public string BuildExportText()
    {
        lock (_lock)
        {
            var (rx, ry, rz) = WorldSpawnInt();
            var f = new TlSaveFile
            {
                Note = $"TranslocatorPath share from {PlayerName}. " +
                       $"Coordinates are relative to world spawn at ({rx}, {ry}, {rz}).",
                SavegameId = SavegameId,
                Owner = PlayerName,
                RefX = rx, RefY = ry, RefZ = rz,
                Groups = _groups.Values.Where(g => !g.Imported).Select(ToDto).ToList(),
                Entries = _entries.Values.Select(e => ToRelativeDto(e, rx, ry, rz)).ToList(),
            };
            return JsonSerializer.Serialize(f, JsonOpts);
        }
    }

    private static TlEntryDto ToRelativeDto(TlEntry e, int rx, int ry, int rz) => new()
    {
        Sx = e.Src.X - rx, Sy = e.Src.Y - ry, Sz = e.Src.Z - rz,
        Dx = e.Dst.X - rx, Dy = e.Dst.Y - ry, Dz = e.Dst.Z - rz,
        GroupId = e.GroupId, Origin = e.Origin,
    };

    public string Export()
    {
        string body = BuildExportText();
        string outPath = Path.Combine(_shareDir, $"translocators-{SafeId(PlayerName)}-{SavegameId}.json");
        File.WriteAllText(outPath, body);
        return outPath;
    }

    /// <summary>Export just one group (its definition + only its entries) to
    /// its own share file. Recipients drop it in their import folder and it
    /// merges as that single group. Returns the path, or null if the group is
    /// unknown.</summary>
    public string? ExportGroup(string gid)
    {
        lock (_lock)
        {
            if (!_groups.TryGetValue(gid, out var g)) return null;
            var (rx, ry, rz) = WorldSpawnInt();
            var f = new TlSaveFile
            {
                Note = $"TranslocatorPath group '{g.Name}' from {PlayerName}. " +
                       $"Coordinates are relative to world spawn at ({rx}, {ry}, {rz}).",
                SavegameId = SavegameId,
                Owner = PlayerName,
                RefX = rx, RefY = ry, RefZ = rz,
                Groups = new() { ToDto(g) },
                Entries = _entries.Values.Where(e => e.GroupId == gid)
                                          .Select(e => ToRelativeDto(e, rx, ry, rz))
                                          .ToList(),
            };
            string outPath = Path.Combine(_shareDir,
                $"translocators-{SafeId(PlayerName)}-{SavegameId}-{SafeId(g.Name)}.json");
            File.WriteAllText(outPath, JsonSerializer.Serialize(f, JsonOpts));
            return outPath;
        }
    }

    public string ImportDirPath => _importDir;
    public string ShareDirPath => _shareDir;

    // ---- group management ------------------------------------------------

    public IReadOnlyList<TlGroup> Groups
    {
        get { lock (_lock) return _groups.Values.OrderBy(g => g.Name).ToList(); }
    }

    public TlGroup? GroupById(string id)
    {
        lock (_lock) return _groups.TryGetValue(id, out var g) ? g : null;
    }

    /// <summary>Set a group's colour by id (preserves its alpha). Used by the
    /// colour-picker dialogs.</summary>
    public bool SetGroupColor(string gid, Vec4f col)
    {
        lock (_lock)
        {
            if (!_groups.TryGetValue(gid, out var g)) return false;
            g.Color = new Vec4f(col.R, col.G, col.B, g.Color.A);
            MarkDirtyLocked();
            return true;
        }
    }

    public bool RenameGroupById(string gid, string newName)
    {
        lock (_lock)
        {
            if (!_groups.TryGetValue(gid, out var g) || string.IsNullOrWhiteSpace(newName))
                return false;
            g.Name = newName.Trim();
            MarkDirtyLocked();
            return true;
        }
    }

    /// <summary>Resolve the entry whose source-pos hashes to <paramref name="key"/>
    /// (for the map click-to-edit dialog).</summary>
    public EntryRow? EntryByKey(long key)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(key, out var e)
                ? new EntryRow(key, e.Src, e.Dst, e.GroupId, e.Origin)
                : null;
        }
    }

    public int CountIn(string gid)
    {
        lock (_lock) return _entries.Values.Count(e => e.GroupId == gid);
    }

    public TlGroup? FindGroupByName(string name)
    {
        lock (_lock) return FindGroupByNameLocked(name);
    }

    public TlGroup AddGroup(string name, string? hex)
    {
        lock (_lock)
        {
            string gid = "g:" + SafeId(name) + ":" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var g = new TlGroup
            {
                Id = gid, Name = name, Visible = true,
                Color = ParseColor(hex ?? "#35DDAC"), Imported = false,
            };
            _groups[gid] = g;
            MarkDirtyLocked();
            return g;
        }
    }

    public bool RecolorGroup(string name, string hex)
    {
        lock (_lock)
        {
            var g = FindGroupByNameLocked(name);
            if (g == null) return false;
            g.Color = ParseColor(hex, g.Color.A);
            MarkDirtyLocked();
            return true;
        }
    }

    public bool RenameGroup(string oldName, string newName)
    {
        lock (_lock)
        {
            var g = FindGroupByNameLocked(oldName);
            if (g == null) return false;
            g.Name = newName;
            MarkDirtyLocked();
            return true;
        }
    }

    public bool ToggleGroup(string name, out bool nowVisible)
    {
        nowVisible = false;
        lock (_lock)
        {
            var g = FindGroupByNameLocked(name);
            if (g == null) return false;
            g.Visible = !g.Visible;
            nowVisible = g.Visible;
            MarkDirtyLocked();
            return true;
        }
    }

    public void SetGroupVisible(string gid, bool visible)
    {
        lock (_lock)
        {
            if (_groups.TryGetValue(gid, out var g)) { g.Visible = visible; MarkDirtyLocked(); }
        }
    }

    public bool DeleteGroup(string name)
    {
        lock (_lock)
        {
            var g = FindGroupByNameLocked(name);
            if (g == null || g.Id == SelfGroupId) return false;
            foreach (var e in _entries.Values.Where(e => e.GroupId == g.Id))
                e.GroupId = SelfGroupId;
            _groups.Remove(g.Id);
            MarkDirtyLocked();
            return true;
        }
    }

    private TlGroup? FindGroupByNameLocked(string name) =>
        _groups.Values.FirstOrDefault(
            g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    public (bool ok, BlockPos? src) AssignNearest(Vec3d near, string groupName, double maxDist)
    {
        lock (_lock)
        {
            var g = FindGroupByNameLocked(groupName);
            if (g == null) return (false, null);

            TlEntry? best = null;
            double bestD2 = maxDist * maxDist;
            foreach (var e in _entries.Values)
            {
                double dx = e.Src.X + 0.5 - near.X;
                double dy = e.Src.Y - near.Y;
                double dz = e.Src.Z + 0.5 - near.Z;
                double d2 = dx * dx + dy * dy + dz * dz;
                if (d2 <= bestD2) { bestD2 = d2; best = e; }
            }
            if (best == null) return (false, null);
            best.GroupId = g.Id;
            best.Origin = "";
            MarkDirtyLocked();
            return (true, best.Src);
        }
    }

    /// <summary>Move one specific entry (by source-pos key) to a group.
    /// Used by the GUI per-row dropdown.</summary>
    public bool ReassignEntry(long key, string groupId)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var e)) return false;
            if (!_groups.ContainsKey(groupId)) return false;
            e.GroupId = groupId;
            if (!IsImportedGroup(groupId)) e.Origin = ""; // now player-owned
            MarkDirtyLocked();
            return true;
        }
    }

    // ---- snapshots for render / GUI -------------------------------------

    public readonly struct RenderItem
    {
        public readonly BlockPos Src;
        public readonly BlockPos Dst;
        public readonly Vec4f Color;
        public readonly string GroupName;
        public readonly string Origin;
        public RenderItem(BlockPos s, BlockPos d, Vec4f c, string gn, string o)
        { Src = s; Dst = d; Color = c; GroupName = gn; Origin = o; }
    }

    public List<RenderItem> SnapshotVisible()
    {
        lock (_lock)
        {
            var outl = new List<RenderItem>(_entries.Count);
            foreach (var e in _entries.Values)
            {
                if (!_groups.TryGetValue(e.GroupId, out var g)) g = _groups[SelfGroupId];
                if (!g.Visible) continue;
                outl.Add(new RenderItem(e.Src, e.Dst, g.Color, g.Name, e.Origin));
            }
            return outl;
        }
    }

    public readonly struct EntryRow
    {
        public readonly long Key;
        public readonly BlockPos Src;
        public readonly BlockPos Dst;
        public readonly string GroupId;
        public readonly string Origin;
        public EntryRow(long k, BlockPos s, BlockPos d, string g, string o)
        { Key = k; Src = s; Dst = d; GroupId = g; Origin = o; }
    }

    /// <summary>All entries (for the GUI list), sorted by group then distance
    /// from <paramref name="near"/> so nearby ones surface first.</summary>
    public List<EntryRow> SnapshotEntries(Vec3d near)
    {
        lock (_lock)
        {
            return _entries
                .OrderBy(kv => kv.Value.GroupId)
                .ThenBy(kv =>
                {
                    double dx = kv.Value.Src.X - near.X;
                    double dz = kv.Value.Src.Z - near.Z;
                    return dx * dx + dz * dz;
                })
                .Select(kv => new EntryRow(kv.Key, kv.Value.Src, kv.Value.Dst,
                                           kv.Value.GroupId, kv.Value.Origin))
                .ToList();
        }
    }

    public int TotalCount { get { lock (_lock) return _entries.Count; } }
}
