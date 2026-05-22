using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace TranslocatorPath;

/// <summary>
/// In-game settings / translocator manager, built on the native VS GuiComposer.
///
/// Top: action buttons (Save / Reload / Import / Export / Rescan).
/// Middle: the groups — toggle visibility, recolour, edit, delete, plus a
/// "new group" field.
/// Bottom: a paged list of every known translocator, each with a dropdown to
/// move it into another group.
///
/// Paged rather than scrollbar-clipped, and the list is pre-sorted
/// nearest-first so the ones you care about are on page 1.
/// </summary>
public class TranslocatorPathDialog : GuiDialogGeneric
{
    public const string HotkeyCode = "translocatorpath-gui";
    private const int PageSize = 12;       // translocator rows per page
    private const int GroupsPerPage = 7;   // group rows per page
    private const int Width = 620;

    private int _page;
    private int _groupPage;

    public TranslocatorPathDialog(ICoreClientAPI capi) : base("TranslocatorPath", capi) { }

    public override string ToggleKeyCombinationCode => HotkeyCode;

    private static TranslocatorStore? Store => TranslocatorPathModSystem.Store;

    private TranslocatorPathLayer? Layer()
    {
        var mm = capi.ModLoader.GetModSystem<Vintagestory.GameContent.WorldMapManager>();
        return mm?.MapLayers.FirstOrDefault(l => l is TranslocatorPathLayer) as TranslocatorPathLayer;
    }

    public override void OnGuiOpened()
    {
        Compose();
        base.OnGuiOpened();
    }

    private void Compose()
    {
        var store = Store;
        var ppos = capi.World.Player?.Entity?.Pos;
        var near = ppos != null ? new Vec3d(ppos.X, ppos.Y, ppos.Z) : new Vec3d();
        var spawn = capi.World.DefaultSpawnPosition.AsBlockPos;

        var allGroups = store?.Groups.ToList() ?? new();
        int gTotalPages = Math.Max(1, (allGroups.Count + GroupsPerPage - 1) / GroupsPerPage);
        _groupPage = Math.Clamp(_groupPage, 0, gTotalPages - 1);
        var groups = allGroups.Skip(_groupPage * GroupsPerPage).Take(GroupsPerPage).ToList();

        var allRows = store?.SnapshotEntries(near) ?? new();
        int totalPages = Math.Max(1, (allRows.Count + PageSize - 1) / PageSize);
        _page = Math.Clamp(_page, 0, totalPages - 1);
        var pageRows = allRows.Skip(_page * PageSize).Take(PageSize).ToList();

        const int gRowH = 30, gRowStep = 36, eRowStep = 34;

        // Size to ACTUAL content, not the per-page maximum, so a near-empty
        // list doesn't leave a huge blank reserved area.
        int gRows = Math.Max(1, groups.Count);
        int eRows = pageRows.Count;

        var titleBar = ElementBounds.Fixed(0, 0, Width, 30);

        // Action buttons, two roomy rows of three.
        ElementBounds Btn(int col, int yy) => ElementBounds.Fixed(20 + col * 197, yy, 185, 30);
        var bSave = Btn(0, 40); var bReload = Btn(1, 40); var bImport = Btn(2, 40);
        var bExport = Btn(0, 78); var bHere = Btn(1, 78); var bAll = Btn(2, 78);

        // Settings/share row.
        var autoScanLbl = ElementBounds.Fixed(20, 120, 200, 24);
        var autoScanSwitch = ElementBounds.Fixed(224, 116, 30, 30);
        var shareChatBtn = Btn(2, 116);

        var grpHeader = ElementBounds.Fixed(20, 160, 240, 24);
        var gPrev = ElementBounds.Fixed(Width - 150, 158, 54, 26);
        var gNext = ElementBounds.Fixed(Width - 90, 158, 54, 26);
        var newGrpInput = ElementBounds.Fixed(20, 190, 360, 28);
        var addGrpBtn = ElementBounds.Fixed(392, 189, 130, 30);

        int groupsTop = 234;
        int groupsBottom = groupsTop + gRows * gRowStep;

        int lhY = groupsBottom + 18;
        var listHeader = ElementBounds.Fixed(20, lhY, 300, 24);
        var prevBtn = ElementBounds.Fixed(Width - 224, lhY - 3, 54, 26);
        var pageLbl = ElementBounds.Fixed(Width - 162, lhY, 80, 22);
        var nextBtn = ElementBounds.Fixed(Width - 78, lhY - 3, 54, 26);

        int listTop = lhY + 34;
        int listBottom = listTop + (eRows > 0 ? eRows * eRowStep : 28);
        var closeBtn = ElementBounds.Fixed((Width - 140) / 2, listBottom + 12, 140, 30);

        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);
        // Shrink-wrap the content (the DialogBackground(w,h) overload is
        // PADDING, not size — passing w/h there is what ballooned the window).
        var bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        // Release the previous composer's GPU MeshRefs; without this every
        // RebuildUi leaks the vertex buffers of every visual element.
        SingleComposer?.Dispose();

