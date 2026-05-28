import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/imaging/exposure_state.dart' show FrameKind;
import '../../../state/settings/imaging_defaults_state.dart';
import '../../../theme/ara_colors.dart';

/// §37.11 Imaging Defaults panel. Phase 12h.2-imaging-b makes the form
/// editable — values flow through `imagingDefaultsProvider`. Phase 12h.2b
/// (next sub-PR) wires `/api/v1/profile/imaging-defaults` for daemon
/// round-trip persistence; today's "Save" is in-memory only and shows a
/// snackbar to confirm the change stuck.
class ImagingDefaultsPanel extends ConsumerWidget {
  const ImagingDefaultsPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final d = ref.watch(imagingDefaultsProvider);
    final n = ref.read(imagingDefaultsProvider.notifier);
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        _NumberField(
          label: 'Default exposure (s)',
          initialValue: d.defaultExposure.inSeconds.toString(),
          parse: (s) {
            final i = int.tryParse(s);
            if (i == null) return;
            n.setExposure(Duration(seconds: i));
          },
        ),
        _NumberField(
          label: 'Default gain',
          initialValue: d.defaultGain.toString(),
          parse: (s) {
            final i = int.tryParse(s);
            if (i != null) n.setGain(i);
          },
        ),
        _NumberField(
          label: 'Default offset',
          initialValue: d.defaultOffset.toString(),
          parse: (s) {
            final i = int.tryParse(s);
            if (i != null) n.setOffset(i);
          },
        ),
        _NumberField(
          label: 'Default bin',
          initialValue: d.defaultBin.toString(),
          parse: (s) {
            final i = int.tryParse(s);
            if (i != null) n.setBin(i);
          },
        ),
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Row(
            children: [
              SizedBox(
                width: 280,
                child: Text('Default frame type',
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: AraColors.textSecondary,
                        )),
              ),
              Expanded(
                child: DropdownButtonFormField<FrameKind>(
                  initialValue: d.defaultFrameKind,
                  isDense: true,
                  items: const [
                    DropdownMenuItem(value: FrameKind.light, child: Text('Light')),
                    DropdownMenuItem(value: FrameKind.dark, child: Text('Dark')),
                    DropdownMenuItem(value: FrameKind.bias, child: Text('Bias')),
                    DropdownMenuItem(value: FrameKind.flat, child: Text('Flat')),
                  ],
                  onChanged: (v) {
                    if (v != null) n.setFrameKind(v);
                  },
                ),
              ),
            ],
          ),
        ),
        _NumberField(
          label: 'Cooling target temperature (°C)',
          initialValue: d.coolerTargetC.toString(),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setCoolerTargetC(v);
          },
        ),
        _NumberField(
          label: 'Cooler ramp rate (°C/min)',
          initialValue: d.coolerRampRatePerMin.toString(),
          parse: (s) {
            final v = double.tryParse(s);
            if (v != null) n.setCoolerRampRate(v);
          },
        ),
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Row(
            children: [
              SizedBox(
                width: 280,
                child: Text('Warm-up cooler at session end',
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: AraColors.textSecondary,
                        )),
              ),
              Switch(
                value: d.warmupAtSessionEnd,
                onChanged: n.setWarmupAtSessionEnd,
              ),
            ],
          ),
        ),
        const SizedBox(height: 24),
        Row(
          mainAxisAlignment: MainAxisAlignment.end,
          children: [
            FilledButton.icon(
              onPressed: () {
                ScaffoldMessenger.of(context).showSnackBar(
                  const SnackBar(
                    content: Text(
                      'Imaging defaults saved (in memory). Daemon round-trip lands in 12h.2b.',
                    ),
                  ),
                );
              },
              icon: const Icon(Icons.save, size: 16),
              label: const Text('Save'),
            ),
          ],
        ),
      ],
    );
  }
}

/// Single-line text field for an imaging-defaults numeric row. Owns its own
/// TextEditingController in a StatefulWidget so the controller is disposed
/// properly (the 12h.1 CR finding on PR #63 — never allocate a
/// TextEditingController inside build()).
class _NumberField extends StatefulWidget {
  final String label;
  final String initialValue;
  final void Function(String) parse;
  const _NumberField({
    required this.label,
    required this.initialValue,
    required this.parse,
  });

  @override
  State<_NumberField> createState() => _NumberFieldState();
}

class _NumberFieldState extends State<_NumberField> {
  late final TextEditingController _controller;
  late final FocusNode _focusNode;

  @override
  void initState() {
    super.initState();
    _controller = TextEditingController(text: widget.initialValue);
    _focusNode = FocusNode();
    // Commit the parsed value on focus-out so a user can tab/click away
    // without explicitly hitting enter.
    _focusNode.addListener(() {
      if (!_focusNode.hasFocus) {
        widget.parse(_controller.text);
      }
    });
  }

  @override
  void dispose() {
    _controller.dispose();
    _focusNode.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        children: [
          SizedBox(
            width: 280,
            child: Text(widget.label,
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      color: AraColors.textSecondary,
                    )),
          ),
          Expanded(
            child: TextField(
              controller: _controller,
              focusNode: _focusNode,
              decoration: const InputDecoration(isDense: true),
              onSubmitted: widget.parse,
            ),
          ),
        ],
      ),
    );
  }
}
