using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace TranslocatorPath;

/// <summary>
/// Registers the map layer, owns the shared <see cref="TranslocatorStore"/>
/// (created at level finalize when the savegame id + data paths exist), the
/// GUI dialog, and the chat commands. Discovered translocators auto-save to a
/// per-world file; sharing is drop-folder file exchange.
/// </summary>
public class TranslocatorPathModSystem : ModSystem
{
    public static TranslocatorStore? Store;

    /// <summary>Set in StartClientSide so the GUI dialog can reach instance
    /// methods (chat-share, settings) without plumbing a reference through.</summary>
    public static TranslocatorPathModSystem? Instance;

    // Chat marker. Paired with a paste.rs URL so a stray "[tlpath]" mention
    // doesn't trigger a fetch.
    public const string ChatMarker = "[tlpath]";
    private const string PasteEndpoint = "https://paste.rs/";

    // Global client setting, off by default: the player opts in before we
    // fetch paste.rs links seen in chat.
    private const string AutoScanSettingKey = "translocatorpath-autoscanchatshare";

    public bool AutoScanChatShare
    {
        get => _capi.Settings.Bool.Get(AutoScanSettingKey, false);
        set => _capi.Settings.Bool[AutoScanSettingKey] = value;
    }

    // Global client setting, off by default: also record unrepaired
    // translocators as destination-less markers in a "Broken" group.
    private const string ShowBrokenSettingKey = "translocatorpath-showbroken";

    public bool ShowBrokenTranslocators
    {
        get => _capi.Settings.Bool.Get(ShowBrokenSettingKey, false);
        set
        {
            _capi.Settings.Bool[ShowBrokenSettingKey] = value;
            if (Store != null) Store.ShowBroken = value;
        }
    }

    private static readonly Regex ShareLinkRegex = new(
        @"\[tlpath\]\s+(https?://paste\.rs/[A-Za-z0-9./_-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Flatten an exception chain into "OuterType: msg -> InnerType: msg".
    /// HttpClient buries the real cause (e.g. a TLS failure) inside a generic
    /// "SSL connection could not be established", so the inner messages matter.</summary>
    private static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (sb.Length > 0) sb.Append(" -> ");
            sb.Append(e.GetType().Name).Append(": ").Append(e.Message);
        }
        // Collapse whitespace runs: ShowChatMessage renders each \n as its own
        // chat line, and exception text is often multi-line.
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    // Chat renders blank for over-long lines, so clip what we show (the full
    // text still goes to the client log).
    private static string Clip(string s, int max = 300) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";

    /// <summary>Show a line in chat and mirror it to client-main.log, since
    /// ShowChatMessage alone never reaches a log file. Pass logMsg to log a
    /// fuller (untruncated) version than what chat shows.</summary>
    private void Report(string chatMsg, string? logMsg = null)
    {
        _capi.Logger.Notification("[translocatorpath] " + (logMsg ?? chatMsg));
        ToMainThread(() => _capi.ShowChatMessage(chatMsg));
    }

    private ICoreClientAPI _capi = null!;
    private TranslocatorPathDialog? _dialog;
    private readonly HashSet<string> _ownShareUrls = new(StringComparer.OrdinalIgnoreCase);

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        _capi = api;
        Instance = this;

        var mapManager = api.ModLoader.GetModSystem<WorldMapManager>();
        if (mapManager == null)
        {
            api.Logger.Warning("[translocatorpath] WorldMapManager not found; disabled.");
            return;
        }
        mapManager.RegisterMapLayer<TranslocatorPathLayer>("translocatorpath", 1.5);

        api.Input.RegisterHotKey(TranslocatorPathDialog.HotkeyCode,
            "TranslocatorPath: open manager", GlKeys.K, HotkeyType.GUIOrOtherControls);
        api.Input.SetHotKeyHandler(TranslocatorPathDialog.HotkeyCode, _ => { ToggleDialog(); return true; });

