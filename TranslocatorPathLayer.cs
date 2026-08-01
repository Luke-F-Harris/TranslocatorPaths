using System;
using System.Collections.Generic;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace TranslocatorPath;

/// <summary>
/// Client-side world-map layer. Scans loaded chunks for repaired
/// <see cref="BlockEntityStaticTranslocator"/>s, hands them to the shared
/// <see cref="TranslocatorStore"/>, and draws every visible entry as a line
/// in its group's colour from the translocator to its exit.
///
/// Scan cache: a chunk is scanned at most once (<see cref="_scannedChunks"/>),
/// so a translocator repaired after its chunk was first scanned won't appear
/// until a RescanCurrentChunk / RescanAll re-examines it.
///
/// Per-group visibility is handled inside this single layer (VS's map layer
/// list only supports statically registered layers); the "Translocators"
/// checkbox is the master switch.
/// </summary>
public class TranslocatorPathLayer : MapLayer
{
    private readonly ICoreClientAPI? _capi;

    private readonly HashSet<long> _scannedChunks = new();
    private readonly object _scanLock = new();

    private long _scanListenerId;

    // Reused per-batch for the two colour uniforms.
    private readonly Vec4f _lineCol = new();
    private readonly Vec4f _endCol = new();

    // Snapshot cache: SnapshotVisible() takes the store lock and allocates a
    // full list; the store's Version counter tells us when the data actually
    // changed, so we only re-snapshot then.
    private List<TranslocatorStore.RenderItem>? _snapshot;
    private int _snapshotVersion = -1;

    /// <summary>Screen-space endpoint positions captured during Render, so
    /// the hover / right-click hit tests reuse this frame's projections
    /// instead of re-projecting every entry per event.</summary>
    private struct HitPoint
    {
        public float X, Y;
        public int ItemIndex; // into _renderedItems
        public bool IsSrc;
    }

    private readonly List<HitPoint> _hitPoints = new();
    private List<TranslocatorStore.RenderItem> _renderedItems = new();

    /// <summary>One mesh pair per group colour: every line quad of that colour
    /// in one mesh, every endpoint marker in another. A frame then renders in
    /// 2 draw calls per distinct colour instead of 3 per translocator.
    /// Vertices are written in final screen space, so no per-quad model
    /// matrix is needed.</summary>
    private sealed class ColorBatch
    {
        // xyz + uv, no normals/rgba/flags - same vertex layout the previous
        // QuadMeshUtil.GetQuad() single-quad path fed the Gui shader.
        public readonly MeshData Lines = new(64, 96, false, true, false, false);
        public readonly MeshData Markers = new(64, 96, false, true, false, false);
        public MeshRef? LinesRef;
        public MeshRef? MarkersRef;
        public int LinesCapacity;
        public int MarkersCapacity;
        public readonly Vec4f Rgb = new();
        public bool Used;

        public void DisposeRefs()
        {
            LinesRef?.Dispose(); LinesRef = null; LinesCapacity = 0;
            MarkersRef?.Dispose(); MarkersRef = null; MarkersCapacity = 0;
        }
    }

    private readonly Dictionary<int, ColorBatch> _batches = new();
    private readonly List<int> _deadBatches = new();

    public static float ScanIntervalSec = 3f;
    public static int MaxLinks = 2000;
    public static float LineThicknessPx = 2.5f;
    public static float MarkerSizePx = 7f;

    public TranslocatorPathLayer(ICoreAPI api, IWorldMapManager mapSink) : base(api, mapSink)
    {
        _capi = api as ICoreClientAPI;
    }

    public override string Title => "Translocators";
    public override string LayerGroupCode => "translocatorpath";
    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;
    public override bool RequireChunkLoaded => false;

    private static TranslocatorStore? Store => TranslocatorPathModSystem.Store;

    public override void OnLoaded()
    {
        if (_capi == null) return;
        _scanListenerId = _capi.Event.RegisterGameTickListener(
            _ => ScanNewChunks(), Math.Max(250, (int)(ScanIntervalSec * 1000)));
        ScanNewChunks();
    }