        var c = capi.Gui.CreateCompo("translocatorpath-gui", dialogBounds)
            .AddShadedDialogBG(bgBounds, true)
            .AddDialogTitleBar("TranslocatorPath", () => { TryClose(); }, null, titleBar)
            .BeginChildElements(bgBounds)
            .AddSmallButton("Save Now", OnSave, bSave)
            .AddSmallButton("Reload", OnReload, bReload)
            .AddSmallButton("Import", OnImport, bImport)
            .AddSmallButton("Export", OnExport, bExport)
            .AddSmallButton("Rescan Here", OnRescanHere, bHere)
            .AddSmallButton("Rescan All", OnRescanAll, bAll)
            .AddStaticText("Auto-scan chat shares", CairoFont.WhiteSmallText(), autoScanLbl)
            .AddSwitch(OnToggleAutoScan, autoScanSwitch, "autoscan", 28)
            .AddSmallButton("Share via Chat", OnShareViaChat, shareChatBtn)
            .AddStaticText($"Groups ({_groupPage + 1}/{gTotalPages})", CairoFont.WhiteSmallText(), grpHeader)
            .AddSmallButton("<", () => { _groupPage--; RebuildUi(); return true; }, gPrev)
            .AddSmallButton(">", () => { _groupPage++; RebuildUi(); return true; }, gNext)
            .AddTextInput(newGrpInput, null, CairoFont.WhiteSmallText(), "newgrp")
            .AddSmallButton("Add Group", OnAddGroup, addGrpBtn);

        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            int gy = groupsTop + i * gRowStep;
            string gid = g.Id;
            int count = store!.CountIn(gid);

            string gname = g.Name;
            var visB = ElementBounds.Fixed(20, gy, 68, gRowH);
            var swatchB = ElementBounds.Fixed(96, gy + 7, 18, 18);
            var nameB = ElementBounds.Fixed(122, gy + 6, Width - 408, 22);
            var expB = ElementBounds.Fixed(Width - 274, gy, 76, gRowH);
            var editB = ElementBounds.Fixed(Width - 192, gy, 76, gRowH);
            var delB = ElementBounds.Fixed(Width - 110, gy, 76, gRowH);

            c.AddSmallButton(g.Visible ? "Shown" : "Hide",
                    () => { store.SetGroupVisible(gid, !g.Visible); store.SaveIfDirty(); RebuildUi(); return true; },
                    visB)
             .AddColorListPicker(new[] { TranslocatorStore.ColorToInt(g.Color) },
                    _ => { }, swatchB, 24, "gsw_" + i)
             .AddStaticText($"{g.Name} ({count}){(g.Imported ? " [imp]" : "")}",
                    CairoFont.WhiteSmallText(), nameB)
             .AddSmallButton("Export", () =>
                 {
                     string? p = store.ExportGroup(gid);
                     capi.TriggerChatMessage(p != null
                         ? $"[translocatorpath] Exported group '{gname}' to {p}"
                         : "[translocatorpath] Export failed.");
                     return true;
                 }, expB)
             .AddSmallButton("Edit", () => { OpenGroupEditor(gid); return true; }, editB);

