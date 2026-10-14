# AppMover

Move software and data folders off your C: drive to another disk, **and everything keeps working**.

Windows 10 / 11 only. No installation, no runtime to install.

---

## Download and run

1. Open the [Releases page](https://github.com/HXX-ita/AppMover/releases) and download
   **`AppMover-x.y.z-win-x64.zip`**
2. Extract it anywhere (for example `D:\AppMover`)
3. Double-click **`AppMover.exe`**

> ⚠️ **Don't download "Source code (zip)"** — that's the source, not the program.
> Pick the file whose name contains `win-x64`.

The extracted folder looks like this, and the program sits at the top level:

```
AppMover.exe          <- double-click this
AppMover.exe.config
AppMover-cli.exe      command line version (usually not needed)
AppMover-cli.exe.config
feedback.html
feedback.json
README.md
README.en.md
LICENSE
```

> If you built it from source yourself, the binaries live in `build\`
> (`build\AppMover.exe`). The release package has **no** `build` folder.

---

## Using the window

1. **Pick a target disk** — by default it selects the non-system disk with the most free space
2. **Click "Start scan"** — it lists directories on C:, each with a category and the reason for it
3. **Select an item, then click "Move selected"** (or double-click it)
4. **Read the confirmation dialog**, tick the checkbox, and only then does it start
5. Not happy with the result? Go to **"History & undo"** and undo it in one click

**Every folder is confirmed individually.** There is deliberately no "move everything" button.

---

## Four categories

| Colour | Meaning | What to do |
|---|---|---|
| Safe to move | Data folders, caches, portable apps | Go ahead |
| Movable with care | Installed apps under `Program Files`, ones with services or auto-updaters | Read the dialog first |
| Do not move | Windows itself, shared components, drivers, Store apps, uninstall entries | The program refuses |
| Possibly leftover | Caches and installer leftovers | **Suggestion only, never deleted automatically** |

Each item tells you **why** it was classified that way, rather than just handing you a verdict.

---

## How it works

```
copy to target disk -> verify -> delete the original -> create a junction in its place
```

A junction is transparent to software: when a program opens `C:\Program Files\Foo\x.dll`,
the filesystem redirects it to `D:\Foo\x.dll`, while **the path string itself never changes**.
Registry entries, shortcuts and service configs all still point at the old path, so they keep working.

---

## When administrator rights are needed

| Task | Needs |
|---|---|
| Scanning | Normal user |
| Moving `AppData` or user folders | Normal user |
| Moving `Program Files` / `ProgramData` | **Administrator** |
| Stopping / starting services | **Administrator** |

It asks explicitly when needed and never elevates silently.

---

## Safety rules it follows

- **Never deletes the source** before verification passes; any failure leaves the original usable
- One confirmation per folder
- Writes an undo journal, so any move can be reverted
- Has a dry-run mode that only produces a plan
- If a move was interrupted (power loss, crash), `recover` repairs it

---

## Command line (advanced / troubleshooting)

Use `AppMover-cli.exe` (it shares the same core as the GUI):

```
volumes                list volumes and whether each can be a target
rules                  print the classification rules
tree <path>            size and file count of a directory
classify <path>        category and reasons for one directory
scan [--json file]     scan standard locations and classify
plan <path>            plan a move (read-only, touches nothing)
move <path>            perform a move
journal                show move records (undo needs a record number)
undo <record>          undo a completed move
recover                repair an interrupted move
                       --restore <record> / --complete <record>
feedback               generate a redacted report and a prefilled issue link
selftest               built-in self-check (173 assertions)
```

`plan` / `move` options: `--to D:\`, `--dry-run`, `--allow-same-volume`, `--force`

---

## FAQ

**Antivirus flags it.**
It copies, creates junctions and deletes directories, so behaviourally it looks like a
"dangerous tool". The full source is in this repository, so you can build it yourself and compare.

**A program stopped working after a move.**
Undo it under "History & undo". Undo is the inverse operation and verifies as it goes.

**Can it move Windows folders or drivers?**
No, it refuses. Moving those breaks the system.

**Did it actually free up C:?**
Yes, but a junction stays behind in the original location (a few dozen bytes).
Delete it and the software can no longer find its files.

**Why two executables?**
The `WinExe` subsystem doesn't attach to the parent console, so command-line output is lost.
Merging both into one binary requires manual `AttachConsole` and stdout reopening, which is
fragile and hard to test. Two executables sharing one core is the more reliable arrangement.

---

## Building from source

You need the `csc.exe` that ships with Windows (.NET Framework 4.8, **no .NET SDK required**).

```powershell
git clone https://github.com/HXX-ita/AppMover.git
cd AppMover
powershell -ExecutionPolicy Bypass -File src\build.ps1
# output goes to build\ : build\AppMover.exe / build\AppMover-cli.exe
```

Run the self-check:

```powershell
build\AppMover-cli.exe selftest
```

---

## Feedback

The "Feedback" button in the app produces a **redacted** diagnostic report
(no user name, no paths) and opens a prefilled issue page.

## License

MIT, see [LICENSE](LICENSE). Provided as is; make sure your data is backed up before moving.
