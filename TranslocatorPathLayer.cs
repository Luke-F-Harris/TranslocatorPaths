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

    private MeshRef? _quad;
    private readonly Matrixf _mat = new();
    private long _scanListenerId;

    // Reused per-frame to avoid 2 * MaxLinks * fps allocations in Render.
    private readonly Vec4f _lineCol = new();
    private readonly Vec4f _endCol = new();

    /// <summary>Screen-space endpoint positions captured during Render, so
    /// the hover / right-click hit tests reuse this frame's projections
    /// instead of re-projecting (and, for right-click, distance-sorting)
    /// every entry per event.</summary>
    private struct HitPoint
    {
        public float X, Y;
        public int ItemIndex; // into _renderedItems
        public bool IsSrc;
    }

    private readonly List<HitPoint> _hitPoints = new();
    private List<TranslocatorStore.RenderItem> _renderedItems = new();

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
        _renderedItems = items;
        _hitPoints.Clear();
        if (items.Count == 0) return;

        _quad ??= _capi.Render.UploadMesh(QuadMeshUtil.GetQuad());

        var api = map.Api;
        IShaderProgram prog = api.Render.GetEngineShader(EnumShaderProgram.Gui);
        prog.Uniform("extraGlow", 0);
        prog.Uniform("applyColor", 0);
        prog.Uniform("noTexture", 1f);
        prog.UniformMatrix("projectionMatrix", api.Render.CurrentProjectionMatrix);

        api.Render.PushScissor(map.Bounds, true);

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

            var rgb = item.Color;
            _lineCol.Set(rgb.R, rgb.G, rgb.B, 0.55f);
            _endCol.Set(rgb.R, rgb.G, rgb.B, 1f);

            DrawQuad(api, prog, (ax + bx) / 2f, (ay + by) / 2f, ang,
                len / 2f, LineThicknessPx / 2f, _lineCol);
            DrawQuad(api, prog, ax, ay, 0f, MarkerSizePx / 2f, MarkerSizePx / 2f, _endCol);
            DrawQuad(api, prog, bx, by, 0f, MarkerSizePx / 3f, MarkerSizePx / 3f, _endCol);
        }

        api.Render.PopScissor();
    }

    private void DrawQuad(ICoreClientAPI api, IShaderProgram prog,
        float cx, float cy, float angle, float halfW, float halfH, Vec4f color)
    {
        _mat.Set(api.Render.CurrentModelviewMatrix).Translate(cx, cy, 60f);
        if (angle != 0f) _mat.RotateZ(angle);
        _mat.Scale(halfW, halfH, 0f);
        prog.Uniform("rgbaIn", color);
        prog.UniformMatrix("modelViewMatrix", _mat.Values);
        api.Render.RenderMesh(_quad);
    }

    private static bool OffSameSide(double a, double b, double lo, double hi)
    {
        const double m = 8;
        return (a < lo - m && b < lo - m) || (a > hi + m && b > hi + m);
    }

    /// <summary>Find the hit-cache entry under the cursor, if any. Uses the
    /// screen positions Render already computed this frame instead of
    /// re-projecting every entry per mouse event.</summary>
    private bool TryHit(double mx, double my, out TranslocatorStore.RenderItem item)
    {
        item = default;
        double hit = RuntimeEnv.GUIScale * 6;
        for (int i = 0; i < _hitPoints.Count; i++)
        {
            var p = _hitPoints[i];
            if (Math.Abs(mx - p.X) >= hit || Math.Abs(my - p.Y) >= hit) continue;
            if (p.ItemIndex >= _renderedItems.Count) continue; // stale frame guard
            item = _renderedItems[p.ItemIndex];
            return true;
        }
        return false;
    }

    public override void OnMouseMoveClient(MouseEvent args, GuiElementMap map, StringBuilder hoverText)
    {
        if (_capi == null || !Active) return;
        if (!TryHit(args.X, args.Y, out var item)) return;

        var spawn = _capi.World.DefaultSpawnPosition.AsBlockPos;
        var s = item.Src; var d = item.Dst;
        string who = string.IsNullOrEmpty(item.Origin) ? "you" : item.Origin;
        hoverText.AppendLine(
            $"[{item.GroupName}] (via {who})\n" +
            $"Translocator {s.X - spawn.X}, {s.Y}, {s.Z - spawn.Z}\n" +
            $"  -> {d.X - spawn.X}, {d.Y}, {d.Z - spawn.Z}");
    }

    private TranslocatorPathEditDialog? _editDlg;

    /// <summary>Right-click a translocator endpoint on the map to open the
    /// per-translocator group picker. Left-click is left alone so map panning
    /// still works.</summary>
    public override void OnMouseUpClient(MouseEvent args, GuiElementMap map)
    {
        if (_capi == null || !Active || args.Handled) return;
        if (args.Button != EnumMouseButton.Right) return;

        // Previously this snapshotted and distance-sorted EVERY entry
        // (including hidden groups) per right-click; the hit cache covers
        // exactly what is drawn, so hidden-group endpoints - which were
        // invisible yet clickable - no longer react.
        if (!TryHit(args.X, args.Y, out var item)) return;

        if (_editDlg != null) { _editDlg.TryClose(); _editDlg.Dispose(); }
        _editDlg = new TranslocatorPathEditDialog(_capi, item.Key);
        _editDlg.TryOpen();
        args.Handled = true;
    }

    public override void Dispose()
    {
        if (_capi != null && _scanListenerId != 0)
            _capi.Event.UnregisterGameTickListener(_scanListenerId);
        _quad?.Dispose();
        _quad = null;
        base.Dispose();
    }
}