            if (gid != TranslocatorStore.SelfGroupId)
                c.AddSmallButton("Del",
                    () => { store.DeleteGroup(g.Name); store.SaveIfDirty(); RebuildUi(); return true; },
                    delB);
        }

        c.AddStaticText($"Translocators ({allRows.Count})", CairoFont.WhiteSmallText(), listHeader)
         .AddSmallButton("<", () => { _page--; RebuildUi(); return true; }, prevBtn)
         .AddStaticText($"{_page + 1}/{totalPages}", CairoFont.WhiteSmallText(), pageLbl)
         .AddSmallButton(">", () => { _page++; RebuildUi(); return true; }, nextBtn);

        string[] gids = allGroups.Select(g => g.Id).ToArray();
        string[] gnames = allGroups.Select(g => g.Name).ToArray();

        if (pageRows.Count == 0)
            c.AddStaticText(
                "No translocators yet — explore near repaired ones, then Rescan.",
                CairoFont.WhiteDetailText(), ElementBounds.Fixed(20, listTop + 2, Width - 40, 20));

        for (int i = 0; i < pageRows.Count; i++)
        {
            var row = pageRows[i];
            int ry = listTop + i * eRowStep;
            var lblB = ElementBounds.Fixed(20, ry + 7, Width - 250, 22);
            var ddB = ElementBounds.Fixed(Width - 216, ry, 196, 30);

            string who = string.IsNullOrEmpty(row.Origin) ? "you" : row.Origin;
            string txt =
                $"{row.Src.X - spawn.X},{row.Src.Y},{row.Src.Z - spawn.Z} -> " +
                $"{row.Dst.X - spawn.X},{row.Dst.Y},{row.Dst.Z - spawn.Z} ({who})";

            int sel = Math.Max(0, Array.IndexOf(gids, row.GroupId));
            long key = row.Key;
            c.AddStaticText(txt, CairoFont.WhiteDetailText(), lblB)
             .AddDropDown(gids, gnames, sel,
                 (code, selected) =>
                 {
                     if (!selected) return;
                     store!.ReassignEntry(key, code);
                     store.SaveIfDirty();
                     RebuildUi();
                 }, ddB, "grp_" + i);
        }

        c.AddSmallButton("Close", () => { TryClose(); return true; }, closeBtn);

        SingleComposer = c.EndChildElements().Compose();

        var modsys = TranslocatorPathModSystem.Instance;
        if (modsys != null)
            SingleComposer.GetSwitch("autoscan").On = modsys.AutoScanChatShare;

        for (int i = 0; i < groups.Count; i++)
            SingleComposer.ColorListPickerSetValue("gsw_" + i, 0);
    }

    private void OnToggleAutoScan(bool on)
    {
        var modsys = TranslocatorPathModSystem.Instance;
        if (modsys == null) return;
        modsys.AutoScanChatShare = on;
        capi.TriggerChatMessage(
            $"[translocatorpath] Auto-scan for chat shares {(on ? "ENABLED" : "disabled")}.");
    }

    private bool OnShareViaChat()
    {
        var dlg = new GuiDialogConfirm(capi,
            "Warning, this will upload your translocators to paste.rs and share " +
            "this upload in chat. Do you want to continue?",
            confirmed =>
            {
                if (confirmed) TranslocatorPathModSystem.Instance?.ShareViaChat();
            });
        dlg.TryOpen();
        return true;
    }

    private TranslocatorPathGroupDialog? _groupDlg;

    private void OpenGroupEditor(string gid)
    {
        if (_groupDlg != null) { _groupDlg.TryClose(); _groupDlg.Dispose(); }
        _groupDlg = new TranslocatorPathGroupDialog(capi, gid, RebuildUi);
        _groupDlg.TryOpen();
    }

    private void RebuildUi()
    {
        Compose();
    }

    private bool OnSave()
    {
        Store?.ForceSave();
        capi.TriggerChatMessage("[translocatorpath] Saved.");
        return true;
    }

    private bool OnReload()
    {
        Store?.ReloadFromDisk();
        Layer()?.RescanAll();
        capi.TriggerChatMessage("[translocatorpath] Reloaded from disk.");
        RebuildUi();
        return true;
    }

    private bool OnImport()
    {
        var s = Store;
        if (s == null) return true;
        var (files, added, skipped) = s.ImportDropFolder();
        capi.TriggerChatMessage(
            $"[translocatorpath] Imported {added} from {files} file(s)" +
            (skipped > 0 ? $", skipped {skipped} (other worlds)." : "."));
        RebuildUi();
        return true;
    }

    private bool OnExport()
    {
        var s = Store;
        if (s == null) return true;
        s.ForceSave();
        string p = s.Export();
        capi.TriggerChatMessage($"[translocatorpath] Exported to {p}");
        return true;
    }

    private bool OnRescanHere()
    {
        Layer()?.RescanCurrentChunk();
        RebuildUi();
        return true;
    }

    private bool OnRescanAll()
    {
        Layer()?.RescanAll();
        RebuildUi();
        return true;
    }

    private bool OnAddGroup()
    {
        var s = Store;
        if (s == null) return true;
        string name = SingleComposer.GetTextInput("newgrp")?.GetText()?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            capi.TriggerChatMessage("[translocatorpath] Enter a group name first.");
            return true;
        }
        if (s.FindGroupByName(name) != null)
        {
            capi.TriggerChatMessage($"[translocatorpath] Group '{name}' already exists.");
            return true;
        }
        s.AddGroup(name, null);
        s.SaveIfDirty();
        RebuildUi();
        return true;
    }
}