    public override void OnMapOpenedClient()
    {
        // Drop any pan left over from the previous time the map was open;
        // the map element may have been rebuilt since.
        _panMap = null;
        _panTarget = null;

        Store?.ImportDropFolder();
        ScanNewChunks();
    }

    public int ScannedChunkCount { get { lock (_scanLock) return _scannedChunks.Count; } }

    public void RestartScanTimer()
    {
        if (_capi == null) return;
        if (_scanListenerId != 0) _capi.Event.UnregisterGameTickListener(_scanListenerId);
        _scanListenerId = _capi.Event.RegisterGameTickListener(
            _ => ScanNewChunks(), Math.Max(250, (int)(ScanIntervalSec * 1000)));
    }

    public void RescanAll()
    {
        lock (_scanLock) _scannedChunks.Clear();
        ScanNewChunks();
    }

    public bool RescanCurrentChunk()
    {
        if (_capi?.World?.Player?.Entity == null) return false;
        if (!TryGetCurrentChunkIndex(out long idx)) return false;
        lock (_scanLock) _scannedChunks.Remove(idx);
        Store?.EvictChunk(idx);
        ScanNewChunks();
        return true;
    }

    private bool TryGetCurrentChunkIndex(out long idx)
    {
        idx = 0;
        var ba = _capi?.World?.BlockAccessor;
        var pos = _capi?.World?.Player?.Entity?.Pos;
        if (ba == null || pos == null) return false;
        int cs = GlobalConstants.ChunkSize;
        long sizeXc = ba.MapSizeX / cs;
        long sizeZc = ba.MapSizeZ / cs;
        idx = MapUtil.Index3dL((int)pos.X / cs, (int)pos.Y / cs, (int)pos.Z / cs, sizeXc, sizeZc);
        return true;
    }

    private void ScanNewChunks()
    {
        if (_capi?.World == null) return;
        var store = Store;
        if (store == null) return;
        var ba = _capi.World.BlockAccessor;

        long[] indices;
        try { indices = _capi.World.LoadedChunkIndices; }
        catch { return; }

        // Claim all unscanned chunks in ONE lock acquisition. The previous
        // per-chunk lock meant a steady-state tick (everything already
        // scanned) still took the lock once per loaded chunk.
        List<long>? fresh = null;
        lock (_scanLock)
        {
            foreach (long idx in indices)
                if (_scannedChunks.Add(idx)) (fresh ??= new List<long>()).Add(idx);
        }
        if (fresh == null) return;

        // Track the entry count locally: store.TotalCount takes the store
        // lock, and the previous code queried it once per block entity.
        int total = store.TotalCount;

        for (int i = 0; i < fresh.Count; i++)
        {
            if (total >= MaxLinks) { UnclaimFrom(fresh, i); return; }

            var bes = ba.GetChunk(fresh[i])?.BlockEntities;
            if (bes == null || bes.Count == 0) continue;

            foreach (var be in bes.Values)
            {
                if (be is not BlockEntityStaticTranslocator tl) continue;
                if (!tl.FullyRepaired) continue;

                var dst = tl.TargetLocation;
                if (dst == null) continue;
                var src = tl.Pos;
                if (src == null) continue;
                if (tl.tpLocationIsOffset) dst = dst.AddCopy(src.X, src.Y, src.Z);

                if (total >= MaxLinks) { UnclaimFrom(fresh, i + 1); return; }
                if (store.AddDiscovered(src.Copy(), dst.Copy(), fresh[i])) total++;
            }
        }
    }

    /// <summary>Give back chunks that were claimed up-front but never scanned
    /// because the MaxLinks cap was hit, so a later tick (or a raised cap via
    /// .tllinesmax) can still pick them up.</summary>
    private void UnclaimFrom(List<long> fresh, int start)
    {
        if (start >= fresh.Count) return;
        lock (_scanLock)
            for (int i = start; i < fresh.Count; i++) _scannedChunks.Remove(fresh[i]);
    }

