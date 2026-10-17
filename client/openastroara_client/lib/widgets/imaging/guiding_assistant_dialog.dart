import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guiding_assistant.dart';
import '../../state/guider/guiding_assistant_state.dart';
import '../../theme/ara_colors.dart';

Future<void> showGuidingAssistantDialog(BuildContext context) =>
    showDialog<void>(
      context: context,
      builder: (_) => const Dialog(
        insetPadding: EdgeInsets.all(24),
        child: SizedBox(
          width: 640,
          height: 600,
          child: GuidingAssistantPanel(),
        ),
      ),
    );

/// #1364 item 4 — PHD2's Guiding Assistant from Ara (openastro-guider #83):
/// guide output is held off while the star drifts for a couple of minutes;
/// the guider measures drift, seeing and (optionally) Dec backlash, then
/// suggests min-move, exposure and backlash compensation. Accept writes the
/// ticked suggestions to the guider's profile. Needs a selected star and a
/// calibrated mount — the guider says so when they are missing.
class GuidingAssistantPanel extends ConsumerStatefulWidget {
  const GuidingAssistantPanel({super.key});

  /// The sampling lengths offered, seconds. Two minutes is the guider's
  /// default and what PHD2's dialog asks for.
  static const durations = [60.0, 120.0, 180.0, 300.0];

  @override
  ConsumerState<GuidingAssistantPanel> createState() =>
      _GuidingAssistantPanelState();
}

class _GuidingAssistantPanelState extends ConsumerState<GuidingAssistantPanel> {
  double _durationSec = 120;
  bool _measureBacklash = true;
  final Set<String> _accept = {};
  // The suggestions ticked by default belong to one run: a new run re-ticks.
  // Armed (true) while a run is in progress so the next `done` re-ticks even
  // when its sample count equals the last run's (a fixed duration and
  // exposure give the same count run after run — review of #1387).
  bool _retickOnDone = true;

