import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/equipment_device_api.dart';
import '../../state/equipment/camera_state.dart';
import '../help_icon.dart';
import '../../theme/ara_colors.dart';
import '../settings/settings_row.dart';

/// Reusable cooler control block for both the Settings → Camera panel and the
/// Imaging tab — the on/off switch, quick target presets (−10/−5/0/+5/+10 °C), a
/// custom target field, and (when the camera reports a fan) the cooling-fan
/// toggle.
///
/// Layout follows the app's shared section pattern (a [SettingsSectionHeader]
/// + the 8pt spacing grid) so the block reads identically wherever it's
/// embedded. The cooling-fan coordination is CLIENT-side (this PR ships no
/// daemon changes): `CameraStatusNotifier.setCooler` syncs the bridge's
/// Thermal-Switch Fan port after the cooler command (on → fan on, off → fan
/// off) and surfaces a sync failure. Note the limit: a manual fan-off from
/// the Switches panel while the cooler is running is NOT blocked — this PR
/// ships no daemon guard and no switch-panel interlock for that path.
class CoolerControls extends ConsumerStatefulWidget {
  /// [compact] renders only the target picker (presets + custom field) — used
  /// by the Imaging tab, where the readouts and on/off toggles live in
  /// Settings → Camera instead.
  const CoolerControls({super.key, this.compact = false});

  final bool compact;

  @override
  ConsumerState<CoolerControls> createState() => _CoolerControlsState();
}

class _CoolerControlsState extends ConsumerState<CoolerControls> {
  static const List<int> presets = [-10, -5, 0, 5, 10];
  final TextEditingController _target = TextEditingController();

  /// One-liner compact chip for the target presets (the °C unit lives in the
  /// section label so the chips stay short enough to sit on a single line in
  /// the narrow Imaging rail).
  static Widget _presetChip(
    int p,
    bool selected,
    VoidCallback onSelected,
  ) =>
      ChoiceChip(
        label: Text(p <= 0 ? '$p' : '+$p'),
        selected: selected,
        onSelected: (_) => onSelected(),
        visualDensity: VisualDensity.compact,
        materialTapTargetSize: MaterialTapTargetSize.shrinkWrap,
        labelStyle: const TextStyle(fontSize: 12),
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
      );

