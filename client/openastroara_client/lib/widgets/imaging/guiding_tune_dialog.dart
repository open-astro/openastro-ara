import 'package:flutter/material.dart';
import '../../util/friendly_error.dart';
import '../../l10n/l10n.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_advanced_settings.dart';
import '../../services/profile_api.dart';
import '../../state/guider/guider_equipment_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/profile_management_state.dart';
import '../../state/settings/loaded_section.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../profile/profile_import_flow.dart' show friendlyDaemonError;
import '../settings/editable_field.dart';
import '../settings/section_load_state.dart';
import 'guider_advanced_dialog.dart';
import '../adaptive_controls.dart';

/// Open the §63.18 guiding-tune dialog (the quick-adjust controls that used to
/// live inline in the Imaging tab's Guiding panel — moved to a focused dialog
/// per the macOS quick-settings idiom: the main view keeps live telemetry,
/// adjustments happen in a transient surface).
Future<void> showGuidingTuneDialog(BuildContext context) => showDialog<void>(
      context: context,
      builder: (_) => const GuidingTuneDialog(),
    );

/// The RUNTIME-SAFE §63.5 tuning params only (aggressiveness, minimum move,
/// dec guide mode, dither pixels, guide exposure, guide camera gain) — they map
/// to OpenAstro Guider `set_algo_param` / `set_dec_guide_mode` / `set_exposure`
/// / `set_camera_gain`, which apply
/// while guiding continues. Apply asks the daemon for the TUNING-ONLY push
/// (`?scope=tuning`): the full push also re-sends the equipment selections,
/// which the guider only accepts with its equipment disconnected, so it
/// stopped guiding mid-run. Equipment and optics changes stay in Settings →
/// Guider.
class GuidingTuneDialog extends ConsumerStatefulWidget {
  const GuidingTuneDialog({super.key});

  @override
  ConsumerState<GuidingTuneDialog> createState() => _GuidingTuneDialogState();
}

class _GuidingTuneDialogState extends ConsumerState<GuidingTuneDialog> {
  bool _applying = false;
  String? _status;
  List<GuiderNotApplied> _notApplied = const [];

  // Hydrate gate for Apply: the PUT carries the WHOLE section (host/port/
  // profile and the §63.17 equipment slots included), so Apply waits until
  // the rig's copy is in (ADR 0015); a failed load is surfaced, not
  // swallowed.
  bool _hydrated = false;
  bool _hydrating = false;
  String? _hydrateError;

  // The dialog edits a LOCAL draft, not the shared provider: unapplied edits
  // must never sit in phd2SettingsProvider, where any other surface's load
  // (a live-sync reload, the sequencer's planning load) could clobber them
  // — or worse, where Settings' auto-save could persist a never-confirmed
  // edit. Apply saves the eight tuning fields over the rig's copy (the
  // module's saveOnRigCopy, with its tag), so an edit staged in the shared
  // section is neither sent nor lost; Done discards the draft. Null until
  // the hydrate seeds it.
  Phd2Settings? _draft;

