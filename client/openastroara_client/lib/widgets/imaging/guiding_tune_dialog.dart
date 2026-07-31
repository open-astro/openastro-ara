import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';
import '../../state/guider/guider_equipment_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/profile_management_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../profile/profile_import_flow.dart' show friendlyDaemonError;
import '../settings/editable_field.dart';

/// Open the §63.18 guiding-tune dialog (the quick-adjust controls that used to
/// live inline in the Imaging tab's Guiding panel — moved to a focused dialog
/// per the macOS quick-settings idiom: the main view keeps live telemetry,
/// adjustments happen in a transient surface).
Future<void> showGuidingTuneDialog(BuildContext context) => showDialog<void>(
      context: context,
      builder: (_) => const GuidingTuneDialog(),
    );

/// The RUNTIME-SAFE §63.5 tuning params only (aggressiveness, minimum move,
/// dec guide mode, dither pixels) — they map to PHD2 `set_algo_param` /
/// `set_dec_guide_mode`, which apply while guiding continues. Equipment and
/// optics changes (which force a disconnect window) stay in Settings → Guider.
class GuidingTuneDialog extends ConsumerStatefulWidget {
  const GuidingTuneDialog({super.key});

  @override
  ConsumerState<GuidingTuneDialog> createState() => _GuidingTuneDialogState();
}

class _GuidingTuneDialogState extends ConsumerState<GuidingTuneDialog> {
  bool _applying = false;
  String? _status;

  // Hydrate gate for Apply: persistToServer PUTs the WHOLE Phd2Settings object
  // (host/port/profile and the §63.17 equipment slots included), so applying
  // before the daemon's saved values have been loaded would clobber the
  // daemon-side profile with client defaults. Apply stays disabled until the
  // initial hydrate has succeeded; a failure is surfaced, not swallowed.
  bool _hydrated = false;
  bool _hydrating = false;
  String? _hydrateError;

  // listenManual subscriptions are NOT auto-cancelled with the widget — kept
  // so dispose() can close it. The retry matters even for an on-demand dialog:
  // the profile API can still be null at open (saved servers resolve
  // asynchronously), and without it Apply would stay silently disabled for the
  // whole dialog session.
  ProviderSubscription<ProfileApi?>? _profileApiSub;

  @override
  void initState() {
    super.initState();
    _profileApiSub = ref.listenManual(profileApiProvider, (prev, next) {
      if (next != null && !_hydrated && !_hydrating) _hydrate();
    });
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
  }

  @override
  void dispose() {
    _profileApiSub?.close();
    _profileApiSub = null;
    super.dispose();
  }

  Future<void> _hydrate() async {
    final api = ref.read(profileApiProvider);
    if (api == null || _hydrating || _hydrated) return;
    _hydrating = true;
    try {
      await ref.read(phd2SettingsProvider.notifier).hydrateFromServer(api);
      if (mounted) {
        setState(() {
          _hydrated = true;
          _hydrateError = null;
        });
      }
    } catch (e) {
      if (mounted) {
        setState(() => _hydrateError = 'Could not load saved values: $e');
      }
    } finally {
      _hydrating = false;
    }
  }

