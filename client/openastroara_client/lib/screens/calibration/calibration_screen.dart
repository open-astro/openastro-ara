import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/calibration/calibration_models.dart';
import '../../state/app_shell_state.dart';
import '../../state/calibration/calibration_state.dart';
import '../../state/equipment/camera_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/settings/imaging_defaults_state.dart';
import '../../state/settings/safety_policies_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../theme/ara_colors.dart';
import '../../widgets/library/load_more_button.dart';
import '../../util/friendly_error.dart';

/// §39.10 Calibration screen — live over `/api/v1/calibration/*`.
///
/// Sessions tab: every imaging session with its per-filter summary and
/// flats/bias/darks coverage, plus the §39.5 calibration-frames flow — the
/// server generates a runnable §38 sequence that replays the session's
/// filter/focus/gain state, and the client jumps straight to it in the Run tab.
/// Dark Library tab: catalogued dark and bias groups + the §39.8 dark-matrix
/// build.
class CalibrationScreen extends StatelessWidget {
  const CalibrationScreen({super.key});

  @override
  Widget build(BuildContext context) {
    return DefaultTabController(
      length: 2,
      child: Scaffold(
        appBar: AppBar(
          title: const Text('Calibration'),
          bottom: const TabBar(
            tabs: [
              Tab(text: 'Sessions'),
              Tab(text: 'Dark Library'),
            ],
          ),
        ),
        body: const TabBarView(children: [_SessionsTab(), _DarkLibraryTab()]),
      ),
    );
  }
}

/// Shared "jump to the generated sequence" hop: refresh the sequence list,
/// select the new id, switch the shell to the Run tab, and unwind back to it.
void openGeneratedSequence(BuildContext context, WidgetRef ref, String id) {
  ref.invalidate(sequenceListProvider);
  ref.read(selectedSequenceIdProvider.notifier).select(id);
  ref.read(selectedTabIndexProvider.notifier).select(kRunTabIndex);
  Navigator.of(context).popUntil((route) => route.isFirst);
}

// ── Sessions ──────────────────────────────────────────────────────────────

class _SessionsTab extends ConsumerWidget {
  const _SessionsTab();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final sessions = ref.watch(calibrationSessionsProvider);
    return sessions.when(
      loading: () => const Center(child: CircularProgressIndicator()),
      error: (e, _) => _ErrorRetry(
        message: friendlyError(e, action: 'load the sessions'),
        onRetry: () => ref.read(calibrationSessionsProvider.notifier).refresh(),
      ),
      data: (list) {
        if (list == null) {
          return const _EmptyNote('Connect to a server to see its sessions.');
        }
        if (list.isEmpty) {
          return const _EmptyNote(
            'No imaging sessions with light frames yet — capture some lights first.',
          );
        }
        final hasMore = ref.read(calibrationSessionsProvider.notifier).hasMore;
        // Lazy builder: paged catalogs can grow well past 200 cards (r3).
        return RefreshIndicator(
          onRefresh: () =>
              ref.read(calibrationSessionsProvider.notifier).refresh(),
          child: ListView.builder(
            itemCount: list.length + (hasMore ? 1 : 0),
            itemBuilder: (context, i) {
              if (i == list.length) {
                return Center(
                  child: Padding(
                    padding: const EdgeInsets.all(12),
                    child: LoadMoreButton(
                      onLoadMore: () => ref
                          .read(calibrationSessionsProvider.notifier)
                          .loadMore(),
                    ),
                  ),
                );
              }
              return _SessionCard(session: list[i]);
            },
          ),
        );
      },
    );
  }
}

class _SessionCard extends ConsumerWidget {
  final CalibrationSession session;
  const _SessionCard({required this.session});