  // listenManual subscriptions are NOT auto-cancelled with the widget — kept
  // so dispose() can close it. The retry matters even for an on-demand dialog:
  // the profile API can still be null at open (the session's server list
  // resolves asynchronously), and without it Apply would stay silently disabled for the
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
      // The section loads itself; one more load when it isn't in from the rig
      // (a load over a copy that is in would drop edits staged elsewhere).
      final fresh = await ref.read(phd2SettingsProvider.notifier).rigCopy();
      if (!mounted) return;
      if (fresh == null) {
        throw ref.read(phd2SettingsProvider).lastFailure ??
            const SectionNotLoadedException(null);
      }
      setState(() {
        _hydrated = true;
        _hydrateError = null;
        _draft = fresh;
      });
    } catch (e) {
      if (mounted) {
        setState(() => _hydrateError = friendlyError(context.l10n, e, action: context.l10n.actionLoadSettings));
      }
    } finally {
      _hydrating = false;
    }
  }

  /// Persist the edited §63 settings, then ask the daemon for the tuning-only
  /// re-push (set_algo_param / set_dec_guide_mode / set_exposure /
  /// set_camera_gain — runtime-safe, guiding is not interrupted).
  Future<void> _apply() async {
    final api = ref.read(profileApiProvider);
    if (api == null) {
      setState(() => _status = 'Not connected — connect to your rig to save this.');
      return;
    }
    final draft = _draft;
    if (draft == null) return;
    setState(() {
      _applying = true;
      _status = null;
      _notApplied = const [];
    });
    try {
      // Save ONLY the eight runtime-safe tuning fields from the draft over
      // the rig's copy, with its tag — never the shared provider's object,
      // which may hold edits not saved yet. They show in the shared section
      // too; its other staged edits stay. A copy another device saved since
      // is refused, nothing written (ADR 0015).
      await ref
          .read(phd2SettingsProvider.notifier)
          .saveOnRigCopy(
            (c) => c.copyWith(
              raAggressiveness: draft.raAggressiveness,
              decAggressiveness: draft.decAggressiveness,
              minimumMove: draft.minimumMove,
              decMinimumMove: draft.decMinimumMove,
              decGuideMode: draft.decGuideMode,
              ditherPixels: draft.ditherPixels,
              guideExposureMs: draft.guideExposureMs,
              guideCameraGain: draft.guideCameraGain,
            ),
          );
      // Tuning-only: the full push re-sends the equipment selections inside a
      // stop_capture / disconnect window and ends the guide session.
      await ref
          .read(guiderEquipmentProvider.notifier)
          .pushProfile(tuningOnly: true);
      // #1748 — a setting the Guider's algorithm does not have shows here, where it was changed.
      var notApplied = const <GuiderNotApplied>[];
      try {
        notApplied = await ref.read(guiderAdvancedApiProvider)?.getGuider().then((v) => v.notApplied) ?? notApplied;
      } catch (_) {
        // The list is a hint; the Apply itself succeeded.
      }
      if (!mounted) return;
      setState(() {
        _status = notApplied.isEmpty
            ? 'Applied — guiding continues uninterrupted.'
            : 'Applied, except what is listed below. Guiding continues.';
        _notApplied = notApplied;
      });
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _status =
            e is SectionNotLoadedException || e is SectionConflictException
            ? e.toString()
            : friendlyDaemonError(e, fallback: "Couldn't apply the tuning"),
      );
    } finally {
      if (mounted) setState(() => _applying = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final connected =
        ref.watch(guiderStatusProvider).asData?.value?.isConnected ?? false;
    // Fall back to the provider's values for display while the draft is
    // unseeded (pre-hydrate); edits are only possible once the draft exists.
    // Neither: the section is not loaded, and the controls say so (ADR 0015).
    final phd2 = _draft ?? ref.watch(phd2SettingsProvider).value;

    return Dialog(
      backgroundColor: AraColors.bgPanel,
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 460),
        // Scroll-safe on short windows: the dialog grows with status/error
        // lines and must never overflow its route's height budget.
        child: SingleChildScrollView(
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
              if (phd2 == null)
                SectionNotLoaded(
                  section: ref.watch(phd2SettingsProvider),
                  padding: EdgeInsets.zero,
                  onRetry: _hydrate,
                )
              else
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
                        onChanged: (v) => _edit((d) => d.copyWith(raAggressiveness: v)),
                      ),
                      _slider(
                        context,
                        label: 'Dec aggressiveness',
                        value: phd2.decAggressiveness,
                        onChanged: (v) => _edit((d) => d.copyWith(decAggressiveness: v)),
                      ),
                      EditableNumberRow(
                        label: 'RA minimum move (px)',
                        currentValue: phd2.minimumMove.toString(),
                        getCanonical: () => (_current ?? phd2).minimumMove.toString(),
                        parse: (s) {
                          // Mirror setMinimumMove's bound (>= 0): an invalid
                          // entry never enters the draft, so the canonical
                          // resync snaps the field back — and Apply can never
                          // silently drop it.
                          final v = double.tryParse(s);
                          if (v != null && v >= 0) {
                            _edit((d) => d.copyWith(minimumMove: v));
                          }
                        },
                      ),
                      EditableNumberRow(
                        label: 'Dec minimum move (px)',
                        currentValue: phd2.decMinimumMove.toString(),
                        getCanonical: () => (_current ?? phd2).decMinimumMove.toString(),
                        parse: (s) {
                          final v = double.tryParse(s);
                          if (v != null && v >= 0) {
                            _edit((d) => d.copyWith(decMinimumMove: v));
                          }
                        },
                      ),
                      // "Unset" = the guider keeps its own duration (nothing
                      // is pushed); any other choice is sent with set_exposure
                      // and takes effect on the next guide frame.
                      SettingsDropdownRow<int>(
                        label: 'Guide exposure',
                        // A saved value the guider does not offer (hand-edited
                        // profile) is still shown, so the row never renders
                        // with a value missing from its items.
                        value: phd2.guideExposureMs,
                        items: {
                          0: formatGuideExposure(0),
                          for (final ms in guideExposureChoicesMs)
                            ms: formatGuideExposure(ms),
                          if (phd2.guideExposureMs > 0 &&
                              !guideExposureChoicesMs
                                  .contains(phd2.guideExposureMs))
                            phd2.guideExposureMs:
                                formatGuideExposure(phd2.guideExposureMs),
                        },
                        onChanged: (v) {
                          if (v != null) _edit((d) => d.copyWith(guideExposureMs: v));
                        },
                      ),
                      if (phd2.guideExposureMs > 0 &&
                          (phd2.guideExposureMs < phd2.guideExposureMinMs ||
                              phd2.guideExposureMs > phd2.guideExposureMaxMs))
                        Padding(
                          padding: const EdgeInsets.only(bottom: 6),
                          child: Text(
                            'Outside your dark-library range '
                            '(${formatGuideExposure(phd2.guideExposureMinMs)}–'
                            '${formatGuideExposure(phd2.guideExposureMaxMs)}): '
                            'guide frames at this exposure get no dark. Rebuild '
                            'the dark library from Settings → Guider to cover it.',
                            style: Theme.of(context)
                                .textTheme
                                .labelSmall
                                ?.copyWith(color: AraColors.textDisabled),
                          ),
                        ),
                      // The guider's own scale: percent of the camera's gain
                      // range. Its stock 95% buries stars in read noise.
                      EditableNumberRow(
                        label: 'Guide camera gain (%)',
                        currentValue: phd2.guideCameraGain.toString(),
                        getCanonical: () => (_current ?? phd2).guideCameraGain.toString(),
                        parse: (s) {
                          final v = int.tryParse(s);
                          if (v != null && v >= 0 && v <= 100) {
                            _edit((d) => d.copyWith(guideCameraGain: v));
                          }
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
                          if (v != null) _edit((d) => d.copyWith(decGuideMode: v));
                        },
                      ),
                      EditableNumberRow(
                        label: 'Dither pixels',
                        currentValue: phd2.ditherPixels.toString(),
                        getCanonical: () => (_current ?? phd2).ditherPixels.toString(),
                        parse: (s) {
                          // Mirror setDitherPixels's bound (>= 0) — see above.
                          final v = double.tryParse(s);
                          if (v != null && v >= 0) {
                            _edit((d) => d.copyWith(ditherPixels: v));
                          }
                        },
                      ),
                    ],
                  ),
                ),
              ),
              // Not loaded: the failure shows in the not-loaded state above.
              if (_hydrateError != null && phd2 != null)
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
              if (_notApplied.isNotEmpty) GuiderNotAppliedList(_notApplied),
              Row(
                mainAxisAlignment: MainAxisAlignment.end,
                children: [
                  // #1364 item 3 — every other guider setting.
                  Expanded(
                    child: Align(
                      alignment: Alignment.centerLeft,
                      child: TextButton.icon(
                        key: const Key('tune-advanced'),
                        onPressed: () => showGuiderAdvancedDialog(context),
                        icon: const Icon(Icons.settings_suggest_outlined, size: 16),
                        label: const Text('More…', overflow: TextOverflow.ellipsis),
                      ),
                    ),
                  ),
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
                            child: AdaptiveProgressIndicator(strokeWidth: 2))
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

  /// The values the controls show: the draft once seeded, else the provider;
  /// null while neither holds a copy.
  Phd2Settings? get _current =>
      _draft ?? ref.read(phd2SettingsProvider).value;

  /// Apply an edit to the local draft (no-op until the hydrate seeds it).
  void _edit(Phd2Settings Function(Phd2Settings) change) {
    final draft = _draft;
    if (draft == null) return;
    setState(() => _draft = change(draft));
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
          child: AdaptiveSlider(
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
