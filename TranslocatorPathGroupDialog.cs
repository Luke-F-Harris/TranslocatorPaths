using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace TranslocatorPath;

/// <summary>
/// Edit one group: rename + recolour. Uses the exact colour palette the
/// vanilla waypoint editor uses (<c>WaypointMapLayer.WaypointColors</c>) plus
/// a free-form hex field for anything outside the palette.
/// </summary>
public class TranslocatorPathGroupDialog : GuiDialogGeneric
{
    private const string PickerId = "tpgcolor";
    private const string NameId = "tpgname";
    private const string HexId = "tpghex";

    private readonly string _gid;
    private readonly Action? _onClosed;
    private int[] _palette = Array.Empty<int>();

    public TranslocatorPathGroupDialog(ICoreClientAPI capi, string groupId, Action? onClosed)
        : base("", capi)
    {
        _gid = groupId;
        _onClosed = onClosed;
    }

    public override string ToggleKeyCombinationCode => "translocatorpath-groupedit";
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

    public override void OnGuiClosed()
    {
        base.OnGuiClosed();
        _onClosed?.Invoke();
    }

    private void Compose()
    {
        var store = TranslocatorPathModSystem.Store;
        var g = store?.GroupById(_gid);
        if (store == null || g == null) { TryClose(); return; }

        _palette = TranslocatorPathModSystem.WaypointPalette(capi);
        string curHex = TranslocatorStore.ToHex(g.Color);
        int curPaletteIdx = Array.FindIndex(_palette,
            v => ColorUtil.Int2Hex(v).TrimStart('#').Equals(curHex, StringComparison.OrdinalIgnoreCase));

        var bg = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bg.BothSizing = ElementSizing.FitToChildren;
        var dialogBounds = ElementStdBounds.AutosizedMainDialog.WithAlignment(EnumDialogArea.CenterMiddle);

        var nameLbl = ElementBounds.Fixed(0, 28, 50, 25);
        var nameInput = ElementBounds.Fixed(56, 28, 210, 25);
        var colLbl = ElementBounds.Fixed(0, 0, 266, 20).FixedUnder(nameInput, 10);
        var pick = colLbl.BelowCopy(0, 4).WithFixedSize(22, 22);
        var hexLbl = ElementBounds.Fixed(0, 0, 32, 24).FixedUnder(pick, 56);
        var hexInput = ElementBounds.Fixed(38, 0, 90, 24).FixedUnder(pick, 54);
        var applyHex = ElementBounds.Fixed(134, 0, 80, 26).FixedUnder(pick, 53);
        var cancelBtn = ElementBounds.Fixed(0, 0, 90, 26).FixedUnder(hexLbl, 14);
        var saveBtn = ElementBounds.Fixed(176, 0, 90, 26).FixedUnder(hexLbl, 14);

        SingleComposer?.Dispose();
        SingleComposer = capi.Gui.CreateCompo("translocatorpath-groupedit", dialogBounds)
            .AddShadedDialogBG(bg, false)
            .AddDialogTitleBar("Edit group", () => { TryClose(); })
            .BeginChildElements(bg)
                .AddStaticText("Name", CairoFont.WhiteSmallText(), nameLbl)
                .AddTextInput(nameInput, null, CairoFont.TextInput(), NameId)
                .AddStaticText("Colour (palette, or hex below)", CairoFont.WhiteSmallText(), colLbl)
                .AddColorListPicker(_palette, OnPalettePicked, pick, 264, PickerId)
                .AddStaticText("Hex", CairoFont.WhiteSmallText(), hexLbl)
                .AddTextInput(hexInput, null, CairoFont.TextInput(), HexId)
                .AddSmallButton("Use hex", OnUseHex, applyHex)
                .AddSmallButton("Cancel", () => { TryClose(); return true; }, cancelBtn)
                .AddSmallButton("Save", OnSave, saveBtn)
            .EndChildElements()
            .Compose();

        SingleComposer.GetTextInput(NameId).SetValue(g.Name);
        SingleComposer.GetTextInput(HexId).SetValue(curHex);
        if (curPaletteIdx >= 0) SingleComposer.ColorListPickerSetValue(PickerId, curPaletteIdx);
    }

    private void OnPalettePicked(int index)
    {
        if (index < 0 || index >= _palette.Length) return;
        SingleComposer.GetTextInput(HexId).SetValue(ColorUtil.Int2Hex(_palette[index]).TrimStart('#'));
    }

    private bool OnUseHex()
    {
        string hex = SingleComposer.GetTextInput(HexId)?.GetText()?.Trim() ?? "";
        if (!TranslocatorStore.IsValidHex(hex))
            capi.TriggerChatMessage("[translocatorpath] Colour must be 6-digit hex, e.g. 3FA0FF");
        return true;
    }

    private bool OnSave()
    {
        var store = TranslocatorPathModSystem.Store;
        if (store == null) { TryClose(); return true; }

        string name = SingleComposer.GetTextInput(NameId)?.GetText()?.Trim() ?? "";
        if (!string.IsNullOrEmpty(name)) store.RenameGroupById(_gid, name);

        string hex = SingleComposer.GetTextInput(HexId)?.GetText()?.Trim() ?? "";
        if (TranslocatorStore.IsValidHex(hex))
            store.SetGroupColor(_gid, TranslocatorStore.HexToColor(hex));

        store.SaveIfDirty();
        TryClose();
        return true;
    }
}