  String _dateLabel() {
    final d = session.sessionStartUtc.toLocal();
    return '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    return Card(
      margin: const EdgeInsets.all(8),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              '${_dateLabel()} — ${session.targetName}',
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: 4),
            Text(
              [
                '${session.lightFrameCount} lights',
                for (final f in session.filtersUsed)
                  '${f.filterName} ${f.lightFrameCount}×${_secondsLabel(f.meanExposureSeconds)}',
              ].join(' · '),
              style: Theme.of(
                context,
              ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
            ),
            const SizedBox(height: 6),
            for (final f in session.filtersUsed) _FilterFlatsLine(filter: f),
            const SizedBox(height: 8),
            Row(
              children: [
                // Wraps on a narrow window rather than pushing the action off.
                Expanded(
                  child: Wrap(
                    spacing: 8,
                    runSpacing: 6,
                    children: [
                      _CoverageBadge(
                        label: 'Flats',
                        covered: session.matchingFlatsAvailable,
                      ),
                      _CoverageBadge(
                        label: 'Darks',
                        covered: session.matchingDarksAvailable,
                      ),
                      _CoverageBadge(
                        label: 'Bias',
                        covered: session.matchingBiasAvailable,
                      ),
                    ],
                  ),
                ),
                TextButton.icon(
                  onPressed: () => showDialog<void>(
                    context: context,
                    builder: (_) => MatchingFlatsDialog(
                      sessionId: session.id,
                      targetName: session.targetName,
                      filterNames: [
                        for (final f in session.filtersUsed) f.filterName,
                      ],
                    ),
                  ),
                  icon: const Icon(
                    Icons.add_photo_alternate_outlined,
                    size: 16,
                  ),
                  // Flats carry over while the setup is unchanged: once every
                  // filter is covered the action is a deliberate re-shoot.
                  label: Text(_actionLabel(session)),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

/// The card's calibration action names what the lights still need; with
/// everything covered, it's a re-shoot.
String _actionLabel(CalibrationSession s) {
  final missing = [
    if (!s.matchingFlatsAvailable) 'Flats',
    if (!s.matchingBiasAvailable) 'Bias',
    if (!s.matchingDarksAvailable) 'Darks',
  ];
  if (missing.isEmpty) return 'Re-shoot flats';
  if (missing.length == 1) return 'Capture ${missing.single}';
  return 'Capture ${missing.take(missing.length - 1).join(', ')} & ${missing.last}';
}

const _months = [
  'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', //
  'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
];

String _shortDate(DateTime utc) {
  final d = utc.toLocal();
  return '${_months[d.month - 1]} ${d.day}';
}

/// One filter's flats: which flats cover it (from any night with the same
/// setup), or why it still needs them.
String flatsLineText(CalibrationFilterSummary f) {
  if (f.hasFlats) {
    final when = f.matchingFlatsNewestUtc;
    final from = when == null ? '' : ' from ${_shortDate(when)}';
    final count = f.matchingFlatFrames == 1 ? '1 flat' : '${f.matchingFlatFrames} flats';
    return '${f.filterName} · $count$from';
  }
  return '${f.filterName} · ${f.flatsNote ?? 'needs flats'}';
}

class _FilterFlatsLine extends StatelessWidget {
  final CalibrationFilterSummary filter;
  const _FilterFlatsLine({required this.filter});

  @override
  Widget build(BuildContext context) {
    final color = filter.hasFlats
        ? AraColors.accentConnected
        : AraColors.accentBusy;
    final style = Theme.of(context).textTheme.bodySmall;
    // The covered case's note ("assumes the camera wasn't rotated") is a
    // caveat, not a problem: shown muted after the count.
    final caveat = filter.hasFlats ? filter.flatsNote : null;
    return Padding(
      padding: const EdgeInsets.only(bottom: 2),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Icon(
            filter.hasFlats ? Icons.check : Icons.priority_high,
            size: 14,
            color: color,
          ),
          const SizedBox(width: 6),
          Expanded(
            child: Text.rich(
              TextSpan(
                text: flatsLineText(filter),
                style: style,
                children: [
                  if (caveat != null)
                    TextSpan(
                      text: ' — $caveat',
                      style: style?.copyWith(color: AraColors.textSecondary),
                    ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }
}

String _secondsLabel(double seconds) {
  final text = seconds.toStringAsFixed(
    seconds == seconds.roundToDouble() ? 0 : 1,
  );
  return '${text}s';
}

class _CoverageBadge extends StatelessWidget {
  final String label;
  final bool covered;
  const _CoverageBadge({required this.label, required this.covered});

  @override
  Widget build(BuildContext context) {
    final color = covered ? AraColors.accentConnected : AraColors.accentBusy;
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      decoration: BoxDecoration(
        border: Border.all(color: color),
        borderRadius: BorderRadius.circular(10),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(
            covered ? Icons.check : Icons.priority_high,
            size: 12,
            color: color,
          ),
          const SizedBox(width: 4),
          Text(
            covered ? '$label matched' : '$label needed',
            style: Theme.of(
              context,
            ).textTheme.bodySmall?.copyWith(color: color),
          ),
        ],
      ),
    );
  }
}

/// §39.5 dialog: frame count + target ADU, then generate-and-open. The server
/// persists a runnable sequence that replays the session's per-filter
/// filter/focus/gain state; on success we jump straight to it in the Run tab.
/// Takes plain fields (not a model) so both the Calibration screen and the
/// Image Library session cards can launch it.
class MatchingFlatsDialog extends ConsumerStatefulWidget {
  final String sessionId;
  final String targetName;
  final List<String> filterNames;
  const MatchingFlatsDialog({
    super.key,
    required this.sessionId,
    required this.targetName,
    required this.filterNames,
  });

  @override
  ConsumerState<MatchingFlatsDialog> createState() =>
      _MatchingFlatsDialogState();
}

class _MatchingFlatsDialogState extends ConsumerState<MatchingFlatsDialog> {
  late final _frames = TextEditingController(
    text: ref.read(safetyPoliciesProvider).flatFramesPerFilter.toString(),
  );
  final _coolTo = TextEditingController();
  bool _busy = false;
  String? _error;

  // The set-point the session's lights ran at (null = taken warm) and their
  // coverage. Fetched on open: the Image Library's session cards don't carry
  // them.
  bool _loadingSession = true;
  double? _lightsSetpointC;
  List<CalibrationFilterSummary> _filters = const [];
  bool _cool = false;
  // What to shoot: each kind starts ticked when the lights still need it.
  // Flats from the same setup carry over, so a covered filter is skipped
  // unless the user deliberately re-shoots it.
  bool _flats = true;
  bool _reshoot = false;
  bool _biasNeeded = false;
  bool _bias = false;
  bool _darksNeeded = false;
  bool _darks = false;
  // A panel doesn't always seal out stray light: open its cover and wait for
  // the user to put the lens cap on before the bias and darks.
  bool _lensCap = true;
  // Mount motion is opt-in for every run: never pre-ticked, not even from the
  // profile's end-of-night setting (a bench or indoor rig has no clear path).
  bool _park = false;

  @override
  void initState() {
    super.initState();
    _coolTo.text = _degrees(ref.read(imagingDefaultsProvider).coolerTargetC);
    _loadSetpoint();
  }

  Future<void> _loadSetpoint() async {
    CalibrationSession? session;
    try {
      session = await ref.read(calibrationApiProvider)?.getSession(widget.sessionId);
    } on Exception {
      // No coverage known: flats stay ticked and the user can still cool.
    }
    final setpoint = session?.coolerSetpointC;
    if (!mounted) return;
    setState(() {
      _loadingSession = false;
      _lightsSetpointC = setpoint;
      _filters = session?.filtersUsed ?? const [];
      _flats = session == null || !session.matchingFlatsAvailable;
      _biasNeeded = session != null && !session.matchingBiasAvailable;
      _bias = _biasNeeded;
      _darksNeeded = session != null && !session.matchingDarksAvailable;
      _darks = _darksNeeded;
      _cool = setpoint != null;
      if (setpoint != null) _coolTo.text = _degrees(setpoint);
    });
  }

  List<CalibrationFilterSummary> get _covered => [
    for (final f in _filters)
      if (f.hasFlats) f,
  ];

  bool get _allCovered =>
      _filters.isNotEmpty && _covered.length == _filters.length;

  bool get _shootBias => _biasNeeded && _bias;
  bool get _shootDarks => _darksNeeded && _darks;
  bool get _afterFlats => _shootBias || _shootDarks;

  /// "Capture Flats, Bias & Darks": what the run will shoot.
  String get _title {
    final parts = [
      if (_flats) 'Flats',
      if (_shootBias) 'Bias',
      if (_shootDarks) 'Darks',
    ];
    if (parts.isEmpty) return 'Capture Calibration Frames';
    if (parts.length == 1) return 'Capture ${parts.single}';
    return 'Capture ${parts.take(parts.length - 1).join(', ')} & ${parts.last}';
  }

  static String _degrees(double c) =>
      c == c.roundToDouble() ? c.toStringAsFixed(0) : c.toStringAsFixed(1);

  @override
  void dispose() {
    _frames.dispose();
    _coolTo.dispose();
    super.dispose();
  }

  Future<void> _generate() async {
    final api = ref.read(calibrationApiProvider);
    if (api == null) return;
    final frames = int.tryParse(_frames.text.trim());
    if (_flats && (frames == null || frames <= 0)) {
      setState(() => _error = 'Flats per filter must be a positive number.');
      return;
    }
    final coolTo = double.tryParse(_coolTo.text.trim());
    if (_cool && coolTo == null) {
      setState(() => _error = 'Cooling temperature must be a number in °C.');
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final result = await api.generateMatchingFlats(
        widget.sessionId,
        frameCount: _flats ? frames : null,
        coolCamera: _cool,
        coolToTemperatureC: _cool ? coolTo : null,
        parkMountAfter: _park,
        includeFlats: _flats,
        // With every filter covered, ticking Flats is the re-shoot.
        includeCoveredFilters: _flats && (_reshoot || _allCovered),
        includeBias: _shootBias,
        includeDarks: _shootDarks,
        pauseForLensCap: _afterFlats && _lensCap,
      );
      // Post-await UI work gates on State.mounted (the dialog may have been
      // dismissed while the request was in flight).
      if (!mounted) return;
      final id = result.generatedSequenceId;
      if (id == null) {
        setState(() {
          _busy = false;
          _error = result.totalFlatFrames == 0 &&
                  result.totalBiasFrames == 0 &&
                  result.totalDarkFrames == 0
              ? 'Nothing to shoot: the lights already have what\'s ticked.'
              : 'The server returned a plan but no stored sequence.';
        });
        return;
      }
      final shot = [
        if (result.totalFlatFrames > 0) '${result.totalFlatFrames} flats',
        if (result.totalBiasFrames > 0) '${result.totalBiasFrames} bias',
        if (result.totalDarkFrames > 0) '${result.totalDarkFrames} darks',
      ];
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(
            '"${result.generatedSequenceName}" saved — ${shot.join(' + ')}.',
          ),
        ),
      );
      openGeneratedSequence(context, ref, id);
    } on Exception catch (e) {
      if (!mounted) return;
      setState(() {
        _busy = false;
        _error = friendlyError(e, action: 'generate the calibration');
      });
    }
  }

  String _flatsSubtitle() {
    final filters = widget.filterNames.join(', ');
    final covered = _covered;
    if (covered.isEmpty) {
      return 'For each filter ($filters) at its wheel position, focus, rotator '
          'angle, gain and offset. Exposure and panel brightness come from two '
          'test frames.';
    }
    final coveredLines = [
      for (final f in covered)
        '${flatsLineText(f)}${f.flatsNote != null ? ' — ${f.flatsNote}' : ''}',
    ].join('\n');
    if (_allCovered) {
      return 'Every filter already has flats from this setup:\n$coveredLines\n'
          'Tick only to re-shoot them: new dust, the camera taken off, a '
          'different filter.';
    }
    final missing = [
      for (final f in _filters)
        if (!f.hasFlats) f.filterName,
    ].join(', ');
    return 'Shoots $missing. Already covered by flats from this setup '
        '(skipped):\n$coveredLines';
  }

  Widget _choice({
    required String title,
    required String subtitle,
    required bool value,
    required ValueChanged<bool>? onChanged,
  }) {
    return CheckboxListTile(
      value: value,
      onChanged: onChanged == null || _busy
          ? null
          : (v) => setState(() => onChanged(v ?? false)),
      dense: true,
      contentPadding: EdgeInsets.zero,
      controlAffinity: ListTileControlAffinity.leading,
      title: Text(title),
      subtitle: Text(subtitle),
    );
  }

  @override
  Widget build(BuildContext context) {
    final partlyCovered = _covered.isNotEmpty && !_allCovered;
    final nothingToShoot = !_flats && !_afterFlats;
    final sectionStyle = Theme.of(context).textTheme.titleSmall;
    const checking = 'Checking what the lights already have…';
    return AlertDialog(
      title: Text(_title),
      // Scrolls on a short window (cooling + park options outgrow 668 px).
      content: SingleChildScrollView(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text('Shoot', style: sectionStyle),
            _choice(
              title: 'Flats',
              subtitle: _loadingSession ? checking : _flatsSubtitle(),
              value: _flats,
              onChanged: _loadingSession ? null : (v) => _flats = v,
            ),
            if (_flats && partlyCovered)
              Padding(
                padding: const EdgeInsets.only(left: 32),
                child: _choice(
                  title: 'Also re-shoot the covered filters',
                  subtitle: 'Only if something in the optical train changed.',
                  value: _reshoot,
                  onChanged: (v) => _reshoot = v,
                ),
              ),
            if (_flats)
              Padding(
                padding: const EdgeInsets.only(left: 32, bottom: 8),
                child: TextField(
                  controller: _frames,
                  keyboardType: TextInputType.number,
                  decoration: const InputDecoration(labelText: 'Flats per filter'),
                ),
              ),
            _choice(
              title: 'Bias',
              subtitle: _loadingSession
                  ? checking
                  : _biasNeeded
                  ? '50 at the camera\'s shortest exposure for each gain, offset '
                        'and binning the lights used.'
                  : 'The lights already have matching bias frames.',
              value: _shootBias,
              onChanged: _biasNeeded ? (v) => _bias = v : null,
            ),
            _choice(
              title: 'Darks',
              subtitle: _loadingSession
                  ? checking
                  : _darksNeeded
                  ? '30 for each exposure and gain the lights used, as long as '
                        'the lights themselves, at their temperature.'
                  : 'The lights already have matching darks.',
              value: _shootDarks,
              onChanged: _darksNeeded ? (v) => _darks = v : null,
            ),
            const Divider(height: 24),
            Text('Options', style: sectionStyle),
            SwitchListTile(
              value: _cool,
              onChanged: _loadingSession || _busy
                  ? null
                  : (v) => setState(() => _cool = v),
              dense: true,
              contentPadding: EdgeInsets.zero,
              title: const Text('Cool the camera first'),
              subtitle: Text(
                _loadingSession
                    ? 'Checking the lights\' temperature…'
                    : _lightsSetpointC != null
                    ? 'The lights ran at ${_degrees(_lightsSetpointC!)} °C.'
                    : 'The lights were taken with the cooler off.',
              ),
            ),
            if (_cool)
              TextField(
                controller: _coolTo,
                keyboardType: const TextInputType.numberWithOptions(
                  signed: true,
                  decimal: true,
                ),
                decoration: const InputDecoration(
                  labelText: 'Cool to (°C)',
                  // Indoors the cooler may not reach a set-point a cold night
                  // made easy; CoolCamera gives up after its stall timeout and
                  // the run carries on, each frame recording its real
                  // temperature.
                  helperText:
                      'Indoors it may not get there: after a few minutes the run '
                      'starts at the closest temperature reached.',
                  helperMaxLines: 2,
                ),
              ),
            if (_afterFlats)
              _choice(
                title: _flats
                    ? 'Pause for the lens cap after the flats'
                    : 'Pause for the lens cap first',
                subtitle: 'Opens the flat panel\'s cover and waits while you put '
                    'the lens cap on, then press Resume to take the bias and '
                    'darks. A panel doesn\'t always keep out all the light.',
                value: _lensCap,
                onChanged: (v) => _lensCap = v,
              ),
            _choice(
              title: 'Park the mount at the end',
              subtitle: 'Leave this off unless the mount has a clear path to park.',
              value: _park,
              onChanged: (v) => _park = v,
            ),
            if (_error != null) ...[
              const SizedBox(height: 8),
              Text(
                _error!,
                style: Theme.of(
                  context,
                ).textTheme.bodySmall?.copyWith(color: AraColors.accentError),
              ),
            ],
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: _busy ? null : () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: _busy || nothingToShoot ? null : _generate,
          child: _busy
              ? const SizedBox(
                  width: 16,
                  height: 16,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : const Text('Generate & open'),
        ),
      ],
    );
  }
}

// ── Dark library ──────────────────────────────────────────────────────────

class _DarkLibraryTab extends ConsumerWidget {
  const _DarkLibraryTab();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final status = ref.watch(darkLibraryStatusProvider);
    return status.when(
      loading: () => const Center(child: CircularProgressIndicator()),
      error: (e, _) => _ErrorRetry(
        message: friendlyError(e, action: 'load the dark library'),
        onRetry: () => ref.read(darkLibraryStatusProvider.notifier).refresh(),
      ),
      data: (state) {
        if (state == null) {
          return const _EmptyNote(
            'Connect to a server to see its dark library.',
          );
        }
        return RefreshIndicator(
          onRefresh: () =>
              ref.read(darkLibraryStatusProvider.notifier).refresh(),
          child: ListView(
            padding: const EdgeInsets.all(12),
            children: [
              _DarkStatusHeader(state: state),
              const SizedBox(height: 12),
              if (state.entries.isEmpty)
                const _EmptyNote(
                  'No dark frames catalogued yet — request a build below, then '
                  'run the generated sequence on a cloudy night with the scope covered.',
                )
              else
                ...state.entries.map((e) => _DarkEntryRow(entry: e)),
              const Divider(height: 32),
              Text('Bias', style: Theme.of(context).textTheme.titleSmall),
              if (state.biasEntries.isEmpty)
                const _EmptyNote(
                  'No bias frames catalogued yet — tick Bias when capturing a '
                  'session\'s calibration frames.',
                )
              else
                ...state.biasEntries.map((e) => _BiasEntryRow(entry: e)),
              const Divider(height: 32),
              const DarkBuildForm(),
            ],
          ),
        );
      },
    );
  }
}

class _DarkStatusHeader extends ConsumerWidget {
  final DarkLibraryState state;
  const _DarkStatusHeader({required this.state});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final progress = state.totalCombinations == 0
        ? null
        : '${state.completedCombinations}/${state.totalCombinations} combinations';
    return Row(
      children: [
        Text(
          'Status: ${state.status}',
          style: Theme.of(context).textTheme.titleSmall,
        ),
        if (progress != null) ...[
          const SizedBox(width: 8),
          Text(
            progress,
            style: Theme.of(
              context,
            ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
          ),
        ],
        const Spacer(),
        if (state.generatedSequenceId != null)
          TextButton.icon(
            onPressed: () =>
                openGeneratedSequence(context, ref, state.generatedSequenceId!),
            icon: const Icon(Icons.playlist_play, size: 16),
            label: const Text('Open build sequence'),
          ),
      ],
    );
  }
}

class _DarkEntryRow extends StatelessWidget {
  final DarkLibraryEntry entry;
  const _DarkEntryRow({required this.entry});

  @override
  Widget build(BuildContext context) {
    final gain = entry.gain is int ? 'gain ${entry.gain}' : 'default gain';
    final size = (entry.fileSizeBytes / (1024 * 1024)).toStringAsFixed(1);
    final d = entry.capturedUtc.toLocal();
    final date =
        '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';
    return ListTile(
      dense: true,
      leading: const Icon(Icons.dark_mode_outlined, size: 18),
      title: Text(
        '${_secondsLabel(entry.exposureSeconds)} · $gain · ${entry.temperatureC.toStringAsFixed(0)}°C',
      ),
      subtitle: Text('${entry.frameCount} frames · $size MB · newest $date'),
    );
  }
}

class _BiasEntryRow extends StatelessWidget {
  final BiasLibraryEntry entry;
  const _BiasEntryRow({required this.entry});

  @override
  Widget build(BuildContext context) {
    final gain = entry.gain is int ? 'gain ${entry.gain}' : 'default gain';
    final offset = entry.offset is int ? ' · offset ${entry.offset}' : '';
    final size = (entry.fileSizeBytes / (1024 * 1024)).toStringAsFixed(1);
    final d = entry.capturedUtc.toLocal();
    final date =
        '${d.year}-${d.month.toString().padLeft(2, '0')}-${d.day.toString().padLeft(2, '0')}';
    return ListTile(
      dense: true,
      leading: const Icon(Icons.blur_on_outlined, size: 18),
      title: Text('$gain$offset · bin ${entry.bin}×${entry.bin}'),
      subtitle: Text('${entry.frameCount} frames · $size MB · newest $date'),
    );
  }
}

/// §39.8 build form: comma-separated matrix + frames per combination.
class DarkBuildForm extends ConsumerStatefulWidget {
  const DarkBuildForm({super.key});

  @override
  ConsumerState<DarkBuildForm> createState() => _DarkBuildFormState();
}

class _DarkBuildFormState extends ConsumerState<DarkBuildForm> {
  final _exposures = TextEditingController(text: '60, 120, 300');
  final _gains = TextEditingController(text: '100');
  final _temps = TextEditingController();
  final _frames = TextEditingController(text: '30');
  bool _reuse = true;
  bool _busy = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _temps.text = _defaultTemperatures();
  }

  // Darks only calibrate lights taken at the same temperature, so start from
  // the set-points the catalogued sessions actually ran at; with none, the
  // profile's cooler target when the connected camera can regulate. A user
  // with an uncooled camera still gets an empty field (capture at ambient).
  String _defaultTemperatures() {
    final sessions = ref.read(calibrationSessionsProvider).asData?.value ?? const [];
    final setpoints = <double>{
      for (final s in sessions)
        if (s.coolerSetpointC != null) s.coolerSetpointC!,
    }.toList()..sort();
    if (setpoints.isNotEmpty) {
      return setpoints.map(_MatchingFlatsDialogState._degrees).join(', ');
    }
    final canCool = ref
            .read(cameraStatusProvider)
            .asData
            ?.value
            ?.capabilities
            ?.canSetTemperature ==
        true;
    return canCool
        ? _MatchingFlatsDialogState._degrees(
            ref.read(imagingDefaultsProvider).coolerTargetC)
        : '';
  }

  @override
  void dispose() {
    _exposures.dispose();
    _gains.dispose();
    _temps.dispose();
    _frames.dispose();
    super.dispose();
  }

  List<double>? _parseDoubles(String text) {
    final parts = text
        .split(',')
        .map((p) => p.trim())
        .where((p) => p.isNotEmpty)
        .toList();
    final values = <double>[];
    for (final p in parts) {
      final v = double.tryParse(p);
      if (v == null) return null;
      values.add(v);
    }
    return values;
  }

  Future<void> _submit() async {
    final api = ref.read(calibrationApiProvider);
    if (api == null) return;
    final exposures = _parseDoubles(_exposures.text);
    final temps = _parseDoubles(_temps.text);
    final gains = _parseDoubles(_gains.text);
    final frames = int.tryParse(_frames.text.trim());
    if (exposures == null || exposures.isEmpty) {
      setState(
        () => _error = 'Exposures: comma-separated seconds, e.g. 60, 300',
      );
      return;
    }
    if (gains == null || temps == null) {
      setState(() => _error = 'Gains and temperatures must be numbers.');
      return;
    }
    if (exposures.any((e) => e <= 0)) {
      setState(() => _error = 'Exposures must be positive seconds.');
      return;
    }
    if (gains.any((g) => g < 0)) {
      setState(() => _error = 'Gains cannot be negative.');
      return;
    }
    if (frames == null || frames <= 0) {
      setState(() => _error = 'Frames per combination must be positive.');
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await api.buildDarkLibrary(
        DarkLibraryBuildRequest(
          exposureSecondsList: exposures,
          gainList: gains.map((g) => g.round()).toList(),
          targetTemperatureCList: temps,
          framesPerCombination: frames,
          reuseExistingFrames: _reuse,
        ),
      );
      if (!mounted) return;
      setState(() => _busy = false);
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text(
            'Build sequence generated — open it from the status row above.',
          ),
        ),
      );
      await ref.read(darkLibraryStatusProvider.notifier).refresh();
    } on Exception catch (e) {
      if (!mounted) return;
      setState(() {
        _busy = false;
        _error = friendlyError(e, action: 'build the calibration');
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          'Build dark library',
          style: Theme.of(context).textTheme.titleSmall,
        ),
        const SizedBox(height: 4),
        Text(
          'Generates a ready-to-run sequence covering every exposure × gain × '
          'temperature combination; it cools the camera to each temperature '
          'first. Leave temperatures empty to capture at ambient.',
          style: Theme.of(
            context,
          ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
        ),
        const SizedBox(height: 8),
        TextField(
          controller: _exposures,
          decoration: const InputDecoration(
            labelText: 'Exposures (s, comma-separated)',
          ),
        ),
        const SizedBox(height: 8),
        TextField(
          controller: _gains,
          decoration: const InputDecoration(
            labelText: 'Gains (comma-separated)',
            helperText: 'Empty = camera default gain',
          ),
        ),
        const SizedBox(height: 8),
        TextField(
          controller: _temps,
          decoration: const InputDecoration(
            labelText: 'Sensor temperatures (°C, comma-separated)',
            helperText: 'Empty = capture at ambient (no cooling step)',
          ),
        ),
        const SizedBox(height: 8),
        TextField(
          controller: _frames,
          keyboardType: TextInputType.number,
          decoration: const InputDecoration(
            labelText: 'Frames per combination',
          ),
        ),
        CheckboxListTile(
          value: _reuse,
          onChanged: (v) => setState(() => _reuse = v ?? true),
          dense: true,
          contentPadding: EdgeInsets.zero,
          controlAffinity: ListTileControlAffinity.leading,
          title: const Text('Skip combinations the catalog already covers'),
        ),
        if (_error != null)
          Padding(
            padding: const EdgeInsets.only(bottom: 8),
            child: Text(
              _error!,
              style: Theme.of(
                context,
              ).textTheme.bodySmall?.copyWith(color: AraColors.accentError),
            ),
          ),
        FilledButton.icon(
          onPressed: _busy ? null : _submit,
          icon: _busy
              ? const SizedBox(
                  width: 16,
                  height: 16,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : const Icon(Icons.build_outlined, size: 16),
          label: const Text('Generate build sequence'),
        ),
      ],
    );
  }
}

// ── shared bits ───────────────────────────────────────────────────────────

class _EmptyNote extends StatelessWidget {
  final String message;
  const _EmptyNote(this.message);

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(24),
      child: Center(
        child: Text(
          message,
          textAlign: TextAlign.center,
          style: Theme.of(
            context,
          ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
        ),
      ),
    );
  }
}

class _ErrorRetry extends StatelessWidget {
  final String message;
  final VoidCallback onRetry;
  const _ErrorRetry({required this.message, required this.onRetry});

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(message, textAlign: TextAlign.center),
          const SizedBox(height: 8),
          OutlinedButton(onPressed: onRetry, child: const Text('Retry')),
        ],
      ),
    );
  }
}
