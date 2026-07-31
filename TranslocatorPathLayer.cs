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

        // Claim all unscanned chunks in ONE lock acquisition. The previous
        // per-chunk lock meant a steady-state tick (everything already
        // scanned) still took the lock once per loaded chunk - thousands of
        // acquisitions to discover there was nothing to do.
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
    /// .tllinesmax) can still pick them up. The chunk being scanned when the
    /// cap is hit stays claimed, matching the previous behaviour.</summary>
    private void UnclaimFrom(List<long> fresh, int start)
    {
        if (start >= fresh.Count) return;
        lock (_scanLock)
            for (int i = start; i < fresh.Count; i++) _scannedChunks.Remove(fresh[i]);
    }

    public override void Render(GuiElementMap map, float dt)
    {
        if (_capi == null || !Active) return;
        var store = Store;
        if (store == null) return;

        var items = store.SnapshotVisible();
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
        _quad?.Dispose();
        _quad = null;
        base.Dispose();
    }
}