  @override
  void dispose() {
    _target.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    // The cooler↔fan sync lives in CameraStatusNotifier.setCooler (the single
    // cooler entry point) — see camera_state.dart.
    final s = ref.watch(cameraStatusProvider).maybeWhen(
          data: (v) => v,
          orElse: () => null,
        );
    if (s == null) return const SizedBox.shrink();
    final caps = s.capabilities;
    if (caps == null) return const SizedBox.shrink();
    if (!caps.hasCooler) return const SizedBox.shrink();

    if (widget.compact) {
      // Imaging tab: only the target picker — the readouts and on/off toggles
      // stay in Settings → Camera.
      if (!caps.canSetTemperature) return const SizedBox.shrink();
      return Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const SettingsSectionHeader('Cooling target'),
          _row('Sensor temperature',
              s.ccdTemperature == null ? '—' : '${s.ccdTemperature!.toStringAsFixed(1)} °C'),
          Padding(
            padding: const EdgeInsets.only(top: 8, bottom: 4),
            child: Row(
              children: [
                const Text(
                  'Target temperature (°C)',
                  style: TextStyle(
                    fontSize: 12,
                    color: AraColors.textSecondary,
                  ),
                ),
                const SizedBox(width: 4),
                HelpIcon(helpKey: 'eq.camera.cooler_target', device: s.name),
              ],
            ),
          ),
          // Quick presets — one tap arms cooling at that set-point. Compact so
          // all five sit on a single line in the narrow rail.
          Wrap(
            spacing: 6,
            runSpacing: 6,
            children: [
              for (final p in presets)
                _presetChip(
                  p,
                  s.coolerOn && (s.coolerSetpointC ?? 999) == p,
                  () => _setTarget(p.toDouble()),
                ),
            ],
          ),
          Padding(
            padding: const EdgeInsets.only(top: 8),
            child: Row(
              children: [
                SizedBox(
                  width: 130,
                  child: TextField(
                    controller: _target,
                    keyboardType: const TextInputType.numberWithOptions(
                      signed: true,
                      decimal: true,
                    ),
                    inputFormatters: [
                      FilteringTextInputFormatter.allow(
                        RegExp(r'^-?[0-9]*\.?[0-9]*$'),
                      ),
                    ],
                    decoration: const InputDecoration(
                      isDense: true,
                      labelText: 'Custom (°C)',
                    ),
                  ),
                ),
                const SizedBox(width: 12),
                OutlinedButton(
                  onPressed: () => _setCustomTarget(),
                  child: const Text('Set'),
                ),
              ],
            ),
          ),
          const SizedBox(height: 4),
        ],
      );
    }

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const SettingsSectionHeader('Cooling'),
        _row('Sensor temperature',
            s.ccdTemperature == null ? '—' : '${s.ccdTemperature!.toStringAsFixed(1)} °C'),
        if (s.coolerPowerPct != null)
          _row('Cooler power', '${s.coolerPowerPct!.toStringAsFixed(0)} %'),
        if (s.coolerOn && s.coolerSetpointC != null)
          _row('Cooling to', '${s.coolerSetpointC!.toStringAsFixed(1)} °C'),
        // Cooler on/off.
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 4),
          child: Row(
            children: [
              const Expanded(child: Text('Cooler')),
              Switch(value: s.coolerOn, onChanged: (v) => _setCooler(v)),
            ],
          ),
        ),
        if (caps.canSetTemperature) ...[
          Padding(
            padding: const EdgeInsets.only(top: 8, bottom: 4),
            child: Row(
              children: [
                const Text(
                  'Target temperature (°C)',
                  style: TextStyle(
                    fontSize: 12,
                    color: AraColors.textSecondary,
                  ),
                ),
                const SizedBox(width: 4),
                HelpIcon(helpKey: 'eq.camera.cooler_target', device: s.name),
              ],
            ),
          ),
          // Quick presets + the custom field on ONE line — the presets wrap
          // (Expanded) while the custom input stays pinned at the end.
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Wrap(
                  spacing: 6,
                  runSpacing: 6,
                  children: [
                    for (final p in presets)
                      _presetChip(
                        p,
                        s.coolerOn && (s.coolerSetpointC ?? 999) == p,
                        () => _setTarget(p.toDouble()),
                      ),
                  ],
                ),
              ),
              const SizedBox(width: 8),
              SizedBox(
                width: 130, // app-standard field width (matches the other panels)
                child: TextField(
                  controller: _target,
                  keyboardType: const TextInputType.numberWithOptions(
                    signed: true,
                    decimal: true,
                  ),
                  inputFormatters: [
                    FilteringTextInputFormatter.allow(
                      RegExp(r'^-?[0-9]*\.?[0-9]*$'),
                    ),
                  ],
                  decoration: const InputDecoration(
                    isDense: true,
                    labelText: 'Custom (°C)',
                  ),
                ),
              ),
              const SizedBox(width: 8),
              OutlinedButton(
                onPressed: () => _setCustomTarget(),
                child: const Text('Set'),
              ),
            ],
          ),
        ],
        const SizedBox(height: 4),
      ],
    );
  }

  /// Label/value row matching the app's panel row rhythm (2 pt vertical
  /// padding, label left / value right).
  static Widget _row(String label, String value) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 2),
        child: Row(
          children: [
            Expanded(child: Text(label)),
            Text(value),
          ],
        ),
      );

  Future<void> _setCooler(bool on) =>
      _run(() => ref.read(cameraStatusProvider.notifier).setCooler(on));

  Future<void> _setTarget(double celsius) async {
    // Setting a target turns the cooler on (daemon auto-starts the fan).
    await _run(() => ref
        .read(cameraStatusProvider.notifier)
        .setCooler(true, targetTemperatureC: celsius));
  }

  Future<void> _setCustomTarget() async {
    final t = double.tryParse(_target.text.trim());
    if (t == null) {
      _toast('Enter a target temperature.');
      return;
    }
    await _setTarget(t);
  }

  /// Runs the action and surfaces failures as a toast. The notifier returns
  /// false for a re-entrancy guard, but THROWS on a rejected command (e.g. a
  /// cooler or fan-sync rejection) — so the try/catch here turns it into a
  /// friendly message instead of an unhandled exception.
  Future<void> _run(Future<bool> Function() action) async {
    try {
      final ok = await action();
      if (!ok) {
        _toast('Another action is still in progress.');
      }
    } catch (e) {
      _toast("Couldn't change that: ${describeEquipmentError(e)}");
    }
  }

  void _toast(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(context)
        .showSnackBar(SnackBar(content: Text(message)));
  }
}
