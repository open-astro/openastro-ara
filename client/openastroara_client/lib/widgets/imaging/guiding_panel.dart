import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_status.dart';
import '../../state/guider/guider_equipment_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/guider/live_guiding_state.dart';
import '../../state/profile_management_state.dart';
import '../../state/settings/phd2_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../profile/profile_import_flow.dart' show friendlyDaemonError;
import '../settings/editable_field.dart';

/// §63.18 live guiding panel — collapsible Imaging-tab section showing the
/// guider's live state + rolling RMS, with quick-adjust controls for the
/// RUNTIME-SAFE §63.5 params only (aggressiveness, minimum move, dec guide
/// mode, dither pixels). Those map to PHD2 `set_algo_param` /
/// `set_dec_guide_mode`, which apply while guiding continues — equipment and
/// optics changes (which force a disconnect window) stay in Settings → Guider.
///
/// Follows the [DiagnosticPanel] collapse idiom; the live history comes from
/// [liveGuidingRmsProvider] (a poll-fed local ring buffer — see that file for
/// why it isn't the §50.7 stats series).
class GuidingPanel extends ConsumerStatefulWidget {
  const GuidingPanel({super.key});

  @override
  ConsumerState<GuidingPanel> createState() => _GuidingPanelState();
}

class _GuidingPanelState extends ConsumerState<GuidingPanel> {
  bool _expanded = false;
  bool _applying = false;
  String? _status;

  // Hydrate gate for Apply. persistToServer PUTs the WHOLE Phd2Settings object
  // (host/port/profile and the §63.17 equipment slots included), so applying
  // before the daemon's saved values have been loaded would clobber the
  // daemon-side profile with client defaults. Apply stays disabled until the
  // initial hydrate has succeeded; a failure is surfaced, not swallowed.
  bool _hydrated = false;
  bool _hydrating = false;
  String? _hydrateError;

  static const _emDash = '—';

