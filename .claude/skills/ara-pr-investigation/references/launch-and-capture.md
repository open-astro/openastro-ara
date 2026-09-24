# Launching Ara on the PR's code and capturing what changed

Everything here happens in a **git worktree**, never in the user's main checkout.
The user's working tree may have uncommitted work (mobile config edits in
particular — never touch or revert them). A worktree also means the PR's build
output never collides with `master`'s.

## 1. Worktree on the PR head

```bash
ROOT=$(git rev-parse --show-toplevel)
git fetch origin "pull/$PR/head:refs/pr/$PR"
WT="$SCRATCH/pr-$PR"                       # $SCRATCH = the session scratchpad dir
git worktree add --detach "$WT" "refs/pr/$PR"
```

A fork PR's branch is not in `origin`; the `pull/N/head` ref is the only way to
get it, and it works for fork and same-repo PRs alike.

If the PR is far behind `master` (the facts script prints how far), also build a
**merge preview** so you review what would actually land, not stale context:

```bash
git -C "$WT" merge --no-commit --no-ff origin/master || echo "CONFLICTS — report them, review the PR head as-is"
```

Conflicts are a finding in their own right. Do not resolve them for the
contributor.

When done: `git worktree remove --force "$WT"`; `git update-ref -d refs/pr/$PR`.

## 2. Daemon (only when the PR touches server code, or the client change needs a live daemon to show)

Most client changes need a daemon to get past the connect screen. Run the PR's
daemon if the PR touches `.NET` paths (facts script: `dotnet=true`), otherwise
`master`'s from the main checkout is fine — either way use a throwaway profile
dir so the user's real profile is never written:

```bash
cd "$WT"    # or $ROOT for master's daemon
dotnet build OpenAstroAra.Server -c Debug; echo "build exit $?"      # check the exit code, not grep
scripts/build-astrometry-natives.sh OpenAstroAra.Server/bin/Debug/net10.0/
OPENASTROARA_PROFILE_DIR="$SCRATCH/ara-profile-$PR" ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --no-build --project OpenAstroAra.Server > "$SCRATCH/daemon-$PR.log" 2>&1 &
curl -s http://localhost:5555/healthz     # "ok"
```

`OPENASTROARA_PORT` moves the port if 5555 is already taken (check with
`lsof -i :5555` first — the user may have a daemon running). CFITSIO is only
needed for capture; `brew install cfitsio` if a capture path is under test.

**Real hardware:** the Pi rig at `openastro.lan` runs the released daemon with a
real camera/mount/focuser behind AlpacaBridge (see the `pi-rig-openastro-lan`
memory). Point the local client at it (`openastro.lan`, port 5555) when the
change only makes sense against real equipment. Never deploy a PR's daemon to
the Pi from this skill — that is a user decision.

## 3. Client (macOS host)

`flutter run` in the background exits and kills the app, so build once and
launch the `.app` detached:

```bash
cd "$WT/client/openastroara_client"
flutter pub get
flutter build macos --debug; echo "build exit $?"
open build/macos/Build/Products/Debug/openastroara.app
```

- Debug builds cannot mDNS-discover; if the first-run/connect screen appears,
  enter host `localhost` port `5555` by hand (or `openastro.lan`). The user's
  machine already has a saved server in `org.openastro.openastroara.plist`, so
  usually the app connects straight away — say which daemon it connected to.
- Linux hosts: `flutter build linux --debug` and run the bundle; on Wayland
  prefix `GDK_BACKEND=x11`.
- Do not run `dart format` on files the PR touched — it restyles whole files.

## 4. Drive it and capture

Work out from the diff which screen/tab/panel is affected (widget → screen
→ tab; `lib/screens/tabs/*.dart` names the top-level tabs). Then, in the running
app, get to that screen and capture the **window only**:

```bash
.claude/skills/ara-pr-investigation/scripts/capture-window.sh "OpenAstro Ara" "$SCRATCH/pr-$PR-<what>.png"
```

(It finds the window through CoreGraphics; `osascript`/System Events is
refused Accessibility access from this shell, so do not reach for it. Resizing
for a narrow-layout edge case therefore has to be done by hand or skipped —
say which.) Full-screen `screencapture -x out.png` is the fallback.

**Getting to the screen.** The app has no deep link or "open on tab X"
argument, so someone has to click. Two ways, in order of preference:

1. **Accessibility granted** to the terminal app (System Settings → Privacy &
   Security → Accessibility → the terminal you run Claude from). Test once with
   `osascript -e 'tell application "System Events" to get name of first window
   of process "OpenAstro Ara"'`; the -1719 error means it is not granted.
   Flutter ignores System Events' synthetic `click at`, so drive the app with
   real CoreGraphics events instead:

   ```bash
   scripts/click.sh <x> <y>                    # screen points, not pixels
   scripts/click.sh <x> <y> type 'M31\n'       # click a field, type, trailing \n = Return
   ```

   Get the window origin/size from System Events (`position`/`size` of the
   first window) and work out targets from a capture: a capture is 2× points
   on Retina and includes a shadow margin, so map through the window's
   position rather than reading pixel coordinates straight off the image.
   Known targets on a fresh 960×712 window at (x,y): hostname field
   (x+379, y+620), Use (x+909, y+620), Save & continue (x+122, y+661); on the
   main shell the Tonight's Sky button sits at (x+w-70, y+116).
2. **Not granted** (the default here): capture what the app opens on, then
   tell the user exactly what to click ("Planning tab → Tonight's Sky, expand a
   row") and ask them to say when they are there; capture again. If the session
   is unattended and nobody can click, report that the change could not be
   reached on screen and fall back to the widget-tree walkthrough below.

Capture at least:
1. **Golden path** — the changed flow as a user meets it.
2. **One edge case** — empty state, error state, narrow window, dark/night
   mode, or whatever the diff makes relevant.
3. **Before**, when the change is a modification rather than an addition: the
   same screen from a `master` build, so the reader sees the delta. Skip it for
   purely additive UI and say so.

Read every capture back with the Read tool and describe what is in it. A
screenshot nobody looked at proves nothing; the description is what goes in the
report ("the ⓘ button now sits at the right of each Tonight's Sky row; tapping
it opens a dialog with …"). If the app cannot be launched (toolchain missing,
build failure), say so in the report and fall back to a code-level walkthrough
of the widget tree — never present an unrun UI as verified.

## 5. Tear down

Quit the app (`pkill -x openastroara`), kill the daemon you
started (and only that one — match on the PID you backgrounded, not on
`pkill dotnet`), remove the worktree and the temp ref. Leave the user's
main checkout, branch, and profile exactly as found.
