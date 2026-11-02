# Simple Mode message pills: design brief (phase A)

Shaped 2026-11-02 (`/impeccable shape`). Paperclip: ALP-4825 (part of ALP-4810). Parent brief: [`simple-mode.md`](simple-mode.md). Visual rules: `DESIGN.md`, "Simple Mode: Image First".

## Job and audience

- **Who:** the Simple Mode user, at the rig or on the couch, at night, mid-task, often half dark-adapted.
- **Job:** learn what just happened (a result, a failure, a question) without a popup. Messages sit in the image, out of the way, and never block a control.
- **Mode:** Operate. Precision and restraint; nothing decorative.

## The three kinds

| Kind | Look | Lifetime |
|---|---|---|
| Result | green dot, title, optional detail | fades after 6 s |
| Error | red outline, title, detail in `error-text` | stays until × |
| Question | amber outline, title, its buttons | stays until answered |

## Placement and stacking

- Centred under the status strip, below the Autopilot strip or offer when one shows.
- Never over the plan squares (top right) or the status pill (top left); on a narrow window the stack drops below both.
- Newest on top. After three, the rest fold into one "+N more" pill.
- In the immersive view errors and questions stay; results fade as usual.

## What switches in phase A

Simple Mode only. One call (`notifyUser`) shows a pill in Simple Mode and today's snackbar in the full UI, so the full UI is unchanged.

- Focus: nudge errors.
- Polar align: start, stop and take-frame errors.
- Take One, live view and abort errors (shared with the Live tab).
- The Emergency Stop result.
- The planetarium's "needs internet" note.
- Target search without its dialog: a name the server resolves centres the sky; pasted coordinates give an error pill.
- Emergency Stop from a keyboard or screen reader: press again within 3 s (the button reads PRESS AGAIN, and screen readers announce it) instead of the confirm dialog. The full UI keeps its dialog.

## Access and motion

- Each pill is announced to screen readers when it appears.
- × and question buttons are at least 44 px.
- Reduced motion: no slide, the pill just appears and disappears.
- Night mode tints the pills like everything else (in-app, so the red filter applies; a system notification would not, which is one reason these are not OS notifications).

## Not in phase A

The daemon's prompts: takeover request and session transferred, safety alarm (must stay unmissable, with its audible escalation), bedtime checklist, morning summary, manual filter swap, auto flats and calibration, profile replay. Phase B, next slice, each keeping its automation.

## Next

A rendered mock of the pills over the Simple Mode shell for a pick, then the build, test first.