  /// Persist the edited §63 settings, then ask the daemon to re-push the
  /// profile to the guider (set_algo_param / set_dec_guide_mode — runtime-safe,
  /// guiding is not interrupted).
  Future<void> _apply() async {
    final api = ref.read(profileApiProvider);
    if (api == null) {
      setState(() => _status = 'No active server — connect to a daemon first.');
      return;
    }
    setState(() {
      _applying = true;
      _status = null;
    });
    try {
      await ref.read(phd2SettingsProvider.notifier).persistToServer(api);
      await ref.read(guiderEquipmentProvider.notifier).pushProfile();
      if (!mounted) return;
      setState(() => _status = 'Applied — guiding continues uninterrupted.');
    } catch (e) {
      if (!mounted) return;
      setState(
          () => _status = friendlyDaemonError(e, fallback: 'Apply failed'));
    } finally {
      if (mounted) setState(() => _applying = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final connected =
        ref.watch(guiderStatusProvider).asData?.value?.isConnected ?? false;
    final phd2 = ref.watch(phd2SettingsProvider);
    final phd2N = ref.read(phd2SettingsProvider.notifier);

    return Dialog(
      backgroundColor: AraColors.bgPanel,
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 460),
        child: Padding(
          padding: const EdgeInsets.fromLTRB(20, 16, 20, 16),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Row(
                children: [
                  const Icon(Icons.tune, size: 18, color: AraColors.textSecondary),
                  const SizedBox(width: 8),
                  Text('Tune Guiding',
                      style: Theme.of(context).textTheme.titleMedium),
                  const Spacer(),
                  IconButton(
                    tooltip: 'Close',
                    icon: const Icon(Icons.close, size: 18),
                    onPressed: () => Navigator.of(context).pop(),
                  ),
                ],
              ),
              Text(
                'Applies live — guiding is not interrupted.',
                style: Theme.of(context)
                    .textTheme
                    .labelSmall
                    ?.copyWith(color: AraColors.textDisabled),
              ),
              const SizedBox(height: 12),
              if (!connected)
                Padding(
                  padding: const EdgeInsets.only(bottom: 8),
                  child: Text(
                    'Guider disconnected — connect the guider (equipment chip) '
                    'to tune guiding.',
                    style: Theme.of(context)
                        .textTheme
                        .bodySmall
                        ?.copyWith(color: AraColors.textDisabled),
                  ),
                ),
              // Controls stay visible when disconnected (values are still the
              // saved profile) but inert, matching the settings panel's gating.
              IgnorePointer(
                ignoring: !connected,
                child: Opacity(
                  opacity: connected ? 1 : 0.45,
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      _slider(
                        context,
                        label: 'RA aggressiveness',
                        value: phd2.raAggressiveness,
                        onChanged: phd2N.setRaAggressiveness,
                      ),
                      _slider(
                        context,
                        label: 'Dec aggressiveness',
                        value: phd2.decAggressiveness,
                        onChanged: phd2N.setDecAggressiveness,
                      ),
                      EditableNumberRow(
                        label: 'Minimum move (px)',
                        currentValue: phd2.minimumMove.toString(),
                        getCanonical: () =>
                            ref.read(phd2SettingsProvider).minimumMove.toString(),
                        parse: (s) {
                          final v = double.tryParse(s);
                          if (v != null) phd2N.setMinimumMove(v);
                        },
                      ),
                      SettingsDropdownRow<String>(
                        label: 'Dec guide mode',
                        value: phd2.decGuideMode,
                        items: const {
                          'auto': 'Auto',
                          'north': 'North',
                          'south': 'South',
                          'off': 'Off',
                        },
                        onChanged: (v) {
                          if (v != null) phd2N.setDecGuideMode(v);
                        },
                      ),
                      EditableNumberRow(
                        label: 'Dither pixels',
                        currentValue: phd2.ditherPixels.toString(),
                        getCanonical: () =>
                            ref.read(phd2SettingsProvider).ditherPixels.toString(),
                        parse: (s) {
                          final v = double.tryParse(s);
                          if (v != null) phd2N.setDitherPixels(v);
                        },
                      ),
                    ],
                  ),
                ),
              ),
              if (_hydrateError != null)
                Padding(
                  padding: const EdgeInsets.only(top: 6),
                  child: Text(
                    _hydrateError!,
                    style: Theme.of(context)
                        .textTheme
                        .bodySmall
                        ?.copyWith(color: Theme.of(context).colorScheme.error),
                  ),
                ),
              if (_status != null)
                Padding(
                  padding: const EdgeInsets.only(top: 6),
                  child:
                      Text(_status!, style: Theme.of(context).textTheme.bodySmall),
                ),
              const SizedBox(height: 12),
              Row(
                mainAxisAlignment: MainAxisAlignment.end,
                children: [
                  TextButton(
                    onPressed: () => Navigator.of(context).pop(),
                    child: const Text('Done'),
                  ),
                  const SizedBox(width: 8),
                  FilledButton.icon(
                    // Gated on the hydrate (see the field comment): Apply PUTs
                    // the full settings object, so it must never run from defaults.
                    onPressed:
                        (_applying || !_hydrated || !connected) ? null : _apply,
                    icon: _applying
                        ? const SizedBox(
                            width: 16,
                            height: 16,
                            child: CircularProgressIndicator(strokeWidth: 2))
                        : const Icon(Icons.send, size: 18),
                    label: const Text('Apply'),
                  ),
                ],
              ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _slider(
    BuildContext context, {
    required String label,
    required double value,
    required ValueChanged<double> onChanged,
  }) {
    return Row(
      children: [
        SizedBox(
          width: 150,
          child: Text(label, style: Theme.of(context).textTheme.bodySmall),
        ),
        Expanded(
          child: Slider(
            value: value.clamp(0.0, 1.0),
            min: 0,
            max: 1,
            divisions: 20,
            onChanged: onChanged,
          ),
        ),
        SizedBox(
          width: 44,
          child: Text(
            '${(value * 100).round()}%',
            textAlign: TextAlign.end,
            style: Theme.of(context).textTheme.bodySmall,
          ),
        ),
      ],
    );
  }
}
