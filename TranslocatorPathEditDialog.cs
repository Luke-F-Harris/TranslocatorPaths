using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace TranslocatorPath;

/// <summary>
/// Opened by right-clicking a translocator on the world map. A dropdown of the
/// groups (with a live colour swatch of the selected one) lets you move that
/// single translocator between groups. Changing the group reassigns + saves
/// immediately and keeps the dialog open.
///
/// Overlay behaviour mirrors <c>GuiDialogAddWayPoint</c> so it sits on top of
/// the open world-map dialog.
/// </summary>
public class TranslocatorPathEditDialog : GuiDialogGeneric
{
    private const string DropId = "tpgroupdd";
    private const string SwatchId = "tpgroupsw";
    private readonly long _key;

    public TranslocatorPathEditDialog(ICoreClientAPI capi, long entryKey)
        : base("", capi) => _key = entryKey;

    public override string ToggleKeyCombinationCode => "translocatorpath-edit";
    public override EnumDialogType DialogType => EnumDialogType.Dialog;
    public override bool DisableMouseGrab => true;
    public override bool PrefersUngrabbedMouse => true;
    public override double DrawOrder => 0.2;
    public override bool CaptureAllInputs() => IsOpened();

    public override void OnMouseDown(MouseEvent args) { base.OnMouseDown(args); args.Handled = true; }
    public override void OnMouseUp(MouseEvent args) { base.OnMouseUp(args); args.Handled = true; }
    public override void OnMouseMove(MouseEvent args) { base.OnMouseMove(args); args.Handled = true; }
    public override void OnMouseWheel(MouseWheelEventArgs args) { base.OnMouseWheel(args); args.SetHandled(true); }

    public override bool TryOpen()
    {
        Compose();
        return base.TryOpen();
    }

    private void Compose()
    {
        var store = TranslocatorPathModSystem.Store;
        var row = store?.EntryByKey(_key);
        if (store == null || row == null) { TryClose(); return; }

        var groups = store.Groups.ToList();
        string[] ids = groups.Select(g => g.Id).ToArray();
        string[] names = groups.Select(g => $"{g.Name} ({store.CountIn(g.Id)})").ToArray();
        int sel = Math.Max(0, Array.FindIndex(ids, id => id == row.Value.GroupId));
        int swatchColor = groups.Count > 0
            ? TranslocatorStore.ColorToInt(groups[sel].Color) : 0x35DDAC;

        var spawn = capi.World.DefaultSpawnPosition.AsBlockPos;
        var s = row.Value.Src; var d = row.Value.Dst;
        string coords =
            $"{s.X - spawn.X}, {s.Y}, {s.Z - spawn.Z}  <->  {d.X - spawn.X}, {d.Y}, {d.Z - spawn.Z}";

        var bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bg.BothSizing = ElementSizing.FitToChildren;
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

        var line1 = ElementBounds.Fixed(0, 28, 320, 20);
        var line2 = line1.BelowCopy(0, 8).WithFixedHeight(18);
        var swatch = ElementBounds.Fixed(0, 0, 22, 22).FixedUnder(line2, 8);
        var dropd = ElementBounds.Fixed(30, 0, 290, 28).FixedUnder(line2, 5);
        var closeBtn = ElementBounds.Fixed(0, 0, 100, 26).FixedUnder(dropd, 14);

        SingleComposer?.Dispose();
        SingleComposer = capi.Gui.CreateCompo("translocatorpath-edit", dialogBounds)
            .AddShadedDialogBG(bg, false)
            .AddDialogTitleBar("Translocator", () => { TryClose(); })
            .BeginChildElements(bg)
                .AddStaticText(coords, CairoFont.WhiteSmallText(), line1)
                .AddStaticText("Group:", CairoFont.WhiteDetailText(), line2)
                .AddColorListPicker(new[] { swatchColor }, _ => { }, swatch, 30, SwatchId)
                .AddDropDown(ids, names, sel, OnGroupChanged, dropd, DropId)
                .AddSmallButton("Close", () => { TryClose(); return true; }, closeBtn)
            .EndChildElements()
            .Compose();

        SingleComposer.ColorListPickerSetValue(SwatchId, 0);
    }

    private void OnGroupChanged(string code, bool selected)
    {
        if (!selected) return;
        var store = TranslocatorPathModSystem.Store;
        if (store == null) return;
        store.ReassignEntry(_key, code);
        store.SaveIfDirty();
        Compose(); // refresh swatch + selection, keep dialog open
    }
}
