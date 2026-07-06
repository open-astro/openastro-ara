import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/calibration/calibration_models.dart';
import '../../state/app_shell_state.dart';
import '../../state/calibration/calibration_state.dart';
import '../../state/sequencer/sequence_list_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../theme/ara_colors.dart';
import '../../widgets/library/load_more_button.dart';

/// §39.10 Calibration screen — live over `/api/v1/calibration/*`.
///
/// Sessions tab: every imaging session with its per-filter summary and
/// flats/darks coverage, plus the §39.5 "Capture Matching Flats" flow — the
/// server generates a runnable §38 sequence that replays the session's
/// filter/focus/gain state, and the client jumps straight to it in the Run tab.
/// Dark Library tab: catalogued dark groups + the §39.8 dark-matrix build.
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
        message: 'Could not load sessions: $e',
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
            const SizedBox(height: 8),
            Row(
              children: [
                _CoverageBadge(
                  label: 'Flats',
                  covered: session.matchingFlatsAvailable,
                ),
                const SizedBox(width: 8),
                _CoverageBadge(
                  label: 'Darks',
                  covered: session.matchingDarksAvailable,
                ),
                const Spacer(),
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
                  label: const Text('Capture Matching Flats'),
                ),
              ],
            ),
          ],
        ),
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
  final _frames = TextEditingController(text: '20');
  final _targetAdu = TextEditingController(text: '32000');
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _frames.dispose();
    _targetAdu.dispose();
    super.dispose();
  }

  Future<void> _generate() async {
    final api = ref.read(calibrationApiProvider);
    if (api == null) return;
    final frames = int.tryParse(_frames.text.trim());
    final aduText = _targetAdu.text.trim();
    final adu = int.tryParse(aduText);
    if (frames == null || frames <= 0) {
      setState(() => _error = 'Frames per filter must be a positive number.');
      return;
    }
    // Advisory or not, stray text must not silently become "no override".
    if (aduText.isNotEmpty && adu == null) {
      setState(() => _error = 'Target ADU must be a whole number (or empty).');
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final result = await api.generateMatchingFlats(
        widget.sessionId,
        frameCount: frames,
        targetAdu: adu,
      );
      // Post-await UI work gates on State.mounted (the dialog may have been
      // dismissed while the request was in flight).
      if (!mounted) return;
      final id = result.generatedSequenceId;
      if (id == null) {
        setState(() {
          _busy = false;
          _error = 'The server returned a plan but no stored sequence.';
        });
        return;
      }
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(
          content: Text(
            '"${result.generatedSequenceName}" saved — ${result.totalFlatFrames} flats across ${widget.filterNames.length} filter(s).',
          ),
        ),
      );
      openGeneratedSequence(context, ref, id);
    } on Exception catch (e) {
      if (!mounted) return;
      setState(() {
        _busy = false;
        _error = 'Generation failed: $e';
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final filters = widget.filterNames.join(', ');
    return AlertDialog(
      title: const Text('Capture Matching Flats'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'Generates a flat sequence replaying this session\'s state per '
            'filter ($filters): wheel position, focus, gain and offset.',
            style: Theme.of(
              context,
            ).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary),
          ),
          const SizedBox(height: 12),
          TextField(
            controller: _frames,
            keyboardType: TextInputType.number,
            decoration: const InputDecoration(labelText: 'Flats per filter'),
          ),
          const SizedBox(height: 8),
          TextField(
            controller: _targetAdu,
            keyboardType: TextInputType.number,
            decoration: const InputDecoration(
              labelText: 'Target ADU (advisory)',
              helperText:
                  'Panel brightness/exposure tuning stays manual for now',
            ),
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
      actions: [
        TextButton(
          onPressed: _busy ? null : () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: _busy ? null : _generate,
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
        message: 'Could not load the dark library: $e',
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
        _error = 'Build request failed: $e';
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
          'temperature combination. Leave temperatures empty to capture at ambient.',
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
