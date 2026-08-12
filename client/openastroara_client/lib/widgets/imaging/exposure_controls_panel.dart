import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/filter_wheel_status.dart';
import '../../state/equipment/filter_wheel_state.dart';
import '../../state/imaging/exposure_state.dart';
import '../../state/settings/filter_wheel_labels_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/friendly_error.dart';

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
            _DurationSecondsField(
              label: 'Exposure (s)',
              value: params.exposure,
              onChanged: ctrl.setExposure,
            ),
            _IntField(
              label: 'Gain',
              value: params.gain,
              min: 0,
              max: 1000,
              onChanged: ctrl.setGain,
            ),
          ),
          const SizedBox(height: 6),
          pair(
            _IntField(
              label: 'Offset',
              value: params.offset,
              min: 0,
              max: 200,
              onChanged: ctrl.setOffset,
            ),
            _IntField(
              label: 'Bin',
              value: params.bin,
              min: 1,
              max: 8,
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
            _FilterDropdown(
              value: params.filterSlot,
              onChanged: ctrl.setFilterSlot,
              homing: params.homing,
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

class _DurationSecondsField extends StatefulWidget {
  final String label;
  final Duration value;
  final ValueChanged<Duration> onChanged;
  const _DurationSecondsField({
    required this.label,
    required this.value,
    required this.onChanged,
  });

  @override
  State<_DurationSecondsField> createState() => _DurationSecondsFieldState();
}

class _DurationSecondsFieldState extends State<_DurationSecondsField> {
  late final TextEditingController _ctrl;
  final FocusNode _focus = FocusNode();

  @override
  void initState() {
    super.initState();
    _ctrl = TextEditingController(text: widget.value.inSeconds.toString());
    // Commit on focus loss too — "type 1, click Take One" must shoot 1s, not
    // silently revert to the last submitted value (Enter alone was the only
    // commit path, so an un-submitted edit snapped back on the next rebuild).
    _focus.addListener(() {
      if (!_focus.hasFocus) _commit(_ctrl.text);
    });
  }

  void _commit(String s) {
    final parsed = int.tryParse(s.trim());
    if (parsed != null && parsed >= 0) {
      widget.onChanged(Duration(seconds: parsed));
    } else {
      _ctrl.text = widget.value.inSeconds.toString();
    }
  }

  @override
  void didUpdateWidget(covariant _DurationSecondsField old) {
    super.didUpdateWidget(old);
    // Never fight the user's in-progress edit.
    if (_focus.hasFocus) return;
    final expected = widget.value.inSeconds.toString();
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

class _IntField extends StatefulWidget {
  final String label;
  final int value;
  final int min;
  final int max;
  final ValueChanged<int> onChanged;
  const _IntField({
    required this.label,
    required this.value,
    required this.min,
    required this.max,
    required this.onChanged,
  });

  @override
  State<_IntField> createState() => _IntFieldState();
}

class _IntFieldState extends State<_IntField> {
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
  void didUpdateWidget(covariant _IntField old) {
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
enum _FilterPhase { idle, changing, updated }

class _FilterDropdown extends ConsumerStatefulWidget {
  final String value;
  final ValueChanged<String> onChanged;
  /// True while the wheel is being homed to slot 0 (L) on first launch — the
  /// picker shows the same busy state as a picker-initiated move.
  final bool homing;
  const _FilterDropdown({
    required this.value,
    required this.onChanged,
    this.homing = false,
  });

  @override
  ConsumerState<_FilterDropdown> createState() => _FilterDropdownState();
}

class _FilterDropdownState extends ConsumerState<_FilterDropdown>
    with SingleTickerProviderStateMixin {
  /// Bumped whenever a commanded wheel move is rejected/failed, forcing the
  /// FormField to rebuild from the authoritative [value].
  int _resetToken = 0;

  /// The physical position a picker-initiated move was commanded to land on,
  /// while the wheel is still turning. Cleared once the wheel is observed
  /// parked there — or parked anywhere else (accepted but failed in flight).
  int? _pendingTargetPosition;

  /// True once the wheel has been SEEN turning after a commanded move — only
  /// then may a settled-off-target status count as a failed landing. Right
  /// after the command the wheel may still report its old idle position for a
  /// poll or two; without this gate that would fake a failure instantly.
  bool _sawMove = false;

  /// Backstop for an accepted move that never reports ANY movement (stuck
  /// driver): after this fires without a landing, the picker reverts.
  Timer? _stallTimer;

  _FilterPhase _phase = _FilterPhase.idle;

  /// Drives the red busy pulse (repeat/reverse) and the green confirm flash.
  late final AnimationController _flash;

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
  void didUpdateWidget(_FilterDropdown oldWidget) {
    super.didUpdateWidget(oldWidget);
    // The first-launch home-to-L runs outside the picker: mirror it as a
    // busy state so the stale pre-home slot isn't shown as if current.
    if (widget.homing && !oldWidget.homing) {
      setState(() {
        _phase = _FilterPhase.changing;
        _flash.value = 0;
        _flash.repeat(reverse: true);
      });
    } else if (!widget.homing &&
        oldWidget.homing &&
        _phase == _FilterPhase.changing &&
        _pendingTargetPosition == null) {
      setState(() {
        _phase = _FilterPhase.idle;
        _flash.stop();
      });
    }
  }

  @override
  void dispose() {
    _stallTimer?.cancel();
    _flash.dispose();
    super.dispose();
  }

  /// Backstop for an accepted move that never reports ANY movement. The
  /// client's live poll runs every 15 s, so a landing can legitimately take a
  /// poll to be seen — only a command that never STARTED the wheel is an
  /// error at 10 s; a started-but-unreported landing gets a generous hard
  /// cap instead (the wheel panel shows the driver state meanwhile).
  void _startStallTimer() {
    _stallTimer?.cancel();
    _stallTimer = Timer(const Duration(seconds: 10), () {
      if (!mounted || _phase != _FilterPhase.changing) return;
      if (_sawMove) {
        // The wheel is turning; the landing poll (15 s cadence) will
        // reconcile. Re-arm a hard backstop in case it never reports.
        _stallTimer = Timer(const Duration(seconds: 45), _stallHard);
      } else {
        _stallHard();
      }
    });
  }

  Future<void> _stallHard() async {
    if (!mounted || _phase != _FilterPhase.changing) return;
    // The move may have completed without any poll observing it (the live
    // poll runs every 15 s) — ask the daemon directly before declaring a
    // failure, so a successful-but-unobserved move never errors.
    try {
      await ref.read(filterWheelProvider.notifier).refresh();
    } catch (_) {
      // The read failed; fall through to the failure path below.
    }
    if (!mounted || _phase != _FilterPhase.changing) return;
    final wheel = ref
        .read(filterWheelProvider)
        .maybeWhen(data: (s) => s, orElse: () => null);
    final pending = _pendingTargetPosition;
    if (wheel != null && pending != null && wheel.currentSlot == pending) {
      // It did land — treat as a confirmed success.
      _pendingTargetPosition = null;
      _sawMove = false;
      setState(() {
        _phase = _FilterPhase.updated;
        _flash.stop();
        _flash.value = 0;
        _flash.forward().whenComplete(() {
          if (mounted) setState(() => _phase = _FilterPhase.idle);
        });
      });
      return;
    }
    // Genuinely never moved.
    setState(() {
      _phase = _FilterPhase.idle;
      _flash.stop();
      _pendingTargetPosition = null;
      _sawMove = false;
      _resetToken++;
    });
    ScaffoldMessenger.of(context).showSnackBar(const SnackBar(
      content: Text("Couldn't change the filter - the wheel "
          'didn\'t start moving.'),
      backgroundColor: AraColors.accentError,
    ));
  }

  /// The underline color while busy: a red pulse during the move, a green
  /// pulse on a confirmed landing; null restores the theme default.
  Color? get _blinkColor {
    switch (_phase) {
      case _FilterPhase.idle:
        return null;
      case _FilterPhase.changing:
        return Color.lerp(AraColors.accentError, AraColors.border, _flash.value);
      case _FilterPhase.updated:
        return Color.lerp(
            AraColors.accentConnected, AraColors.border, _flash.value);
    }
  }

  @override
  Widget build(BuildContext context) {
    // Reconcile a commanded move with the wheel's actual arrival, and treat
    // wheel moves from OTHER sources (the §37.4 panel, a sequence) as busy
    // too — the picker is disabled + red until the wheel settles.
    ref.listen(filterWheelProvider, (prev, next) {
      final wheel = next.maybeWhen(data: (s) => s, orElse: () => null);
      final pending = _pendingTargetPosition;
      if (wheel != null && wheel.isMoving) {
        _sawMove = true;
        if (_phase != _FilterPhase.changing) {
          setState(() {
            _phase = _FilterPhase.changing;
            _flash.value = 0;
            _flash.repeat(reverse: true);
          });
        }
      } else if (pending != null && wheel != null && !wheel.isMoving) {
        // The wheel is parked. Landed on the commanded slot — success (even
        // if the move was too fast to observe the moving state). Settled
        // elsewhere — only a failure if we actually SAW it move after the
        // command; otherwise it just hasn't started yet, so keep waiting.
        if (wheel.currentSlot == pending) {
          _pendingTargetPosition = null;
          _sawMove = false;
          _stallTimer?.cancel();
          setState(() {
            _phase = _FilterPhase.updated;
            _flash.stop();
            _flash.value = 0;
            _flash.forward().whenComplete(() {
              if (mounted) setState(() => _phase = _FilterPhase.idle);
            });
          });
        } else if (_sawMove) {
          _pendingTargetPosition = null;
          _sawMove = false;
          _stallTimer?.cancel();
          setState(() {
            _phase = _FilterPhase.idle;
            _flash.stop();
            _resetToken++;
          });
        }
      } else if (_phase == _FilterPhase.changing && pending == null) {
        // An external move settled (no pending of ours) — done.
        _sawMove = false;
        setState(() {
          _phase = _FilterPhase.idle;
          _flash.stop();
        });
      }
    });
    final labels = ref.watch(filterWheelLabelsProvider);
    // Dedupe (keep-first): two slots labelled identically would otherwise
    // crash the picker's exactly-one-item-per-value assertion, and a
    // name-keyed picker only needs each name once anyway.
    final seen = <String>{};
    final names = <String>[
      for (var slot = 1; slot <= labels.slotCount; slot++)
        if (labels.labelAt(slot).isNotEmpty && seen.add(labels.labelAt(slot)))
          labels.labelAt(slot),
    ];
    if (!names.contains(widget.value)) names.insert(0, widget.value);

    final busy = _phase == _FilterPhase.changing || widget.homing;
    final blink = _blinkColor;
    final decoration = InputDecoration(
      labelText: 'Filter',
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

    return DropdownButtonFormField<String>(
      key: ValueKey(_resetToken),
      initialValue: widget.value,
      decoration: decoration,
      items: [
        for (final n in names) DropdownMenuItem(value: n, child: Text(n)),
      ],
      onChanged: busy
          ? null // keep it disabled (showing the picked slot) until it lands
          : (n) async {
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
                if (!context.mounted) return;
                ScaffoldMessenger.of(context).showSnackBar(SnackBar(
                  content: Text(
                    "Filter '$n' isn't a slot on the connected wheel.",
                  ),
                  backgroundColor: AraColors.accentError,
                ));
                return;
              }
              // currentSlot may be null (unknown/not yet reported) — treat
              // that as "not the current slot" and command the move; the
              // driver rejects a redundant move if one slips through.
              if (slot.position == wheelNow.currentSlot) {
                widget.onChanged(n);
                return;
              }
              // INSTANT busy feedback — the underline starts pulsing now,
              // not when the first poll reports the wheel turning.
              setState(() {
                _phase = _FilterPhase.changing;
                _flash.value = 0;
                _flash.repeat(reverse: true);
              });
              _pendingTargetPosition = slot.position;
              _sawMove = false;
              _startStallTimer();
              try {
                final performed = await ref
                    .read(filterWheelProvider.notifier)
                    .changeFilter(slot.position);
                if (!performed) {
                  _stallTimer?.cancel();
                  if (mounted) {
                    setState(() {
                      _phase = _FilterPhase.idle;
                      _flash.stop();
                      _pendingTargetPosition = null;
                      _sawMove = false;
                      _resetToken++;
                    });
                  }
                  if (!context.mounted) return;
                  ScaffoldMessenger.of(context).showSnackBar(const SnackBar(
                    content: Text('Another action is still in progress.'),
                  ));
                  return;
                }
                // No onChanged(n) here for NAMED target slots — the
                // follow-logic syncs filterSlot when the wheel reports its new
                // slot (and reverts nothing if it doesn't). An UNNAMED target
                // slot can never be synced by the follow-logic (it only
                // latches named slots), so tag the picked local label instead.
                if (slot.name.isEmpty) {
                  widget.onChanged(n);
                }
              } catch (e) {
                _stallTimer?.cancel();
                if (mounted) {
                  setState(() {
                    _phase = _FilterPhase.idle;
                    _flash.stop();
                    _pendingTargetPosition = null;
                    _sawMove = false;
                    _resetToken++;
                  });
                }
                if (!context.mounted) return;
                ScaffoldMessenger.of(context).showSnackBar(SnackBar(
                  content: Text(friendlyError(e, action: 'change the filter')),
                  backgroundColor: AraColors.accentError,
                ));
              }
            },
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