  @override
  void initState() {
    super.initState();
    // Hydrate the §63 settings so the quick-adjust controls start from the
    // daemon's saved values, not the client defaults. The active server loads
    // asynchronously, so also retry when the profile API (re)appears — a panel
    // mounted before saved servers resolve must not stay unhydrated forever.
    ref.listenManual(profileApiProvider, (prev, next) {
      if (next != null && !_hydrated && !_hydrating) _hydrate();
    });
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
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
      // Same surfacing as equipment_guider_panel — a visible message, and
      // Apply stays gated so a full-object PUT can't overwrite the daemon's
      // profile with the client defaults.
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
    final messenger = ScaffoldMessenger.of(context);
    if (api == null) {
      messenger.showSnackBar(const SnackBar(
          content: Text('No active server — connect to a daemon first.')));
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
      final msg = friendlyDaemonError(e, fallback: 'Apply failed');
      setState(() => _status = msg);
      messenger.showSnackBar(SnackBar(content: Text(msg)));
    } finally {
      if (mounted) setState(() => _applying = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final statusAsync = ref.watch(guiderStatusProvider);
    final status = statusAsync.asData?.value;
    final connected = status?.isConnected ?? false;
    // Only keep the poller/buffer alive while the section is expanded — the
    // provider is autoDispose, so collapsing stops the status polling.
    final samples =
        _expanded ? ref.watch(liveGuidingRmsProvider) : const <RmsSample>[];

    return Container(
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(top: BorderSide(color: AraColors.border)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          InkWell(
            onTap: () => setState(() => _expanded = !_expanded),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
              child: Row(
                children: [
                  Icon(_expanded ? Icons.expand_more : Icons.chevron_right,
                      size: 18, color: AraColors.textSecondary),
                  const SizedBox(width: 4),
                  const Icon(Icons.track_changes,
                      size: 16, color: AraColors.textSecondary),
                  const SizedBox(width: 6),
                  Text('Guiding',
                      style: Theme.of(context).textTheme.titleSmall),
                  const SizedBox(width: 12),
                  Text(
                    _stateLabel(status),
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: _stateColor(status),
                        ),
                  ),
                  const Spacer(),
                  Text(
                    'RMS ${_fmtArcsec(_liveRms(status)?.$1)}',
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: AraColors.textSecondary,
                        ),
                  ),
                ],
              ),
            ),
          ),
          if (_expanded) ...[
            _rmsSection(context, status),
            _SparklineSection(samples: samples),
            const Divider(height: 1, color: AraColors.border),
            if (!connected)
              Padding(
                padding: const EdgeInsets.all(12),
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
                child: _controls(context),
              ),
            ),
          ],
        ],
      ),
    );
  }

  /// RMS while actively guiding (guiding/dithering), else null → em-dashes.
  /// Returns (total, ra, dec) in arcsec.
  static (double?, double?, double?)? _liveRms(GuiderStatus? status) {
    if (status == null) return null;
    final actively = status.runtimeState == GuiderRuntimeState.guiding ||
        status.runtimeState == GuiderRuntimeState.dithering;
    if (!actively) return null;
    return (status.rmsTotal, status.rmsRa, status.rmsDec);
  }

  static String _stateLabel(GuiderStatus? status) {
    if (status == null || !status.isConnected) return 'disconnected';
    switch (status.runtimeState) {
      case GuiderRuntimeState.stopped:
        return 'stopped';
      case GuiderRuntimeState.calibrating:
        return 'calibrating';
      case GuiderRuntimeState.guiding:
        return 'guiding';
      case GuiderRuntimeState.paused:
        return 'paused';
      case GuiderRuntimeState.starLost:
        return 'star lost';
      case GuiderRuntimeState.dithering:
        return 'dithering';
      case GuiderRuntimeState.unknown:
        return 'connected';
    }
  }

  static Color _stateColor(GuiderStatus? status) {
    if (status == null || !status.isConnected) {
      return AraColors.accentDisconnected;
    }
    switch (status.runtimeState) {
      case GuiderRuntimeState.guiding:
        return AraColors.accentConnected;
      case GuiderRuntimeState.starLost:
        return AraColors.accentError;
      case GuiderRuntimeState.calibrating:
      case GuiderRuntimeState.dithering:
        return AraColors.accentBusy;
      case GuiderRuntimeState.stopped:
      case GuiderRuntimeState.paused:
      case GuiderRuntimeState.unknown:
        return AraColors.textSecondary;
    }
  }

  static String _fmtArcsec(double? v) =>
      v == null ? _emDash : '${v.toStringAsFixed(2)}″';

  static String _fmtPx(double? arcsec, double? scale) {
    if (arcsec == null || scale == null || scale <= 0) return _emDash;
    return '${(arcsec / scale).toStringAsFixed(2)} px';
  }

  Widget _rmsSection(BuildContext context, GuiderStatus? status) {
    final rms = _liveRms(status);
    final phd2 = ref.watch(phd2SettingsProvider);
    // px is derived from the §63.5 guide train (focal length + pixel size);
    // when unset, px reads as em-dash while arcsec stays live.
    final scale =
        guiderArcsecPerPixel(phd2.guideFocalLength, phd2.guidePixelSize);
    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 8, 12, 4),
      child: Row(
        children: [
          _rmsCell(context, 'Total', rms?.$1, scale),
          _rmsCell(context, 'RA', rms?.$2, scale),
          _rmsCell(context, 'Dec', rms?.$3, scale),
        ],
      ),
    );
  }

  Widget _rmsCell(
      BuildContext context, String label, double? arcsec, double? scale) {
    return Expanded(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(label,
              style: Theme.of(context)
                  .textTheme
                  .labelSmall
                  ?.copyWith(color: AraColors.textSecondary)),
          Text(_fmtArcsec(arcsec),
              style: Theme.of(context).textTheme.bodyMedium),
          Text(_fmtPx(arcsec, scale),
              style: Theme.of(context)
                  .textTheme
                  .labelSmall
                  ?.copyWith(color: AraColors.textDisabled)),
        ],
      ),
    );
  }

  Widget _controls(BuildContext context) {
    final phd2 = ref.watch(phd2SettingsProvider);
    final phd2N = ref.read(phd2SettingsProvider.notifier);
    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 4, 12, 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _aggressivenessSlider(
            context,
            label: 'RA aggressiveness',
            value: phd2.raAggressiveness,
            onChanged: phd2N.setRaAggressiveness,
          ),
          _aggressivenessSlider(
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
          const SizedBox(height: 8),
          Row(
            children: [
              FilledButton.icon(
                // Gated on the hydrate (see the field comment): Apply PUTs the
                // full settings object, so it must never run from defaults.
                onPressed: (_applying || !_hydrated) ? null : _apply,
                icon: _applying
                    ? const SizedBox(
                        width: 16,
                        height: 16,
                        child: CircularProgressIndicator(strokeWidth: 2))
                    : const Icon(Icons.send, size: 18),
                label: const Text('Apply'),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: Text(
                  'Applies live, guiding is not interrupted.',
                  style: Theme.of(context)
                      .textTheme
                      .labelSmall
                      ?.copyWith(color: AraColors.textDisabled),
                ),
              ),
            ],
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
              child: Text(
                _status!,
                style: Theme.of(context).textTheme.bodySmall,
              ),
            ),
        ],
      ),
    );
  }

  Widget _aggressivenessSlider(
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

/// Compact rolling sparkline of total RMS over the buffer window (~5 min).
class _SparklineSection extends StatelessWidget {
  const _SparklineSection({required this.samples});

  final List<RmsSample> samples;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(12, 2, 12, 8),
      child: SizedBox(
        height: 36,
        child: samples.length < 2
            ? Align(
                alignment: Alignment.centerLeft,
                child: Text(
                  'No guiding history yet',
                  style: Theme.of(context)
                      .textTheme
                      .labelSmall
                      ?.copyWith(color: AraColors.textDisabled),
                ),
              )
            : CustomPaint(
                size: Size.infinite,
                painter: RmsSparklinePainter(samples),
              ),
      ),
    );
  }
}

/// Painter for the §63.18 RMS sparkline: total RMS as a polyline, x mapped
/// over the sample time span, y auto-scaled 0..max (a fixed floor of 1″ keeps
/// tiny excellent-guiding traces from filling the whole height).
class RmsSparklinePainter extends CustomPainter {
  RmsSparklinePainter(this.samples);

  final List<RmsSample> samples;

  @override
  void paint(Canvas canvas, Size size) {
    if (samples.length < 2) return;
    final t0 = samples.first.time.millisecondsSinceEpoch;
    final t1 = samples.last.time.millisecondsSinceEpoch;
    final span = (t1 - t0).clamp(1, 1 << 62);
    var maxRms = 1.0;
    for (final s in samples) {
      if (s.total > maxRms) maxRms = s.total;
    }
    final path = Path();
    for (var i = 0; i < samples.length; i++) {
      final s = samples[i];
      final x =
          (s.time.millisecondsSinceEpoch - t0) / span * size.width;
      final y = size.height - (s.total / maxRms * size.height);
      if (i == 0) {
        path.moveTo(x, y);
      } else {
        path.lineTo(x, y);
      }
    }
    final paint = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 1.5
      ..color = AraColors.accentInfo;
    canvas.drawPath(path, paint);
  }

  @override
  bool shouldRepaint(RmsSparklinePainter oldDelegate) =>
      oldDelegate.samples != samples;
}
