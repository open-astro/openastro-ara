import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/equipment/camera_state.dart';
import '../../theme/ara_colors.dart';
import '../settings/settings_row.dart';

/// Reusable cooler control block for both the Settings → Camera panel and the
/// Imaging tab — the on/off switch, quick target presets (−10/−5/0/+5 °C), a
/// custom target field, and (when the camera reports a fan) the cooling-fan
/// toggle.
///
/// Layout follows the app's shared section pattern (a [SettingsSectionHeader]
/// + the 8pt spacing grid) so the block reads identically wherever it's
/// embedded. Safety rules are enforced by the daemon and reflected here via
/// the status: turning the cooler ON auto-starts the fan; turning it OFF stops
/// the fan; stopping the fan while cooling is refused. This widget just sends
/// the commands — the daemon owns the interlock.
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
  static const List<int> presets = [-10, -5, 0, 5];
  final TextEditingController _target = TextEditingController();

  @override
  void dispose() {
    _target.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
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
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              for (final p in presets)
                ChoiceChip(
                  label: Text(p <= 0 ? '$p °C' : '+$p °C'),
                  selected: s.coolerOn && (s.coolerSetpointC ?? 999) == p,
                  onSelected: (_) => _setTarget(p.toDouble()),
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
          const Padding(
            padding: EdgeInsets.only(top: 8, bottom: 4),
            child: Text(
              'Target temperature',
              style: TextStyle(
                fontSize: 12,
                color: AraColors.textSecondary,
              ),
            ),
          ),
          // Quick presets — one tap arms cooling at that set-point.
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              for (final p in presets)
                ChoiceChip(
                  label: Text(p <= 0 ? '$p °C' : '+$p °C'),
                  selected: s.coolerOn && (s.coolerSetpointC ?? 999) == p,
                  onSelected: (_) => _setTarget(p.toDouble()),
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
        ],
        // Vendor cooling fan — only when the camera/bridge reports one.
        if (s.fanMaxSpeed != null)
          Padding(
            padding: const EdgeInsets.only(top: 8),
            child: Row(
              children: [
                Expanded(
                  child: Text(
                    s.fanMaxSpeed! > 1
                        ? 'Cooling fan · ${s.fanSpeed ?? 0}/${s.fanMaxSpeed}'
                        : 'Cooling fan',
                  ),
                ),
                Switch(
                  value: (s.fanSpeed ?? 0) > 0,
                  onChanged: (on) => _setFan(on ? s.fanMaxSpeed! : 0),
                ),
              ],
            ),
          ),
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

  Future<void> _setCooler(bool on) async {
    final ok = await ref.read(cameraStatusProvider.notifier).setCooler(on);
    if (!ok && mounted) _toast('Another action is still in progress.');
  }

  Future<void> _setTarget(double celsius) async {
    // Setting a target turns the cooler on (daemon auto-starts the fan).
    final ok = await ref
        .read(cameraStatusProvider.notifier)
        .setCooler(true, targetTemperatureC: celsius);
    if (!ok && mounted) _toast('Another action is still in progress.');
  }

  Future<void> _setCustomTarget() async {
    final t = double.tryParse(_target.text.trim());
    if (t == null) {
      _toast('Enter a target temperature.');
      return;
    }
    await _setTarget(t);
  }

  Future<void> _setFan(int speed) async {
    final ok = await ref.read(cameraStatusProvider.notifier).setFan(speed);
    if (!ok && mounted) _toast('Another action is still in progress.');
  }

  void _toast(String message) {
    if (!mounted) return;
    ScaffoldMessenger.of(context)
        .showSnackBar(SnackBar(content: Text(message)));
  }
}
