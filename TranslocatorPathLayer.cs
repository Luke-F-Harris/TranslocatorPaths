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

    // Reused per-frame to avoid 2 * MaxLinks * fps allocations in Render.
    private readonly Vec4f _lineCol = new();
    private readonly Vec4f _endCol = new();

    /// <summary>One mesh pair per group colour: every line quad of that colour
    /// in one mesh, every endpoint marker in another. A frame then renders in
    /// 2 draw calls per distinct colour instead of 3 per translocator (which
    /// was 6000 draw calls + 12000 uniform uploads at the default 2000-link
    /// cap). Vertices are written in final screen space, so no per-quad
    /// model matrix is needed.</summary>
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

        foreach (long idx in indices)
        {
            lock (_scanLock)
            {
                if (!_scannedChunks.Add(idx)) continue;
            }

            var bes = ba.GetChunk(idx)?.BlockEntities;
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

                if (store.TotalCount >= MaxLinks) return;
                store.AddDiscovered(src.Copy(), dst.Copy(), idx);
            }
        }
    }

    public override void Render(GuiElementMap map, float dt)
    {
        if (_capi == null || !Active) return;
        var store = Store;
        if (store == null) return;

        var items = store.SnapshotVisible();
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

        foreach (var item in items)
        {
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
                // Colour vanished (group recoloured/hidden); free its buffers
                // rather than holding GPU memory for every colour ever seen.
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
    /// past what was uploaded. The GPU buffer is sized by UploadMesh to the
    /// data it was created with, so that count is the reuse limit.</summary>
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

    public override void OnMouseMoveClient(MouseEvent args, GuiElementMap map, StringBuilder hoverText)
    {
        if (_capi == null || !Active) return;
        var store = Store;
        if (store == null) return;
        var items = store.SnapshotVisible();
        if (items.Count == 0) return;

        var spawn = _capi.World.DefaultSpawnPosition.AsBlockPos;
        var vp = new Vec2f();
        var world = new Vec3d();
        double hit = RuntimeEnv.GUIScale * 6;

        foreach (var item in items)
        {
            for (int end = 0; end < 2; end++)
            {
                var p = end == 0 ? item.Src : item.Dst;
                world.Set(p.X + 0.5, p.Y, p.Z + 0.5);
                map.TranslateWorldPosToViewPos(world, ref vp);
                double sx = map.Bounds.renderX + vp.X;
                double sy = map.Bounds.renderY + vp.Y;
                if (Math.Abs(args.X - sx) >= hit || Math.Abs(args.Y - sy) >= hit) continue;

                var s = item.Src; var d = item.Dst;
                string who = string.IsNullOrEmpty(item.Origin) ? "you" : item.Origin;
                hoverText.AppendLine(
                    $"[{item.GroupName}] (via {who})\n" +
                    $"Translocator {s.X - spawn.X}, {s.Y}, {s.Z - spawn.Z}\n" +
                    $"  -> {d.X - spawn.X}, {d.Y}, {d.Z - spawn.Z}");
                return;
            }
        }
    }

    private TranslocatorPathEditDialog? _editDlg;

    /// <summary>Right-click a translocator endpoint on the map to open the
    /// per-translocator group picker. Left-click is left alone so map panning
    /// still works.</summary>
    public override void OnMouseUpClient(MouseEvent args, GuiElementMap map)
    {
        if (_capi == null || !Active || args.Handled) return;
        if (args.Button != EnumMouseButton.Right) return;
        var store = Store;
        if (store == null) return;

        var ppos = _capi.World?.Player?.Entity?.Pos;
        var near = ppos != null ? new Vec3d(ppos.X, ppos.Y, ppos.Z) : new Vec3d();
        var rows = store.SnapshotEntries(near);
        if (rows.Count == 0) return;

        var vp = new Vec2f();
        var world = new Vec3d();
        double hitR = RuntimeEnv.GUIScale * 6;

        foreach (var row in rows)
        {
            for (int end = 0; end < 2; end++)
            {
                var p = end == 0 ? row.Src : row.Dst;
                world.Set(p.X + 0.5, p.Y, p.Z + 0.5);
                map.TranslateWorldPosToViewPos(world, ref vp);
                double sx = map.Bounds.renderX + vp.X;
                double sy = map.Bounds.renderY + vp.Y;
                if (Math.Abs(args.X - sx) >= hitR || Math.Abs(args.Y - sy) >= hitR) continue;

                if (_editDlg != null) { _editDlg.TryClose(); _editDlg.Dispose(); }
                _editDlg = new TranslocatorPathEditDialog(_capi, row.Key);
                _editDlg.TryOpen();
                args.Handled = true;
                return;
            }
        }
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