  @override
  Widget build(BuildContext context) {
    final state = ref.watch(guidingAssistantProvider);
    final notifier = ref.read(guidingAssistantProvider.notifier);
    final status = state.status;
    final text = Theme.of(context).textTheme;
    final recs = status.recommendations;
    if (status.isRunning) {
      _retickOnDone = true;
    } else if (status.isDone && recs != null && _retickOnDone) {
      _retickOnDone = false;
      _accept
        ..clear()
        ..addAll(recs.suggestions.map((s) => s.key));
    }
    return Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              const Icon(
                Icons.insights,
                size: 18,
                color: AraColors.textSecondary,
              ),
              const SizedBox(width: 8),
              Text('Guiding Assistant', style: text.titleMedium),
              const SizedBox(width: 12),
              Flexible(
                child: Text(
                  _headline(state),
                  key: const Key('guiding-assistant-headline'),
                  overflow: TextOverflow.ellipsis,
                  style: text.bodySmall?.copyWith(color: _headlineColor(state)),
                ),
              ),
              const Spacer(),
              IconButton(
                tooltip: 'Close',
                icon: const Icon(Icons.close, size: 18),
                onPressed: () => Navigator.of(context).maybePop(),
              ),
            ],
          ),
          const SizedBox(height: 4),
          Text(
            'Guiding is held off while the guider watches the star drift, so it can '
            'measure your mount and seeing and suggest min-move, exposure and Dec '
            'backlash settings. Needs a selected star and a calibrated mount; takes '
            'the chosen time plus a minute or two for backlash.',
            style: text.bodySmall?.copyWith(color: AraColors.textSecondary),
          ),
          const SizedBox(height: 12),
          Expanded(
            child: SingleChildScrollView(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.stretch,
                children: [
                  if (status.isRunning) _Progress(status: status),
                  if (status.hasStats) _Figures(status: status),
                  if (status.backlashStatus != null ||
                      status.backlashResult != null)
                    _Backlash(status: status),
                  if (status.isDone && recs != null)
                    _Suggestions(
                      recs: recs,
                      accepted: _accept,
                      applied: state.applied,
                      enabled: !state.busy,
                      onToggle: (key, on) => setState(
                        () => on ? _accept.add(key) : _accept.remove(key),
                      ),
                    ),
                  if (status.isFailed)
                    Padding(
                      padding: const EdgeInsets.only(top: 8),
                      child: Text(
                        status.error == null
                            ? 'The measurement failed.'
                            : 'The measurement failed: ${status.error}',
                        key: const Key('guiding-assistant-failed'),
                        style: text.bodyMedium?.copyWith(
                          color: AraColors.accentError,
                        ),
                      ),
                    ),
                ],
              ),
            ),
          ),
          const SizedBox(height: 8),
          // A Wrap, not a Row: at the dialog's width the duration, the
          // backlash tick, Start and Accept do not fit on one line.
          Wrap(
            spacing: 12,
            runSpacing: 8,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              if (!status.isRunning) ...[
                DropdownButton<double>(
                  key: const Key('guiding-assistant-duration'),
                  value: _durationSec,
                  isDense: true,
                  items: [
                    for (final d in GuidingAssistantPanel.durations)
                      DropdownMenuItem(
                        value: d,
                        child: Text('${(d / 60).round()} min'),
                      ),
                  ],
                  onChanged: state.busy
                      ? null
                      : (v) => setState(() => _durationSec = v ?? 120),
                ),
                Row(
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Checkbox(
                      key: const Key('guiding-assistant-backlash'),
                      value: _measureBacklash,
                      visualDensity: VisualDensity.compact,
                      onChanged: state.busy
                          ? null
                          : (v) => setState(() => _measureBacklash = v ?? true),
                    ),
                    const Text('Measure Dec backlash'),
                  ],
                ),
                FilledButton.icon(
                  key: const Key('guiding-assistant-start'),
                  onPressed: !status.connected || state.busy
                      ? null
                      : () => unawaited(
                          notifier.start(
                            durationSec: _durationSec,
                            measureBacklash: _measureBacklash,
                          ),
                        ),
                  icon: const Icon(Icons.play_arrow, size: 16),
                  label: Text(
                    status.isDone || status.isFailed ? 'Run again' : 'Start',
                  ),
                ),
              ] else
                FilledButton.tonalIcon(
                  key: const Key('guiding-assistant-stop'),
                  onPressed: state.busy
                      ? null
                      : () => unawaited(notifier.stop()),
                  icon: const Icon(Icons.stop, size: 16),
                  label: const Text('Stop'),
                ),
              if (status.isDone && recs != null)
                FilledButton.icon(
                  key: const Key('guiding-assistant-apply'),
                  onPressed: state.busy || _accept.isEmpty
                      ? null
                      : () => unawaited(
                          notifier.apply(
                            // Keep the dialog's order, not the tick order.
                            [
                              for (final s in recs.suggestions)
                                if (_accept.contains(s.key)) s.key,
                            ],
                          ),
                        ),
                  icon: const Icon(Icons.check, size: 16),
                  label: const Text('Accept ticked'),
                ),
            ],
          ),
          if (state.error != null)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: Text(
                state.error!,
                key: const Key('guiding-assistant-error'),
                style: text.bodySmall?.copyWith(color: AraColors.accentError),
              ),
            ),
        ],
      ),
    );
  }

  static String _headline(GuidingAssistantState state) {
    final s = state.status;
    if (!state.loaded) return 'Reading…';
    if (!s.connected) {
      return 'Not available — connect a guider that has the assistant.';
    }
    return switch (s.state) {
      'measuring' => 'Measuring drift…',
      'measuring_backlash' => 'Measuring Dec backlash…',
      'done' => 'Done — review the suggestions.',
      'failed' => 'Failed.',
      _ => 'Ready. Loop, pick a star, calibrate once, then Start.',
    };
  }

  static Color _headlineColor(GuidingAssistantState state) {
    final s = state.status;
    if (!s.connected) return AraColors.accentDisconnected;
    if (s.isRunning) return AraColors.accentBusy;
    if (s.isDone) return AraColors.accentConnected;
    if (s.isFailed) return AraColors.accentError;
    return AraColors.textSecondary;
  }
}

class _Progress extends StatelessWidget {
  final GuidingAssistantStatus status;
  const _Progress({required this.status});

  @override
  Widget build(BuildContext context) {
    final p = status.progress;
    final text = Theme.of(context).textTheme;
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          LinearProgressIndicator(
            value: status.state == 'measuring_backlash' ? null : p,
          ),
          const SizedBox(height: 4),
          Text(
            status.state == 'measuring_backlash'
                ? 'Dec backlash: ${status.backlashStatus ?? 'stepping the mount…'}'
                : '${status.elapsedSec?.round() ?? 0} of ${status.durationSec?.round() ?? 0} s · ${status.samples ?? 0} samples',
            key: const Key('guiding-assistant-progress'),
            style: text.bodySmall?.copyWith(color: AraColors.textSecondary),
          ),
        ],
      ),
    );
  }
}

class _Figures extends StatelessWidget {
  final GuidingAssistantStatus status;
  const _Figures({required this.status});

  String _pxArc(double? px, {int digits = 2}) {
    if (px == null) return '—';
    final arc = status.arcsec(px);
    final p = px.toStringAsFixed(digits);
    return arc == null ? '$p px' : '$p px (${arc.toStringAsFixed(digits)}″)';
  }

