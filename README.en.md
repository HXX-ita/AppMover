# AppMover

Move applications and data folders off your system drive (C:) onto another disk —
**while keeping the software working**.

> Status: M3 complete. 123 automated checks green (86 CLI regression + 37 UI smoke).
> Next: release packaging.
>
> **Windows only.** macOS / Linux are explicitly out of scope — directory junctions,
> Restart Manager, registry uninstall entries and NTFS ACLs are Windows-specific
> mechanisms; pretending otherwise would produce a worse tool for everyone.

[中文说明](README.md)

---

## The problem

Your C: drive fills up while another disk sits mostly empty. You cannot just cut
and paste `C:\Program Files\Foo` to `D:\Foo`, because the registry, shortcuts,
services, `%PATH%` and the uninstall entry all reference absolute paths. The
application breaks.

## What AppMover does

```
copy to target disk  →  verify  →  rename source aside  →  create a junction
                     →  verify the junction  →  delete the old copy
```

A **directory junction** is an NTFS reparse point that is completely transparent to
applications: code opening `C:\Program Files\Foo\x.dll` is redirected by the
filesystem to `D:\Foo\x.dll`, and the path string never changes. That is why the
software keeps working.

### Safety design

The order above is deliberately **not** "copy → verify → delete source → create
junction". Deleting the source is irreversible while creating a junction can fail
(permissions, target in use, disk error). If the irreversible step came first, a
failure would leave you with a destroyed application and an empty path.

Instead the source is **renamed aside** — a metadata operation that is instant and
reversible — and the junction is created in the freed path. If anything fails, the
directory is renamed back. **There is never a moment when both copies are gone.**

| Failure point | Disk state | Recovery |
|---|---|---|
| copy / verify | source intact, partial target copy | delete target copy, source untouched |
| rename | source intact | nothing to do |
| create/verify junction | source renamed, data intact | remove junction, rename back, clean target |
| delete old copy | target data + junction both fine | not a failure — just report the leftover path |

Undo is available for every completed relocation, from the **History** tab.

## Usage

**Normal users** — double-click `AppMover.exe`:

1. Pick a target disk (defaults to the non-system disk with the most free space)
2. Click **Scan** and wait for the folder list (each entry shows its category and the
   reason for that category)
3. Select an entry → **Move selected** → review the confirmation dialog (risks and
   required actions are listed) → tick the box → start
4. If something goes wrong, use **Undo** on the History tab

**Power users / troubleshooting** — `AppMover-cli.exe`:

```
AppMover-cli.exe volumes        # enumerate volumes and their eligibility as targets
AppMover-cli.exe classify PATH  # category and evidence for one folder
AppMover-cli.exe scan           # scan standard locations
AppMover-cli.exe plan PATH      # plan a move (read-only, includes occupancy check)
AppMover-cli.exe move PATH      # perform the move
AppMover-cli.exe journal        # relocation records
AppMover-cli.exe undo ID        # undo a relocation
AppMover-cli.exe feedback       # generate a redacted diagnostic report
AppMover-cli.exe selftest       # built-in regression suite
```

`plan` and `move` accept `--to D:\`, `--force`, `--dry-run`.

## Classification

Every folder is classified with the **evidence** shown, not just a verdict:

| Category | Meaning |
|---|---|
| 🟢 Safe to move | data folders, caches, portable apps |
| 🟠 Move with care | installed software under Program Files, folders containing services or auto-updaters |
| 🔴 Do not move | Windows itself, shared components, drivers, MSIX/Store apps, uninstall information, cloud sync folders |
| 🟣 Likely leftover | caches and installer remnants — **advice only, never auto-deleted** |

### Notable rules learned from real data

- **Cloud sync folders are blocked explicitly.** Windows masks the `ReparsePoint`
  attribute of cloud placeholders (OneDrive) on a *per-process* basis — measured on
  the same folder: PowerShell 5.1 reads `1073` (bit set), this program reads `49`
  (bit clear), even with a Windows 10 compatibility manifest embedded. Moving such a
  folder would trigger a **full download** of every cloud file. So detection is based
  on sync roots (environment variables, registry sync-engine entries, and path
  segments) instead of the masked attribute.
- **"No uninstall registry entry" is a very weak signal for AppData.** The first
  version of the rules wrongly flagged CapCut, WeChat, miHoYo and game saves as
  leftovers. AppData is now only split into "cache" vs "data"; a missing registry
  entry never implies leftover.
- **32-bit system folders have different names than assumed** — the real folder is
  `WindowsPowerShell` (no space), not `Windows PowerShell`.

## Feedback

There is **no self-hosted backend**. Feedback goes through GitHub Issues: the program
generates a redacted report and prints a pre-filled "new issue" link. Reports contain
folder names, paths, verdicts and environment versions — **never file contents** —
and user/machine names are replaced with `<user>` / `<PC>`.

```
AppMover-cli.exe feedback --path "C:\some\folder" --open
AppMover-cli.exe feedback --unknown        # collect folder names the rules are unsure about
```

See [docs/FEEDBACK.md](docs/FEEDBACK.md).

## Build

Requires **no .NET SDK** — it compiles with the `csc.exe` that ships with
.NET Framework 4.8, which is present on every Windows 10/11 machine. That is a
deliberate distribution choice: the result is a ~90 KB executable with no runtime
to install.

```powershell
powershell -ExecutionPolicy Bypass -File src\build.ps1                 # build both exes
powershell -ExecutionPolicy Bypass -File src\build.ps1 -Run selftest  # 86 core checks
powershell -ExecutionPolicy Bypass -File src\build.ps1 -SmokeUi       # 37 UI checks
```

Output:

- `build\AppMover.exe` — GUI (WinExe)
- `build\AppMover-cli.exe` — console

Two executables rather than one: a WinExe subsystem does not attach to the parent
console, so CLI output would be silently lost, and AttachConsole + reopening stdout
in one binary is fragile and hard to test. Both share the same `Core`.

### Verifying a GUI without looking at it

WinForms has failure modes that only appear during real layout (wrong docking order
covering a control; anchoring computed while the parent width is still 0, putting a
button outside the window). A compile is no help. So the program has a headless smoke
mode: `AppMover.exe --smoke <report-file>` actually calls `Show()` and then asserts on
**actual control geometry**:

- key controls fully inside their parent's client area
- the top toolbar does not overlap the results area (`toolbar bottom 76 <= results top 76`)
- dialog buttons stay inside the window
- the "start" button is **disabled** when there are blockers

That assertion was validated by **mutation testing**: reintroducing the wrong docking
order makes it fail immediately with `results top 0`.

## Project layout

```
src/
  build.ps1                  build script (csc.exe, no SDK)
  AppMover/
    app.manifest             manifest (Win10 compatibility / long paths / DPI)
    Core/                    rules, relocation, junctions, journal, feedback
    Cli/                     console entry point and regression suite
    Gui/                     WinForms UI and the UI smoke test
docs/
  SPEC.md                    design document (rules, flow, risks, permissions)
  FEEDBACK.md                feedback mechanism design
  feedback.html              static feedback page (GitHub Pages ready)
tools/                       reconnaissance scripts (not shipped)
```

## License

MIT — see [LICENSE](LICENSE). The license file also states, plainly, that any disk
relocation carries risk.