        api.Event.LevelFinalize += () =>
        {
            Store = new TranslocatorStore(api);
            Store.ShowBroken = ShowBrokenTranslocators;
            Store.Init();
        };
        api.Event.RegisterGameTickListener(_ => Store?.SaveIfDirty(), 5000);
        api.Event.LeaveWorld += () => Store?.SaveIfDirty();
        api.Event.ChatMessage += OnChatMessage;

        RegisterCommands(api);
    }

    public override void Dispose()
    {
        Store?.SaveIfDirty();
        base.Dispose();
    }

    private void ToggleDialog()
    {
        _dialog ??= new TranslocatorPathDialog(_capi);
        if (_dialog.IsOpened()) _dialog.TryClose();
        else _dialog.TryOpen();
    }

    private TranslocatorPathLayer? GetLayer()
    {
        var mm = _capi.ModLoader.GetModSystem<WorldMapManager>();
        return mm?.MapLayers.FirstOrDefault(l => l is TranslocatorPathLayer) as TranslocatorPathLayer;
    }

    /// <summary>The exact colour palette the vanilla waypoint editor uses
    /// (ints in ColorUtil's 0x00RRGGBB layout). Falls back to a small default
    /// if the waypoint layer isn't present.</summary>
    public static int[] WaypointPalette(ICoreClientAPI capi)
    {
        var mm = capi.ModLoader.GetModSystem<WorldMapManager>();
        var wp = mm?.MapLayers.FirstOrDefault(l => l is WaypointMapLayer) as WaypointMapLayer;
        var list = wp?.WaypointColors;
        if (list != null && list.Count > 0) return list.ToArray();
        return new[]
        {
            0x35DDAC, 0xFF8C26, 0x3FA0FF, 0xE25BFF, 0xFFE14B,
            0x7CFF5B, 0xFF5B7A, 0x26D7FF, 0xC08CFF, 0xFF3B3B,
        };
    }

    private void RegisterCommands(ICoreClientAPI api)
    {
        var parsers = api.ChatCommands.Parsers;

        api.ChatCommands.Create("tlgui")
            .WithDescription("Open the TranslocatorPath manager (groups + waypoint reassignment).")
            .HandleWith(_ => { ToggleDialog(); return TextCommandResult.Success(""); });

        api.ChatCommands.Create("tl")
            .WithDescription("TranslocatorPath status: groups, counts, share/import folders.")
            .HandleWith(_ =>
            {
                if (Store == null) return TextCommandResult.Error("Not ready yet (join a world).");
                var sb = new StringBuilder();
                sb.AppendLine($"TranslocatorPath: {Store.TotalCount} translocator(s).");
                foreach (var g in Store.Groups)
                    sb.AppendLine(
                        $"  [{(g.Visible ? "x" : " ")}] {g.Name} " +
                        $"({Store.CountIn(g.Id)}){(g.Imported ? " (imported)" : "")} " +
                        $"#{TranslocatorStore.ToHex(g.Color)}");
                sb.AppendLine($"Import folder: {Store.ImportDirPath}");
                sb.AppendLine($"Share folder:  {Store.ShareDirPath}");
                return TextCommandResult.Success(sb.ToString().TrimEnd());
            });

        api.ChatCommands.Create("tlgroup")
            .WithDescription("Manage groups: .tlgroup add|color|rename|toggle|del <args>. " +
                             "Group names cannot contain spaces.")
            .WithArgs(parsers.OptionalAll("args"))
            .HandleWith(args => HandleGroup((args[0] as string)?.Trim() ?? ""));

        api.ChatCommands.Create("tlassign")
            .WithDescription("Assign the translocator nearest you to a group: .tlassign <groupname>")
            .WithArgs(parsers.OptionalAll("group"))
            .HandleWith(args =>
            {
                if (Store == null) return TextCommandResult.Error("Not ready yet.");
                string name = (args[0] as string)?.Trim() ?? "";
                if (string.IsNullOrEmpty(name))
                    return TextCommandResult.Error("Usage: .tlassign <groupname>");
                var ppos = _capi.World?.Player?.Entity?.Pos;
                if (ppos == null) return TextCommandResult.Error("No player.");
                var (ok, src) = Store.AssignNearest(new Vec3d(ppos.X, ppos.Y, ppos.Z), name, 24);
                if (!ok)
                    return TextCommandResult.Error(
                        Store.FindGroupByName(name) == null
                            ? $"No group '{name}'."
                            : "No known translocator within 24 blocks.");
                Store.SaveIfDirty();
                return TextCommandResult.Success($"Assigned translocator at {src} to '{name}'.");
            });

        api.ChatCommands.Create("tlexport")
            .WithDescription("Export to the share folder. No arg = whole list; " +
                             ".tlexport <groupname> = just that group.")
            .WithArgs(parsers.OptionalAll("group"))
            .HandleWith(args =>
            {
                if (Store == null) return TextCommandResult.Error("Not ready yet.");
                Store.ForceSave();
                string name = (args[0] as string)?.Trim() ?? "";
                if (string.IsNullOrEmpty(name))
                {
                    string path = Store.Export();
                    return TextCommandResult.Success(
                        $"Exported full list to:\n{path}\nDrop it in someone's import folder.");
                }
                var g = Store.FindGroupByName(name);
                if (g == null) return TextCommandResult.Error($"No group '{name}'.");
                string? gp = Store.ExportGroup(g.Id);
                return gp == null
                    ? TextCommandResult.Error("Export failed.")
                    : TextCommandResult.Success($"Exported group '{g.Name}' to:\n{gp}");
            });

        api.ChatCommands.Create("tlimport")
            .WithDescription("Scan the import folder for other players' lists and merge them.")
            .HandleWith(_ =>
            {
                if (Store == null) return TextCommandResult.Error("Not ready yet.");
                var (files, added, skipped) = Store.ImportDropFolder();
                return TextCommandResult.Success(
                    $"Imported {added} new from {files} file(s)" +
                    (skipped > 0 ? $", skipped {skipped} from other worlds." : ".") +
                    $"\nDrop files into: {Store.ImportDirPath}");
            });

        api.ChatCommands.Create("tlshare")
            .WithDescription("Upload your translocator list to paste.rs and post the link to current chat.")
            .HandleWith(args =>
            {
                if (Store == null) return TextCommandResult.Error("Not ready yet.");
                ShareViaChat();
                return TextCommandResult.Success("Uploading to paste.rs...");
            });

        api.ChatCommands.Create("tllinesrescanhere")
            .WithDescription("Forget + rescan the chunk you're standing in (use after repairing a TL).")
            .HandleWith(_ =>
            {
                var layer = GetLayer();
                if (layer == null) return TextCommandResult.Error("Open the world map once first.");
                if (!layer.RescanCurrentChunk())
                    return TextCommandResult.Error("Couldn't resolve your current chunk.");
                return TextCommandResult.Success($"Rescanned current chunk: {Store?.TotalCount ?? 0} total.");
            });

        api.ChatCommands.Create("tllinesrescanall")
            .WithDescription("Clear the scan cache and re-read all loaded chunks (keeps groups/assignments).")
            .HandleWith(_ =>
            {
                var layer = GetLayer();
                if (layer == null) return TextCommandResult.Error("Open the world map once first.");
                layer.RescanAll();
                return TextCommandResult.Success($"Full rescan: {Store?.TotalCount ?? 0} total.");
            });

        api.ChatCommands.Create("tllinesscan")
            .WithDescription("Set rescan interval seconds (default 3).")
            .WithArgs(parsers.OptionalAll("sec"))
            .HandleWith(args =>
            {
                string v = (args[0] as string)?.Trim() ?? "";
                if (string.IsNullOrEmpty(v))
                    return TextCommandResult.Success($"Scan interval: {TranslocatorPathLayer.ScanIntervalSec}s");
                if (!float.TryParse(v, out float s) || s <= 0)
                    return TextCommandResult.Error("Usage: .tllinesscan <positive seconds>");
                TranslocatorPathLayer.ScanIntervalSec = Math.Clamp(s, 0.5f, 60f);
                GetLayer()?.RestartScanTimer();
                return TextCommandResult.Success($"Scan interval: {TranslocatorPathLayer.ScanIntervalSec}s");
            });

        api.ChatCommands.Create("tllinesmax")
            .WithDescription("Set max tracked translocators (default 2000).")
            .WithArgs(parsers.OptionalAll("n"))
            .HandleWith(args =>
            {
                string v = (args[0] as string)?.Trim() ?? "";
                if (string.IsNullOrEmpty(v))
                    return TextCommandResult.Success($"Max links: {TranslocatorPathLayer.MaxLinks}");
                if (!int.TryParse(v, out int n) || n < 1)
                    return TextCommandResult.Error("Usage: .tllinesmax <positive integer>");
                TranslocatorPathLayer.MaxLinks = Math.Clamp(n, 1, 100000);
                return TextCommandResult.Success($"Max links: {TranslocatorPathLayer.MaxLinks}");
            });

        api.ChatCommands.Create("tllinesthickness")
            .WithDescription("Set on-map line thickness px (default 2.5).")
            .WithArgs(parsers.OptionalAll("px"))
            .HandleWith(args =>
            {
                string v = (args[0] as string)?.Trim() ?? "";
                if (string.IsNullOrEmpty(v))
                    return TextCommandResult.Success($"Line thickness: {TranslocatorPathLayer.LineThicknessPx}px");
                if (!float.TryParse(v, out float px) || px <= 0)
                    return TextCommandResult.Error("Usage: .tllinesthickness <positive px>");
                TranslocatorPathLayer.LineThicknessPx = Math.Clamp(px, 0.5f, 20f);
                return TextCommandResult.Success($"Line thickness: {TranslocatorPathLayer.LineThicknessPx}px");
            });
    }

    private TextCommandResult HandleGroup(string argstr)
    {
        if (Store == null) return TextCommandResult.Error("Not ready yet (join a world).");
        var parts = argstr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return TextCommandResult.Error(
                "Usage: .tlgroup add <name> [#RRGGBB] | color <name> <#RRGGBB> | " +
                "rename <old> <new> | toggle <name> | del <name>");

        string sub = parts[0].ToLowerInvariant();
        switch (sub)
        {
            case "add":
            {
                if (parts.Length < 2) return TextCommandResult.Error("Usage: .tlgroup add <name> [#RRGGBB]");
                if (Store.FindGroupByName(parts[1]) != null)
                    return TextCommandResult.Error($"Group '{parts[1]}' already exists.");
                string? hex = null;
                if (parts.Length >= 3)
                {
                    if (!TranslocatorStore.IsValidHex(parts[2]))
                        return TextCommandResult.Error("Colour must be #RRGGBB hex.");
                    hex = parts[2];
                }
                var g = Store.AddGroup(parts[1], hex);
                Store.SaveIfDirty();
                return TextCommandResult.Success($"Created group '{g.Name}' #{TranslocatorStore.ToHex(g.Color)}.");
            }
            case "color":
            case "colour":
                if (parts.Length < 3 || !TranslocatorStore.IsValidHex(parts[2]))
                    return TextCommandResult.Error("Usage: .tlgroup color <name> <#RRGGBB>");
                if (!Store.RecolorGroup(parts[1], parts[2]))
                    return TextCommandResult.Error($"No group '{parts[1]}'.");
                Store.SaveIfDirty();
                return TextCommandResult.Success($"Recoloured '{parts[1]}'.");
            case "rename":
                if (parts.Length < 3) return TextCommandResult.Error("Usage: .tlgroup rename <old> <new>");
                if (!Store.RenameGroup(parts[1], parts[2]))
                    return TextCommandResult.Error($"No group '{parts[1]}'.");
                Store.SaveIfDirty();
                return TextCommandResult.Success($"Renamed '{parts[1]}' -> '{parts[2]}'.");
            case "toggle":
                if (parts.Length < 2) return TextCommandResult.Error("Usage: .tlgroup toggle <name>");
                if (!Store.ToggleGroup(parts[1], out bool vis))
                    return TextCommandResult.Error($"No group '{parts[1]}'.");
                Store.SaveIfDirty();
                return TextCommandResult.Success($"Group '{parts[1]}' {(vis ? "shown" : "hidden")}.");
            case "del":
            case "delete":
                if (parts.Length < 2) return TextCommandResult.Error("Usage: .tlgroup del <name>");
                if (!Store.DeleteGroup(parts[1]))
                    return TextCommandResult.Error(
                        $"Can't delete '{parts[1]}' (no such group, or it's the default).");
                Store.SaveIfDirty();
                return TextCommandResult.Success(
                    $"Deleted '{parts[1]}'; its translocators moved to 'Discovered'.");
            default:
                return TextCommandResult.Error($"Unknown subcommand '{sub}'.");
        }
    }

    private void OnChatMessage(int groupId, string message, EnumChatType chattype, string data)
    {
        if (Store == null || string.IsNullOrEmpty(message)) return;
        if (!AutoScanChatShare) return; // opt-in; off by default
        var m = ShareLinkRegex.Match(message);
        if (!m.Success) return;
        string url = m.Groups[1].Value;
        // Skip echoes of links we just shared ourselves.
        if (_ownShareUrls.Contains(url)) return;
        _ = FetchAndIngestAsync(url);
    }

    private async Task FetchAndIngestAsync(string url)
    {
        _capi.Logger.Notification($"[translocatorpath] auto-import: fetching {url}");
        string body;
        try { body = await Http.GetStringAsync(url); }
        catch (Exception ex)
        {
            string detail = Describe(ex);
            Report($"[translocatorpath] fetch {url} failed: {Clip(detail)}",
                   $"fetch {url} failed: {detail}");
            return;
        }

        _capi.Logger.Notification(
            $"[translocatorpath] auto-import: fetched {body.Length} chars from {url}");
        var (added, err) = Store!.IngestText(body, url);
        if (err != null)
            Report($"[translocatorpath] import from {url} failed: {err}");
        else
            Report($"[translocatorpath] imported {added} new translocator(s) from {url}");
        Store?.SaveIfDirty();
    }

    /// <summary>Upload the current list to paste.rs and announce it in chat.
    /// Fire-and-forget; status surfaces through local chat messages.</summary>
    public void ShareViaChat()
    {
        if (Store == null) return;
        Store.ForceSave();
        _ = ShareToPasteAsync();
    }

    private async Task ShareToPasteAsync()
    {
        string body = Store!.BuildExportText();
        string url;
        try { url = await UploadAsync(body); }
        catch (Exception ex)
        {
            string detail = Describe(ex);
            Report($"[translocatorpath] paste.rs upload failed: {Clip(detail)}",
                   $"paste.rs upload failed: {detail}");
            return;
        }

        _ownShareUrls.Add(url);
        // The (message)-only overload posts to the player's active channel;
        // the groupId overload would always land in General.
        ToMainThread(() => _capi.SendChatMessage($"{ChatMarker} {url}"));
        Report($"[translocatorpath] shared to {url}");
    }

    private async Task<string> UploadAsync(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "text/plain");
        using var resp = await Http.PostAsync(PasteEndpoint, content);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadAsStringAsync()).Trim();
    }

    private void ToMainThread(Action action) =>
        _capi.Event.EnqueueMainThreadTask(action, "translocatorpath-share");
}
