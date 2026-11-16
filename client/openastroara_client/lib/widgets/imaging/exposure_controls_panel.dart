import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/filter_wheel_status.dart';
import '../../state/equipment/camera_state.dart';
import '../../state/equipment/filter_choices_state.dart';
import '../../state/equipment/filter_wheel_state.dart';
import '../../state/equipment/filter_wheel_command_state.dart';
import '../../theme/ara_metrics.dart';
import '../../util/reduce_motion.dart';
import '../../state/imaging/exposure_state.dart';
import '../../state/simple_messages_state.dart';
import '../../state/settings/filter_wheel_labels_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/friendly_error.dart';
import '../../l10n/l10n.dart';

/// Right-side controls in the Imaging tab per §25.5.1 — exposure / gain /
/// offset / bin / filter / frame type + Take One + Live View toggle.
/// Pure presentation; mutates ExposureController. "Take One" + "Live View"
/// are wired to no-op handlers here; Phase 12c.2 connects them to the
/// daemon's /api/v1/sequence/exposure endpoint.
class ExposureControlsPanel extends ConsumerWidget {
  final VoidCallback? onTakeOne;
  final ValueChanged<bool>? onLiveViewToggle;
  final bool liveViewOn;

  const ExposureControlsPanel({
    super.key,
    this.onTakeOne,
    this.onLiveViewToggle,
    this.liveViewOn = false,
  });

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final params = ref.watch(exposureControllerProvider);
    final ctrl = ref.read(exposureControllerProvider.notifier);
    final exposureCaps = switch (ref.watch(cameraStatusProvider)) {
      AsyncData(:final value) => value?.capabilities,
      _ => null,
    };

    // Width, background and border come from the imaging tab's rail (which
    // also owns scrolling) — this is the compact two-column control grid.
    Widget pair(Widget left, Widget right) => Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Expanded(child: left),
            const SizedBox(width: 8),
            Expanded(child: right),
          ],
        );
    return Padding(
      padding: const EdgeInsets.all(12),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          pair(
            DurationSecondsField(
              label: 'Exposure (s)',
              value: params.exposure,
              minSeconds: exposureCaps?.minExposureSec ?? 0,
              maxSeconds: exposureCaps?.maxExposureSec ?? 0,
              onChanged: ctrl.setExposure,
            ),
            IntField(
              label: 'Gain',
              value: params.gain,
              min: 0,
              max: 1000,
              onChanged: ctrl.setGain,
            ),
          ),
          const SizedBox(height: 6),
          pair(
            IntField(
              label: 'Offset',
              value: params.offset,
              min: 0,
              max: 200,
              onChanged: ctrl.setOffset,
            ),
            _BinDropdown(
              value: params.bin,
              onChanged: ctrl.setBin,
            ),
          ),
          const SizedBox(height: 6),
          // PR #71 follow-up — the filter picker, wired to params.filterSlot
          // (sent as `filter_name` on every capture; until now only the 'L'
          // default ever went up because nothing set it). Choices come from the
          // profile's wheel slot labels (daemon-authoritative via the 12h.2b
          // round-trip), same source as the sequence editor's picker.
          pair(
            FilterDropdown(
              value: params.filterSlot,
              onChanged: ctrl.setFilterSlot,
            ),
            DropdownButtonFormField<FrameKind>(
              initialValue: params.frameKind,
              decoration: const InputDecoration(labelText: 'Frame type'),
              items: const [
                DropdownMenuItem(value: FrameKind.light, child: Text('Light')),
                DropdownMenuItem(value: FrameKind.dark, child: Text('Dark')),
                DropdownMenuItem(value: FrameKind.bias, child: Text('Bias')),
                DropdownMenuItem(value: FrameKind.flat, child: Text('Flat')),
              ],
              onChanged: (k) {
                if (k != null) ctrl.setFrameKind(k);
              },
            ),
          ),
          const SizedBox(height: 12),
          Row(
            children: [
              Expanded(
                child: FilledButton.icon(
                  onPressed: onTakeOne,
                  icon: const Icon(Icons.camera_alt),
                  label: const Text('Take One'),
                ),
              ),
              const SizedBox(width: 10),
              Text('Live', style: Theme.of(context).textTheme.bodySmall),
              const SizedBox(width: 2),
              Switch(value: liveViewOn, onChanged: onLiveViewToggle),
            ],
          ),
        ],
      ),
    );
  }
}