    /// <summary>The current visible-entry snapshot, re-fetched from the store
    /// only when its Version says something changed.</summary>
    private List<TranslocatorStore.RenderItem> VisibleItems(TranslocatorStore store)
    {
        int v = store.Version;
        if (_snapshot == null || v != _snapshotVersion)
        {
            _snapshot = store.SnapshotVisible();
            _snapshotVersion = v;
        }
        return _snapshot;
    }

    public override void Render(GuiElementMap map, float dt)
    {
        if (_capi == null || !Active) return;

        AdvancePan(map, dt);

        var store = Store;
        if (store == null) return;

        var items = VisibleItems(store);
        _renderedItems = items;
        _hitPoints.Clear();
        if (items.Count == 0) return;

        var api = map.Api;

        // ---- CPU pass: fill one mesh pair per distinct group colour --------
        foreach (var b in _batches.Values)
        {
            b.Used = false;
            b.Lines.Clear();
            b.Markers.Clear();
        }

        var aPos = new Vec2f();
        var bPos = new Vec2f();
        var srcWorld = new Vec3d();
        var dstWorld = new Vec3d();

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            srcWorld.Set(item.Src.X + 0.5, item.Src.Y, item.Src.Z + 0.5);
            dstWorld.Set(item.Dst.X + 0.5, item.Dst.Y, item.Dst.Z + 0.5);
            map.TranslateWorldPosToViewPos(srcWorld, ref aPos);
            map.TranslateWorldPosToViewPos(dstWorld, ref bPos);

            float ax = (float)(map.Bounds.renderX + aPos.X);
            float ay = (float)(map.Bounds.renderY + aPos.Y);
            float bx = (float)(map.Bounds.renderX + bPos.X);
            float by = (float)(map.Bounds.renderY + bPos.Y);

            if (OffSameSide(ax, bx, map.Bounds.renderX, map.Bounds.renderX + map.Bounds.InnerWidth) ||
                OffSameSide(ay, by, map.Bounds.renderY, map.Bounds.renderY + map.Bounds.InnerHeight))
                continue;

            // Culled endpoints are off-screen and can't be hovered/clicked,
            // so recording only surviving items keeps the hit list small.
            _hitPoints.Add(new HitPoint { X = ax, Y = ay, ItemIndex = i, IsSrc = true });
            _hitPoints.Add(new HitPoint { X = bx, Y = by, ItemIndex = i, IsSrc = false });

            float dx = bx - ax;
            float dy = by - ay;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.001f) continue;
            float ang = (float)Math.Atan2(dy, dx);

            int colKey = TranslocatorStore.ColorToInt(item.Color);
            if (!_batches.TryGetValue(colKey, out var batch))
            {
                batch = new ColorBatch();
                batch.Rgb.Set(item.Color.R, item.Color.G, item.Color.B, 1f);
                _batches[colKey] = batch;
            }
            batch.Used = true;

