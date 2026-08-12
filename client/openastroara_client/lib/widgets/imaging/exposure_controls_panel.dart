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
/// showed a blank value in the web build). Truthfulness on a failed move is
/// restored by bumping [_resetToken]: the field is recreated from the
/// (unchanged) [value], so a tap whose move was rejected can't leave the box
/// displaying a filter that isn't in the light path.
class _FilterDropdown extends ConsumerStatefulWidget {
  final String value;
  final ValueChanged<String> onChanged;
  const _FilterDropdown({required this.value, required this.onChanged});

  @override
  ConsumerState<_FilterDropdown> createState() => _FilterDropdownState();
}

class _FilterDropdownState extends ConsumerState<_FilterDropdown> {
  /// Bumped whenever a commanded wheel move is rejected/failed, forcing the
  /// FormField to rebuild from the authoritative [value].
  int _resetToken = 0;

  /// The physical position a picker-initiated move was commanded to land on,
  /// while the wheel is still turning. Cleared once the wheel is observed
  /// parked there (the follow-logic tags, the field follows) — or when it's
  /// observed parked ANYWHERE ELSE (accepted but failed in flight), which
  /// forces the reset token so the field stops showing the tapped name.
  int? _pendingTargetPosition;

  @override
  Widget build(BuildContext context) {
    final labels = ref.watch(filterWheelLabelsProvider);
    final wheel = ref
        .watch(filterWheelProvider)
        .maybeWhen(data: (s) => s, orElse: () => null);
    // The wheel is turning: show progress in the same box so the user knows
    // the picker is about to snap to the new slot (instead of looking stuck).
    final moving = wheel != null && wheel.isConnected && wheel.isMoving;
    // Watch where a commanded move actually lands: accepted-but-never-landed
    // (stall/fault) must not leave the field showing the tapped name.
    final pending = _pendingTargetPosition;
    if (pending != null && wheel != null && !moving) {
      if (wheel.currentSlot == pending) {
        _pendingTargetPosition = null; // landed — follow tags, field follows
      } else {
        _pendingTargetPosition = null;
        WidgetsBinding.instance.addPostFrameCallback((_) {
          if (mounted) setState(() => _resetToken++);
        });
      }
    }
    if (moving) {
      return InputDecorator(
        decoration: const InputDecoration(labelText: 'Filter'),
        child: SizedBox(
          // Matches the field's internal height so the box doesn't resize
          // while the wheel turns.
          height: 48,
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              const SizedBox(
                width: 16,
                height: 16,
                child: CircularProgressIndicator(strokeWidth: 2),
              ),
              const SizedBox(width: 8),
              Text('Changing…', style: Theme.of(context).textTheme.bodyMedium),
            ],
          ),
        ),
      );
    }
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
    return DropdownButtonFormField<String>(
      key: ValueKey(_resetToken),
      initialValue: widget.value,
      decoration: const InputDecoration(labelText: 'Filter'),
      items: [
        for (final n in names) DropdownMenuItem(value: n, child: Text(n)),
      ],
      onChanged: (n) async {
        if (n == null) return;
        final wheelNow = ref
            .read(filterWheelProvider)
            .maybeWhen(data: (s) => s, orElse: () => null);
        // No wheel connected: nothing to move — just tag the capture (the
        // offline-authoring case; a reconnect re-syncs the picker).
        if (wheelNow == null || !wheelNow.isConnected) {
          widget.onChanged(n);
          return;
        }
        final slot = _resolveSlot(ref, wheelNow, n);
        if (slot == null) {
          // Connected, but the picked name maps to no physical slot (local
          // labels and the driver's names can diverge): tagging it would tag a
          // filter that isn't on the wheel. Say so instead of silently lying.
          if (!context.mounted) return;
          ScaffoldMessenger.of(context).showSnackBar(SnackBar(
            content: Text("Filter '$n' isn't a slot on the connected wheel."),
            backgroundColor: AraColors.accentError,
          ));
          return;
        }
        // currentSlot may be null (unknown/not yet reported) — treat that as
        // "not the current slot" and command the move; the driver rejects a
        // redundant move if one slips through.
        if (slot.position == wheelNow.currentSlot) {
          widget.onChanged(n);
          return;
        }
        // Move the wheel, and DON'T tag optimistically: the tag lands via the
        // follow-logic only once the wheel is actually OBSERVED at the new
        // slot. A move that's accepted (202) but fails in flight (motor
        // stall/fault) then can't leave a stale filter name on captures — the
        // picker stays truthful to wherever the wheel actually ends up.
        try {
          final performed = await ref
              .read(filterWheelProvider.notifier)
              .changeFilter(slot.position);
          if (!performed) {
            // Rebuild the field from the authoritative value (it may have
            // shown the just-tapped name via the FormField's internal state).
            if (mounted) setState(() => _resetToken++);
            if (!context.mounted) return;
            ScaffoldMessenger.of(context).showSnackBar(const SnackBar(
              content: Text('Another action is still in progress.'),
            ));
            return;
          }
          // Remember where the move should land; the build-time watcher above
          // reconciles it with the wheel's actual arrival.
          _pendingTargetPosition = slot.position;
          // No onChanged(n) here for NAMED target slots — the follow-logic
          // syncs filterSlot when the wheel reports its new slot (and reverts
          // nothing if it doesn't). An UNNAMED target slot can never be synced
          // by the follow-logic (it only latches named slots), so tag the
          // picked local label instead: otherwise a capture taken after the
          // move would be tagged with the previous filter while the wheel sits
          // on an unnamed slot.
          if (slot.name.isEmpty) {
            widget.onChanged(n);
          }
        } catch (e) {
          if (mounted) setState(() => _resetToken++);
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
