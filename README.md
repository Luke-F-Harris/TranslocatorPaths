# TranslocatorPaths

A client-side [Vintage Story](https://www.vintagestory.at/) mod that draws a
line on the world map from every repaired static translocator to the location
it teleports you to, so you can see your translocator network at a glance.

## Features

- **Map lines** — each repaired translocator is drawn from its source to its
  exit, coloured by the group it belongs to.
- **Groups** — organise translocators into colour-coded groups; toggle each
  one's visibility independently.
- **Per-world save** — discovered translocators are saved automatically per
  savegame.
- **Sharing** — export your list (whole list or a single group) to a drop
  folder others can import, or 1-click upload to [paste.rs](https://paste.rs/) & auto-post
  the link in chat for auto-import by anyone who has opted in.
- **In-game GUI** — open with the `K` hotkey (or `.tlgui`) to manage groups and
  reassign translocators. Right-click a translocator endpoint on the map to
  change its group directly.

## Usage

| Command | Description |
| --- | --- |
| `.tlgui` | Open the manager GUI (also bound to `K`). |
| `.tl` | Status: groups, counts, share/import folder paths. |
| `.tlgroup add\|color\|rename\|toggle\|del <args>` | Manage groups. |
| `.tlassign <group>` | Assign the nearest translocator to a group. |
| `.tlexport [group]` | Export to the share folder (whole list, or one group). |
| `.tlimport` | Scan the import folder and merge other players' lists. |
| `.tlshare` | Upload your list to paste.rs and post the link to chat. |
| `.tllinesrescanhere` / `.tllinesrescanall` | Rescan the current chunk / all loaded chunks. |
| `.tllinesscan <sec>` | Set the rescan interval (default 3s). |
| `.tllinesmax <n>` | Set the max tracked translocators (default 2000). |
| `.tllinesthickness <px>` | Set the on-map line thickness (default 2.5px). |

A translocator only appears once it has been **repaired** and its chunk has
been scanned. Chunks are scanned at most once, so use *Rescan Here* after
repairing a translocator you're standing next to.

## Building

The project targets `net10.0` and builds against a local Vintage Story install.
Set the `VINTAGE_STORY` environment variable to your install directory, then:

```sh
dotnet build TranslocatorPath.csproj -c Release
```

The referenced assemblies (`VintagestoryAPI`, `VintagestoryLib`, `0Harmony`,
`VSEssentials`, `VSSurvivalMod`) are resolved from `$(VINTAGE_STORY)` and are
not redistributed here.

## License

[GPL-3.0](LICENSE)