            AddQuad(batch.Lines, (ax + bx) / 2f, (ay + by) / 2f, ang,
                len / 2f, LineThicknessPx / 2f);
            AddQuad(batch.Markers, ax, ay, 0f, MarkerSizePx / 2f, MarkerSizePx / 2f);
            AddQuad(batch.Markers, bx, by, 0f, MarkerSizePx / 3f, MarkerSizePx / 3f);
        }

        // ---- GPU pass: upload + draw each used batch -----------------------
        IShaderProgram prog = api.Render.GetEngineShader(EnumShaderProgram.Gui);
        prog.Uniform("extraGlow", 0);
        prog.Uniform("applyColor", 0);
        prog.Uniform("noTexture", 1f);
        prog.UniformMatrix("projectionMatrix", api.Render.CurrentProjectionMatrix);
        prog.UniformMatrix("modelViewMatrix", api.Render.CurrentModelviewMatrix);

        api.Render.PushScissor(map.Bounds, true);

        _deadBatches.Clear();
        foreach (var kv in _batches)
        {
            var b = kv.Value;
            if (!b.Used)
            {
                // Colour vanished (group recoloured/hidden); free its buffers.
                b.DisposeRefs();
                _deadBatches.Add(kv.Key);
                continue;
            }
            if (b.Lines.VerticesCount == 0) continue;

            b.LinesRef = UploadOrUpdate(b.LinesRef, b.Lines, ref b.LinesCapacity);
            b.MarkersRef = UploadOrUpdate(b.MarkersRef, b.Markers, ref b.MarkersCapacity);

            _lineCol.Set(b.Rgb.R, b.Rgb.G, b.Rgb.B, 0.55f);
            prog.Uniform("rgbaIn", _lineCol);
            api.Render.RenderMesh(b.LinesRef);

            _endCol.Set(b.Rgb.R, b.Rgb.G, b.Rgb.B, 1f);
            prog.Uniform("rgbaIn", _endCol);
            api.Render.RenderMesh(b.MarkersRef);
        }
        foreach (int key in _deadBatches) _batches.Remove(key);

        api.Render.PopScissor();
    }

    /// <summary>Append a rotated rectangle (centre, angle, half extents) to
    /// <paramref name="mesh"/> as 4 screen-space vertices + 2 triangles.
    /// z=60 matches the depth the old per-quad model matrix translated to.</summary>
    private static void AddQuad(MeshData mesh, float cx, float cy, float angle,
        float halfW, float halfH)
    {
        float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
        float axx = cos * halfW, axy = sin * halfW;   // half vector along the quad
        float pxx = -sin * halfH, pxy = cos * halfH;  // half vector across the quad

        int v = mesh.VerticesCount;
        mesh.AddVertex(cx - axx - pxx, cy - axy - pxy, 60f, 0f, 0f);
        mesh.AddVertex(cx + axx - pxx, cy + axy - pxy, 60f, 1f, 0f);
        mesh.AddVertex(cx + axx + pxx, cy + axy + pxy, 60f, 1f, 1f);
        mesh.AddVertex(cx - axx + pxx, cy - axy + pxy, 60f, 0f, 1f);
        mesh.AddIndex(v); mesh.AddIndex(v + 1); mesh.AddIndex(v + 2);
        mesh.AddIndex(v); mesh.AddIndex(v + 2); mesh.AddIndex(v + 3);
    }

    /// <summary>Upload the mesh data, reusing the existing GPU buffer via
    /// UpdateMesh while the data still fits and re-allocating when it grew
    /// past what was uploaded.</summary>
    private MeshRef UploadOrUpdate(MeshRef? mref, MeshData data, ref int capacity)
    {
        if (mref == null || data.VerticesCount > capacity)
        {
            mref?.Dispose();
            capacity = data.VerticesCount;
            return _capi!.Render.UploadMesh(data);
        }
        _capi!.Render.UpdateMesh(mref, data);
        return mref;
    }

    private static bool OffSameSide(double a, double b, double lo, double hi)
    {
        const double m = 8;
        return (a < lo - m && b < lo - m) || (a > hi + m && b > hi + m);
    }

    /// <summary>Find the hit-cache entry under the cursor, if any. Uses the
    /// screen positions Render already computed this frame instead of
    /// re-projecting every entry per mouse event.</summary>
    private bool TryHit(double mx, double my, out TranslocatorStore.RenderItem item, out bool isSrc)
    {
        item = default;
        isSrc = false;
        double hit = RuntimeEnv.GUIScale * 6;
        for (int i = 0; i < _hitPoints.Count; i++)
        {
            var p = _hitPoints[i];
            if (Math.Abs(mx - p.X) >= hit || Math.Abs(my - p.Y) >= hit) continue;
            if (p.ItemIndex >= _renderedItems.Count) continue; // stale frame guard
            item = _renderedItems[p.ItemIndex];
            isSrc = p.IsSrc;
            return true;
        }
        return false;
    }

    public override void OnMouseMoveClient(MouseEvent args, GuiElementMap map, StringBuilder hoverText)
    {
        if (_capi == null || !Active) return;
        if (!TryHit(args.X, args.Y, out var item, out _)) return;

        var spawn = _capi.World.DefaultSpawnPosition.AsBlockPos;
        var s = item.Src; var d = item.Dst;
        string who = string.IsNullOrEmpty(item.Origin) ? "you" : item.Origin;
        hoverText.AppendLine(
            $"[{item.GroupName}] (via {who})\n" +
            $"Translocator {s.X - spawn.X}, {s.Y}, {s.Z - spawn.Z}\n" +
            $"  -> {d.X - spawn.X}, {d.Y}, {d.Z - spawn.Z}");
    }

    private TranslocatorPathEditDialog? _editDlg;

    // ---- click-to-pan ------------------------------------------------------

    /// <summary>Smooth-pan state. Scoped to the exact GuiElementMap that
    /// initiated it: the world-map dialog and the HUD minimap are separate
    /// map elements rendering the same layers, and a pan on one must never
    /// move the other.</summary>
    private GuiElementMap? _panMap;
    private BlockPos? _panTarget;

    /// <summary>Start smoothly panning <paramref name="map"/> until
    /// <paramref name="target"/> is centred. Advanced each frame in Render;
    /// cancelled the moment the player drags the map themselves.</summary>
    public void BeginPanTo(GuiElementMap map, BlockPos target)
    {
        _panMap = map;
        _panTarget = target;
    }

    private void AdvancePan(GuiElementMap map, float dt)
    {
        if (_panTarget == null || !ReferenceEquals(_panMap, map)) return;
        if (map.IsDragingMap) { _panMap = null; _panTarget = null; return; }

        var b = map.CurrentBlockViewBounds;
        double dx = _panTarget.X + 0.5 - (b.X1 + b.X2) / 2.0;
        double dz = _panTarget.Z + 0.5 - (b.Z1 + b.Z2) / 2.0;

        // Within a block of centre: snap exact and finish.
        if (dx * dx + dz * dz < 1)
        {
            map.CenterMapTo(_panTarget);
            _panMap = null;
            _panTarget = null;
            return;
        }

        // Framerate-independent ease-out: close ~99.8% of the remaining
        // distance per second. Long teleport hops start fast, land gently.
        double f = 1 - Math.Exp(-dt * 6);
        b.Translate(dx * f, 0, dz * f);
    }

    private bool IsJumpModifierDown()
    {
        var keys = _capi!.Input.KeyboardKeyStateRaw;
        return keys[(int)GlKeys.ShiftLeft] || keys[(int)GlKeys.ShiftRight]
            || keys[(int)GlKeys.ControlLeft] || keys[(int)GlKeys.ControlRight];
    }

    /// <summary>Right-click a translocator endpoint on the map to open the
    /// per-translocator group picker; with Ctrl or Shift held, instead pan
    /// the map to the endpoint's other end. Left-click is left alone so map
    /// panning still works.</summary>
    public override void OnMouseUpClient(MouseEvent args, GuiElementMap map)
    {
        if (_capi == null || !Active || args.Handled) return;
        if (args.Button != EnumMouseButton.Right) return;
        if (!TryHit(args.X, args.Y, out var item, out bool isSrc)) return;

        // The link's far side, relative to the endpoint clicked.
        var other = isSrc ? item.Dst : item.Src;

        if (IsJumpModifierDown())
        {
            BeginPanTo(map, other);
            args.Handled = true;
            return;
        }

        if (_editDlg != null) { _editDlg.TryClose(); _editDlg.Dispose(); }
        _editDlg = new TranslocatorPathEditDialog(_capi, item.Key,
            () => BeginPanTo(map, other));
        _editDlg.TryOpen();
        args.Handled = true;
    }

    public override void Dispose()
    {
        if (_capi != null && _scanListenerId != 0)
            _capi.Event.UnregisterGameTickListener(_scanListenerId);
        foreach (var b in _batches.Values) b.DisposeRefs();
        _batches.Clear();
        base.Dispose();
    }
}