/// Exposure in seconds, fractions allowed (0.5 s for bright targets and
/// flats). Millisecond resolution — what the daemon request carries — with a
/// floor at the camera's ExposureMin and a ceiling at its ExposureMax when it
/// reports them (0 = not reported).
class DurationSecondsField extends StatefulWidget {
  final String label;
  final Duration value;
  final double minSeconds;
  final double maxSeconds;
  final ValueChanged<Duration> onChanged;
  const DurationSecondsField({
    super.key,
    required this.label,
    required this.value,
    required this.onChanged,
    this.minSeconds = 0,
    this.maxSeconds = 0,
  });

  @override
  State<DurationSecondsField> createState() => _DurationSecondsFieldState();
}

/// Upper sanity bound when the camera reports no ExposureMax, so an input like
/// 1e300 cannot overflow Duration.
const int _kMaxExposureMs = 24 * 3600 * 1000;

/// "5" for whole seconds, "0.5" / "0.001" otherwise (no trailing zeros).
String _formatSeconds(Duration d) {
  final ms = d.inMilliseconds;
  if (ms % 1000 == 0) return (ms ~/ 1000).toString();
  return (ms / 1000)
      .toStringAsFixed(3)
      .replaceFirst(RegExp(r'0+$'), '');
}

class _DurationSecondsFieldState extends State<DurationSecondsField> {
  late final TextEditingController _ctrl;
  final FocusNode _focus = FocusNode();

  @override
  void initState() {
    super.initState();
    _ctrl = TextEditingController(text: _formatSeconds(widget.value));
    // Commit on focus loss too — "type 1, click Take One" must shoot 1s, not
    // silently revert to the last submitted value (Enter alone was the only
    // commit path, so an un-submitted edit snapped back on the next rebuild).
    _focus.addListener(() {
      if (!_focus.hasFocus) _commit(_ctrl.text);
    });
  }

  void _commit(String s) {
    // Accept a comma decimal separator too (European keyboards).
    final parsed = double.tryParse(s.trim().replaceFirst(',', '.'));
    if (parsed == null || !parsed.isFinite || parsed <= 0) {
      // Zero, negative or unparseable: the controller would ignore it, so
      // show the value that is actually in force.
      _ctrl.text = _formatSeconds(widget.value);
      return;
    }
    // Clamp in seconds before converting so a huge input never reaches
    // Duration; any positive input is at least 1 ms, the request's resolution.
    final maxSec = widget.maxSeconds, minSec = widget.minSeconds;
    final maxMs = maxSec.isFinite && maxSec > 0
        ? (maxSec * 1000).floor().clamp(1, _kMaxExposureMs)
        : _kMaxExposureMs;
    final minMs = minSec.isFinite && minSec > 0
        ? (minSec * 1000).ceil().clamp(1, maxMs)
        : 1;
    final ms =
        (parsed.clamp(0, maxMs / 1000) * 1000).round().clamp(minMs, maxMs);
    final d = Duration(milliseconds: ms);
    widget.onChanged(d);
    _ctrl.text = _formatSeconds(d);
  }

  @override
  void didUpdateWidget(covariant DurationSecondsField old) {
    super.didUpdateWidget(old);
    // Never fight the user's in-progress edit.
    if (_focus.hasFocus) return;
    final expected = _formatSeconds(widget.value);
    if (_ctrl.text != expected) _ctrl.text = expected;
  }

  @override
  void dispose() {
    _focus.dispose();
    _ctrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return TextField(
      controller: _ctrl,
      focusNode: _focus,
      decoration: InputDecoration(labelText: widget.label),
      keyboardType: const TextInputType.numberWithOptions(decimal: true),
      onSubmitted: _commit,
    );
  }
}

class IntField extends StatefulWidget {
  final String label;
  final int value;
  final int min;
  final int max;
  final ValueChanged<int> onChanged;
  const IntField({
    super.key,
    required this.label,
    required this.value,
    required this.min,
    required this.max,
    required this.onChanged,
  });

  @override
  State<IntField> createState() => _IntFieldState();
}

class _IntFieldState extends State<IntField> {
  late final TextEditingController _ctrl;
  final FocusNode _focus = FocusNode();

  @override
  void initState() {
    super.initState();
    _ctrl = TextEditingController(text: widget.value.toString());
    // Same commit-on-focus-loss stance as the exposure field: an edit the
    // user walks away from is an edit they meant.
    _focus.addListener(() {
      if (!_focus.hasFocus) _commit(_ctrl.text);
    });
  }

  void _commit(String s) {
    final parsed = int.tryParse(s.trim());
    if (parsed != null && parsed >= widget.min && parsed <= widget.max) {
      widget.onChanged(parsed);
    } else {
      _ctrl.text = widget.value.toString();
    }
  }