  @override
  Widget build(BuildContext context) {
    final pa = status.polarAlignErrorArcmin;
    final rows = <(String, String)>[
      ('RA drift', '${_pxArc(status.raDriftPxMin)}/min'),
      ('Dec drift', '${_pxArc(status.decDriftPxMin)}/min'),
      ('RA RMS', _pxArc(status.raRmsPx)),
      ('Dec RMS', _pxArc(status.decRmsPx)),
      ('High-frequency RMS (seeing)', _pxArc(status.highFreqRmsPx)),
      ('RA peak-to-peak', _pxArc(status.raPeakPeakPx)),
      if (pa != null)
        (
          'Polar alignment error',
          '${status.polarAlignErrorIsLowerBound == true ? '≥ ' : ''}${pa.toStringAsFixed(1)}′',
        ),
      if (status.snr != null) ('Star SNR', status.snr!.toStringAsFixed(1)),
    ];
    return _Table(title: 'Measured', rows: rows, keyPrefix: 'ga-figure');
  }
}

class _Backlash extends StatelessWidget {
  final GuidingAssistantStatus status;
  const _Backlash({required this.status});

  @override
  Widget build(BuildContext context) {
    final rows = <(String, String)>[
      if (status.backlashMs != null)
        (
          'Dec backlash',
          '${status.backlashMs!.round()} ms'
              '${status.backlashSigmaMs != null ? ' ± ${status.backlashSigmaMs!.round()}' : ''}'
              '${status.backlashPx != null ? ' (${status.backlashPx!.toStringAsFixed(1)} px)' : ''}',
        ),
      if (status.backlashResult != null)
        ('Result', status.backlashResult!)
      else if (status.backlashStatus != null)
        ('Status', status.backlashStatus!),
    ];
    if (rows.isEmpty) return const SizedBox.shrink();
    return _Table(title: 'Dec backlash', rows: rows, keyPrefix: 'ga-backlash');
  }
}

class _Suggestions extends StatelessWidget {
  final GuidingAssistantRecommendations recs;
  final Set<String> accepted;
  final List<String> applied;
  final bool enabled;
  final void Function(String key, bool on) onToggle;
  const _Suggestions({
    required this.recs,
    required this.accepted,
    required this.applied,
    required this.enabled,
    required this.onToggle,
  });

  @override
  Widget build(BuildContext context) {
    final text = Theme.of(context).textTheme;
    return Padding(
      padding: const EdgeInsets.only(top: 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('Suggested', style: text.titleSmall),
          if (recs.suggestions.isEmpty)
            Padding(
              padding: const EdgeInsets.only(top: 4),
              child: Text(
                'Nothing to change — your settings already suit this run.',
                key: const Key('guiding-assistant-no-suggestions'),
                style: text.bodySmall?.copyWith(color: AraColors.textSecondary),
              ),
            ),
          for (final s in recs.suggestions)
            CheckboxListTile(
              key: Key('ga-suggest-${s.key}'),
              dense: true,
              controlAffinity: ListTileControlAffinity.leading,
              contentPadding: EdgeInsets.zero,
              value: accepted.contains(s.key),
              onChanged: enabled ? (v) => onToggle(s.key, v ?? false) : null,
              title: Text(s.label),
              subtitle: Text(
                applied.contains(s.key) ? '${s.value} · applied' : s.value,
                style: text.bodySmall?.copyWith(
                  color: applied.contains(s.key)
                      ? AraColors.accentConnected
                      : AraColors.textSecondary,
                ),
              ),
            ),
          if (recs.minMoveSeeingBased == true)
            Text(
              'Min-move is set from the seeing, so guiding ignores the twinkle and chases the drift.',
              style: text.bodySmall?.copyWith(color: AraColors.textSecondary),
            ),
          for (final m in recs.messages)
            Padding(
              padding: const EdgeInsets.only(top: 6),
              child: Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Icon(
                    Icons.info_outline,
                    size: 14,
                    color: AraColors.accentInfo,
                  ),
                  const SizedBox(width: 6),
                  Expanded(child: Text(m.text, style: text.bodySmall)),
                ],
              ),
            ),
        ],
      ),
    );
  }
}

class _Table extends StatelessWidget {
  final String title;
  final List<(String, String)> rows;
  final String keyPrefix;
  const _Table({
    required this.title,
    required this.rows,
    required this.keyPrefix,
  });

  @override
  Widget build(BuildContext context) {
    final text = Theme.of(context).textTheme;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(title, style: text.titleSmall),
        const SizedBox(height: 4),
        for (var i = 0; i < rows.length; i++)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 2),
            child: Row(
              children: [
                Expanded(
                  child: Text(
                    rows[i].$1,
                    style: text.bodySmall?.copyWith(
                      color: AraColors.textSecondary,
                    ),
                  ),
                ),
                Text(
                  rows[i].$2,
                  key: Key('$keyPrefix-$i'),
                  style: text.bodyMedium,
                ),
              ],
            ),
          ),
      ],
    );
  }
}
