import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/imaging/exposure_state.dart';
import '../../state/settings/filter_wheel_labels_state.dart';

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
class _FilterDropdown extends ConsumerWidget {
  final String value;
  final ValueChanged<String> onChanged;
  const _FilterDropdown({required this.value, required this.onChanged});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final labels = ref.watch(filterWheelLabelsProvider);
    // Dedupe (keep-first): two slots labelled identically would otherwise crash
    // DropdownButtonFormField's exactly-one-item-per-value assertion (r1 —
    // nothing stops duplicate slot labels at entry, and a name-keyed picker
    // only needs each name once anyway).
    final seen = <String>{};
    final names = <String>[
      for (var slot = 1; slot <= labels.slotCount; slot++)
        if (labels.labelAt(slot).isNotEmpty && seen.add(labels.labelAt(slot)))
          labels.labelAt(slot),
    ];
    if (!names.contains(value)) names.insert(0, value);
    return DropdownButtonFormField<String>(
      initialValue: value,
      decoration: const InputDecoration(labelText: 'Filter'),
      items: [
        for (final n in names) DropdownMenuItem(value: n, child: Text(n)),
      ],
      onChanged: (n) {
        if (n != null) onChanged(n);
      },
    );
  }
}