  @override
  void didUpdateWidget(covariant IntField old) {
    super.didUpdateWidget(old);
    if (_focus.hasFocus) return;
    final expected = widget.value.toString();
    if (_ctrl.text != expected) _ctrl.text = expected;
  }

  @override
  void dispose() {
    _focus.dispose();
    _ctrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return TextField(
      controller: _ctrl,
      focusNode: _focus,
      decoration: InputDecoration(labelText: widget.label),
      keyboardType: TextInputType.number,
      onSubmitted: _commit,
    );
  }
}

/// Binning for manual captures, as a dropdown of the camera's supported
/// symmetric modes (1x1, 2x2, …). Option A — client-only: the list is derived
/// from the capabilities' bin range so it works with any daemon (the daemon
/// already guarantees both axes accept every value up to max). When no camera is
/// connected (or its caps are absent) it falls back to 1..8, matching the
/// numeric field it replaces.
///
/// The payload always carries the same value for binX and binY, so only the
/// symmetric modes both axes support are offered: the span
/// [max(minBinX, minBinY) .. min(maxBinX, maxBinY)]. A stored bin outside that
/// span (a camera swap after a sequence seeded a higher value) is clamped —
/// and the clamp is WRITTEN BACK to [ExposureParams.bin] (post-frame), not
/// just rendered: the raw stored value is what a capture submits, so a
/// display-only clamp would still fire an exposure with an invalid bin.
class _BinDropdown extends ConsumerWidget {
  final int value;
  final ValueChanged<int> onChanged;
  const _BinDropdown({required this.value, required this.onChanged});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final caps = switch (ref.watch(cameraStatusProvider)) {
      AsyncData(:final value) => value?.capabilities,
      _ => null,
    };
    var lo = 1;
    var hi = 8;
    if (caps != null && caps.maxBinX > 0 && caps.maxBinY > 0) {
      final l = caps.minBinX > caps.minBinY ? caps.minBinX : caps.minBinY;
      final h = caps.maxBinX < caps.maxBinY ? caps.maxBinX : caps.maxBinY;
      // A malformed capability report (min above max) keeps the 1..8
      // fallback instead of silently collapsing the range to one entry.
      if (l <= h) {
        lo = l < 1 ? 1 : l;
        hi = h < lo ? lo : h;
      }
    }
    final shown = value.clamp(lo, hi);
    if (shown != value) {
      // Correct the state, not just the pixels (see class doc). Post-frame:
      // notifying the exposure controller mid-build is illegal. `shown` is
      // in-range, so this fires at most once — no rebuild loop.
      WidgetsBinding.instance.addPostFrameCallback((_) => onChanged(shown));
    }
    return DropdownButtonFormField<int>(
      // FormField reads initialValue in initState only — remount when the
      // range changes so capabilities arriving after the first build (the
      // provider starts AsyncLoading) can't leave a selection outside the
      // new items list (the exactly-one-item-per-value crash).
      key: ValueKey('bin-$lo-$hi'),
      initialValue: shown,
      decoration: const InputDecoration(labelText: 'Bin'),
      items: [
        for (var b = lo; b <= hi; b++)
          DropdownMenuItem(value: b, child: Text('${b}x$b')),
      ],
      onChanged: (v) {
        if (v != null) onChanged(v);
      },
    );
  }
}

/// Filter choice for manual captures. Options are the labelled wheel slots;
/// a stored value not among them (a renamed slot, or a profile switch) stays
/// selectable rather than being silently dropped — same stance as the §38
/// editor's picker. When no slots are labelled at all, the control still
/// offers the current value so the capture keeps a filter name.
///
/// Rendered as a [DropdownButtonFormField] — the same widget as the sibling
/// Frame-type picker, so the two boxes are pixel-identical and the value
/// renders reliably on every platform (a bare controlled [DropdownButton]
/// showed a blank value in the web build).
///
/// While a move is in flight the picker is DISABLED and its underline pulses
/// red — instantly on the click, not when the first poll reports the wheel
/// turning. When the wheel is observed on the commanded slot it re-enables
/// and the underline flashes green to confirm; a move that never lands snaps
/// the field back to the truthful value (via a keyed rebuild).

class FilterDropdown extends ConsumerStatefulWidget {
  final String value;
  final ValueChanged<String> onChanged;

  /// Draws the picker in place of the dropdown (Simple Mode has no popups):
  /// the names, the one showing, and the pick (null while the wheel moves).
  final Widget Function(
    List<String> names,
    String value,
    ValueChanged<String>? pick,
  )? inline;

  const FilterDropdown({
    super.key,
    required this.value,
    required this.onChanged,
    this.inline,
  });

  @override
  ConsumerState<FilterDropdown> createState() => _FilterDropdownState();
}

class _FilterDropdownState extends ConsumerState<FilterDropdown>
    with SingleTickerProviderStateMixin {
  /// Bumped whenever a commanded wheel move is rejected/failed, forcing the
  /// FormField to rebuild from the authoritative [value].
  int _resetToken = 0;

  /// Green while a landing is confirmed; the move itself, its stall timers and
  /// the plan question are the shared device command's (ADR 0013).
  bool _landed = false;

  /// Drives the red busy pulse (repeat/reverse) and the green confirm flash.
  late final AnimationController _flash;

  /// The name being moved to: what an inline picker shows meanwhile, and the
  /// tag an unnamed slot keeps once the wheel lands on it.
  String? _picked;
  bool _pickedUnnamed = false;

  @override
  void initState() {
    super.initState();
    // Created here (not as a `late final` field) so dispose() can always
    // dispose it — a lazy creation on first use could otherwise fire during
    // teardown and crash the vsync lookup.
    _flash = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 600),
    );
  }

  @override
  void dispose() {
    _flash.dispose();
    super.dispose();
  }

  /// The underline color while busy: green blinks while the wheel moves, as
  /// in Simple Mode (DESIGN.md status rule; steady with reduce motion), and a
  /// green flash confirms a landing; null restores the theme default.
  Color? _blinkColor(bool busy) {
    if (_landed || busy) {
      return Color.lerp(AraColors.accentConnected, AraColors.border, _flash.value);
    }
    return null;
  }

  void _onOutcome(DeviceCommand next) {
    if (next.phase == DeviceCommandPhase.landed) {
      // An UNNAMED target slot can never be synced by the follow-logic (it
      // only latches named slots), so tag the picked local label instead.
      if (_pickedUnnamed && _picked != null) widget.onChanged(_picked!);
      setState(() => _landed = true);
      _flash
        ..stop()
        ..value = 0;
      _flash.forward().whenComplete(() {
        if (mounted) setState(() => _landed = false);
      });
      return;
    }
    // Refused or reverted: the field snaps back to the truthful value.
    setState(() => _resetToken++);
    final l10n = context.l10n;
    switch (next.refusal) {
      case DeviceCommandRefusal.busy:
        userNotifier(context)(l10n.filterDropdownBusy, refused: true);
      case DeviceCommandRefusal.failed:
        userNotifier(context)(
          friendlyError(l10n, next.error!, action: l10n.filterDropdownActionChange),
          error: true,
        );
      case DeviceCommandRefusal.didntMove:
        userNotifier(context)(l10n.filterDropdownDidntMove, error: true);
      case null:
        break;
    }
  }

  /// While a plan runs a change asks first: a dialog here, in place inline
  /// (Simple Mode has no popups).
  Future<void> _ask() async {
    final cmd = ref.read(filterWheelCommandProvider.notifier);
    final l10n = context.l10n;
    final go = await showDialog<bool>(
      context: context,
      builder: (ctx) => AlertDialog(
        content: Text(l10n.devicePlanRunningChangeFilter),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(ctx).pop(false),
            child: Text(l10n.planCancel),
          ),
          FilledButton(
            onPressed: () => Navigator.of(ctx).pop(true),
            child: Text(l10n.deviceChangeAnyway),
          ),
        ],
      ),
    );
    if (!mounted) return;
    if (go ?? false) {
      cmd.confirm();
    } else {
      cmd.cancel();
      // The field showed the pick; it goes back to the filter in place.
      setState(() => _resetToken++);
    }
  }

  @override
  Widget build(BuildContext context) {
    ref.listen(filterWheelCommandProvider, (prev, next) {
      // Only a move this picker asked for: another picker's question and
      // outcome are its own to show.
      if (next.by != this) return;
      if (next.phase == DeviceCommandPhase.asking &&
          prev?.phase != DeviceCommandPhase.asking &&
          widget.inline == null) {
        unawaited(_ask());
      }
      if (next.outcomes != prev?.outcomes) _onOutcome(next);
    });
    final command = ref.watch(filterWheelCommandProvider);
    // Wheel moves from OTHER sources (the §37.4 panel, a sequence) are busy
    // too — the picker is disabled + red until the wheel settles.
    final wheelMoving = ref.watch(
      filterWheelProvider.select((w) => w.value?.isMoving ?? false),
    );
    final busy = command.phase == DeviceCommandPhase.moving || wheelMoving;
    if (busy && reduceMotionOf(context)) {
      _flash
        ..stop()
        ..value = 0;
    } else if (busy && !_flash.isAnimating) {
      _flash
        ..value = 0
        ..repeat(reverse: true);
    } else if (!busy && !_landed && _flash.isAnimating) {
      _flash.stop();
    }
    // The connected wheel's names, else the profile labels — deduped
    // keep-first by [filterChoicesProvider] (a duplicate would crash the
    // picker's exactly-one-item-per-value assertion).
    final names = [for (final c in ref.watch(filterChoicesProvider)) c.name];
    if (!names.contains(widget.value)) names.insert(0, widget.value);

    InputDecoration decoration() {
      final blink = _blinkColor(busy);
      return InputDecoration(
      labelText: context.l10n.filterDropdownLabel,
      enabledBorder: blink == null
          ? null
          : UnderlineInputBorder(
              borderSide: BorderSide(color: blink, width: 2)),
      focusedBorder: blink == null
          ? null
          : UnderlineInputBorder(
              borderSide: BorderSide(color: blink, width: 2)),
      disabledBorder: blink == null
          ? null
          : UnderlineInputBorder(
              borderSide: BorderSide(color: blink, width: 2)),
      );
    }

    final ValueChanged<String?>? pick = busy
          ? null // keep it disabled (showing the picked slot) until it lands
          : (n) {
              if (n == null) return;
              final wheelNow = ref
                  .read(filterWheelProvider)
                  .maybeWhen(data: (s) => s, orElse: () => null);
              // No wheel connected: nothing to move — just tag the capture
              // (the offline-authoring case; a reconnect re-syncs the picker).
              if (wheelNow == null || !wheelNow.isConnected) {
                widget.onChanged(n);
                return;
              }
              final slot = _resolveSlot(ref, wheelNow, n);
              if (slot == null) {
                // Connected, but the picked name maps to no physical slot
                // (local labels and the driver's names can diverge).
                userNotifier(context)(
                  context.l10n.filterDropdownNotASlot(n),
                  error: true,
                );
                return;
              }
              // currentSlot may be null (unknown/not yet reported) — treat
              // that as "not the current slot" and command the move; the
              // driver rejects a redundant move if one slips through.
              if (slot.position == wheelNow.currentSlot) {
                widget.onChanged(n);
                return;
              }
              _picked = n;
              _pickedUnnamed = slot.name.isEmpty;
              // No onChanged(n) for NAMED target slots — the follow-logic
              // syncs filterSlot when the wheel reports its new slot.
              ref
                  .read(filterWheelCommandProvider.notifier)
                  .request(slot.position, by: this);
            };
    final inline = widget.inline;
    if (inline != null) {
      final picker = inline(
        names,
        busy ? (_picked ?? widget.value) : widget.value,
        pick,
      );
      if (command.phase != DeviceCommandPhase.asking || command.by != this) {
        return picker;
      }
      final cmd = ref.read(filterWheelCommandProvider.notifier);
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        mainAxisSize: MainAxisSize.min,
        spacing: AraSpace.s8,
        children: [
          picker,
          Text(context.l10n.devicePlanRunningChangeFilter),
          Wrap(
            alignment: WrapAlignment.end,
            spacing: AraSpace.s8,
            children: [
              TextButton(onPressed: cmd.cancel, child: Text(context.l10n.planCancel)),
              FilledButton(onPressed: cmd.confirm, child: Text(context.l10n.deviceChangeAnyway)),
            ],
          ),
        ],
      );
    }
    final items = [
      for (final n in names) DropdownMenuItem(value: n, child: Text(n)),
    ];
    // Only the field follows each tick of the blink (its underline reads
    // _flash), not the whole picker's build.
    return AnimatedBuilder(
      animation: _flash,
      builder: (context, _) => DropdownButtonFormField<String>(
        key: ValueKey(_resetToken),
        initialValue: widget.value,
        decoration: decoration(),
        items: items,
        onChanged: pick,
      ),
    );
  }

  /// Maps a picked dropdown name to a physical wheel slot. The dropdown's
  /// items are the profile's LOCAL labels, while the wheel reports the
  /// DRIVER's names — two independently editable lists. Prefer an exact name
  /// match on the driver's slots; otherwise resolve the label to its position
  /// in the profile labels and take the wheel slot at that position. Returns
  /// null when neither matches (no physical filter behind the picked name).
  FilterSlot? _resolveSlot(WidgetRef ref, FilterWheelStatus wheel, String name) {
    for (final s in wheel.slots) {
      if (s.name == name) return s;
    }
    final labels = ref.read(filterWheelLabelsProvider);
    for (var slot = 1; slot <= labels.slotCount; slot++) {
      if (labels.labelAt(slot) != name) continue;
      for (final s in wheel.slots) {
        if (s.position == slot - 1) return s;
      }
    }
    return null;
  }
}
