import 'dart:async';
import 'dart:io';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../util/stream_save_location.dart';

import '../../models/bug_report_preparation.dart';
import '../../services/client_error_log.dart';
import '../../state/simple_messages_state.dart';
import '../../state/simple_mode_state.dart';
import '../../state/support/bug_report_state.dart';
import 'client_log_card.dart';
import '../inline_choices.dart';
import 'support_card_row.dart';

/// §54 "Send me a bug report" action — prepares a diagnostic bundle on the
/// daemon, shows the user exactly what it contains (a PII disclosure, required
/// before the server will serve it via `?acknowledge=pii`), then downloads the
/// ZIP to a chosen path.
class BugReportCard extends ConsumerStatefulWidget {
  /// Test seam for the OS save-location dialog (no platform channel under
  /// widget tests). Returns the destination path, or null on cancel.
  /// Production leaves it null → [pickStreamSavePath] (directory picker +
  /// collision-safe file name, so the download can stream to the path).
  final Future<String?> Function(String dialogTitle, String suggestedName)?
      savePathPicker;

  /// How often a whole-rig bundle's packing is polled (test seam).
  final Duration pollInterval;

  /// How long packing may take before the card gives up (a night of logs on a
  /// Pi packs in well under a minute; this only stops a wedged daemon).
  final Duration packingTimeout;

  const BugReportCard({
    super.key,
    this.savePathPicker,
    this.pollInterval = const Duration(seconds: 1),
    this.packingTimeout = const Duration(minutes: 15),
  });

  @override
  ConsumerState<BugReportCard> createState() => _BugReportCardState();
}

/// §54.3 sharing modes, in the order the review modal lists them.
const bugReportSharingModes = <(String wire, String label, String detail)>[
  ('full', 'Include everything', 'best for debugging'),
  ('coarse_gps', 'Coarse GPS only', 'site rounded to ~111 km'),
  ('redacted', 'Redact all sensitive items', 'coordinates, serials, hostnames, usernames in paths, private IPs, tokens'),
];

/// What the bundle covers: Ara's newest logs only, or the whole rig's logs
/// (Ara, its NINA layer, AlpacaBridge, the guider, package history and the
/// journal) written in the range. "Last night" is the default: it is what a
/// report about a night at the rig needs.
const bugReportRanges = <(Duration? range, String label)>[
  (Duration(hours: 24), 'Last night (whole rig)'),
  (Duration(days: 3), 'Last 3 days (whole rig)'),
  (Duration(days: 7), 'Last 7 days (whole rig)'),
  (null, 'Ara logs only'),
];

/// The disclosure's name for each source the daemon's manifest reports.
String _sourceLabel(AppLocalizations l, String source) => switch (source) {
  'ara' => l.bugSourceAra,
  'ara-nina' => l.bugSourceAraNina,
  'alpacabridge' => l.bugSourceAlpacaBridge,
  'guider' => l.bugSourceGuider,
  'system' => l.bugSourceSystem,
  'journal' => l.bugSourceJournal,
  _ => source,
};

class _BugReportCardState extends ConsumerState<BugReportCard> {
  bool _busy = false;
  // §54.3 — the spec's default is "Include everything"; the user picks before preparing.
  String _mode = 'full';

  /// Simple Mode's extra Sharing choice: only this device's log, no rig needed.
  static const _clientLogOnly = 'client_log';

  Future<void> _saveClientLog() async {
    setState(() => _busy = true);
    await saveClientLog(context, ref, widget.savePathPicker);
    if (mounted) setState(() => _busy = false);
  }
  int _rangeIndex = 0;
  // Bytes read so far while a whole-rig bundle packs; null when not packing.
  int? _packed;
  // Simple Mode's review, answered in the card.
  BugReportPreparation? _review;
  Completer<bool>? _reviewAnswer;

  @override
  void dispose() {
    _reviewAnswer?.complete(false);
    super.dispose();
  }

  void _answerReview(bool go) {
    _reviewAnswer?.complete(go);
    setState(() {
      _review = null;
      _reviewAnswer = null;
    });
  }

  Future<void> _run() async {
    final api = ref.read(bugReportApiProvider);
    if (api == null) return;
    setState(() => _busy = true);
    try {
      final range = bugReportRanges[_rangeIndex].$1;
      var prep = await api.prepare(
          mode: _mode,
          since: range == null ? null : DateTime.now().toUtc().subtract(range));
      // A whole-rig bundle packs in the background on the daemon: poll it.
      final deadline = DateTime.now().add(widget.packingTimeout);
      while (prep.isPreparing) {
        if (!mounted) return;
        setState(() => _packed = prep.estimatedSizeBytes);
        if (DateTime.now().isAfter(deadline)) {
          throw const _PackingFailed('the rig is still packing the logs; try again in a moment');
        }
        await Future<void>.delayed(widget.pollInterval);
        prep = await api.status(prep.preparationId);
      }
      if (!mounted) return;
      setState(() => _packed = null);
      if (prep.isFailed) {
        throw _PackingFailed(prep.error ?? 'the rig could not pack the logs');
      }

      // PII gate: the user must see what's in the bundle before it's downloaded.
      final proceed = await _showDisclosure(prep);
      if (!mounted || proceed != true) return;

      // Path first, then STREAM the ZIP to it — the bundle is written chunk by
      // chunk instead of being buffered whole in memory (§54 follow-up).
      final pick = widget.savePathPicker ?? pickStreamSavePath;
      final savePath = await pick(
          'Choose where to save the bug report', 'openastroara-bug-report.zip');
      if (!mounted || savePath == null) return;
      final name = await api.downloadTo(savePath, prep.preparationId);
      // #1111 — the client's own error log goes beside the ZIP. The bundle
      // is built on the daemon, which has never seen this app's failures.
      final clientLogName = await _saveClientLogBeside(savePath);
      if (!mounted) return;
      userNotifier(context)(
        clientLogName == null ? 'Saved $name' : 'Saved $name and $clientLogName',
      );
    } catch (e) {
      if (!mounted) return;
      userNotifier(context)(
        'Bug report failed: ${_friendly(e)}',
        error: true,
        snackBarRed: false,
      );
    } finally {
      if (mounted) {
        setState(() {
          _busy = false;
          _packed = null;
        });
      }
    }
  }

  /// Exports the client log next to the bundle at [zipPath]; returns the
  /// file name written, or null when the export failed (the ZIP is the
  /// deliverable — a client-log hiccup must not turn its success into an
  /// error).
  Future<String?> _saveClientLogBeside(String zipPath) async {
    try {
      final dir = File(zipPath).parent.path;
      final path = uniquePathIn(dir, ClientLogCard.suggestedFileName);
      await ref.read(clientErrorLogProvider).exportTo(path);
      return File(path).uri.pathSegments.last;
    } catch (_) {
      return null;
    }
  }

  Future<bool?> _showDisclosure(BugReportPreparation prep) {
    if (ref.read(simpleModeActiveProvider)) {
      final answer = Completer<bool>();
      setState(() {
        _review = prep;
        _reviewAnswer = answer;
      });
      return answer.future;
    }
    return showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(context.l10n.bugReviewTitle),
        // Scrollable so the disclosure can't overflow on a small viewport or a
        // large accessibility font scale.
        content: SingleChildScrollView(child: _disclosure(prep)),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: Text(context.l10n.planCancel),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: Text(context.l10n.bugReviewDownload),
          ),
        ],
      ),
    );
  }

  /// What the bundle holds: the review's words, dialog or inline.
  Widget _disclosure(BugReportPreparation prep) {
    final l = context.l10n;
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(l.bugReviewContains),
        const SizedBox(height: 8),
        if (prep.sources.isEmpty) Text(l.bugReviewNoSources),
        for (final s in prep.sources) Text(_sourceLine(l, s)),
        Text(switch (prep.sharingMode) {
          'redacted' => l.bugReviewProfileRedacted,
          'coarse_gps' => l.bugReviewProfileCoarse,
          _ => l.bugReviewProfileFull,
        }),
        Text(prep.sharingMode == 'redacted'
            ? l.bugReviewSystemRedacted
            : l.bugReviewSystemFull),
        Text(l.bugReviewClientLog),
        const SizedBox(height: 12),
        Text(l.bugReviewSize(_humanSize(prep.estimatedSizeBytes))),
        const SizedBox(height: 8),
        Text(l.bugReviewTrust),
      ],
    );
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    // Watch (not just read-in-_run) so the autoDispose provider — and its Dio —
    // stays alive for the card's lifetime and can't be torn down mid prepare/
    // download. Also gates the button when no server is connected.
    final api = ref.watch(bugReportApiProvider);
    final review = _review;
    final simple = ref.watch(simpleModeActiveProvider);
    final logOnly = _mode == _clientLogOnly;
    return Card(
      margin: const EdgeInsets.all(8),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: 12,
          children: [
            SupportCardRow(
            icon: Icon(Icons.bug_report_outlined, color: theme.colorScheme.primary),
            body: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('Send me a bug report',
                      style: theme.textTheme.titleSmall),
                  const SizedBox(height: 2),
                  Text(
                    'Bundles the rig\'s logs (Ara, AlpacaBridge, the guider, '
                    'the system journal) + your profile + system info into a '
                    'ZIP you can attach to a report.',
                    style: theme.textTheme.bodySmall,
                  ),
                  if (_packed != null)
                    Text('Packing the logs on the rig… ${_humanSize(_packed!)} read',
                        style: theme.textTheme.bodySmall),
                  if (!logOnly) ...[
                  const SizedBox(height: 4),
                  Row(
                    children: [
                      Text('Covers: ', style: theme.textTheme.bodySmall),
                      Flexible(
                        child: DropdownButton<int>(
                          value: _rangeIndex,
                          // Simple Mode's 44 px; the full UI stays compact.
                          isDense: !simple,
                          isExpanded: true,
                          style: theme.textTheme.bodySmall,
                          onChanged: _busy ? null : (v) => setState(() => _rangeIndex = v ?? 0),
                          items: [
                            for (var i = 0; i < bugReportRanges.length; i++)
                              DropdownMenuItem(
                                value: i,
                                child: Text(bugReportRanges[i].$2, overflow: TextOverflow.ellipsis),
                              ),
                          ],
                        ),
                      ),
                    ],
                  ),
                  ],
                  const SizedBox(height: 4),
                  // §54.3 sharing mode: chosen before the bundle is built, so the
                  // ZIP the user downloads never held what the mode scrubs.
                  Row(
                    children: [
                      Text('Sharing: ', style: theme.textTheme.bodySmall),
                      Flexible(
                        child: DropdownButton<String>(
                          value: _mode,
                          isDense: !simple,
                          isExpanded: true,
                          style: theme.textTheme.bodySmall,
                          onChanged: _busy ? null : (v) => setState(() => _mode = v ?? 'full'),
                          items: [
                            for (final m in bugReportSharingModes)
                              DropdownMenuItem(
                                value: m.$1,
                                child: Tooltip(message: m.$3, child: Text(m.$2, overflow: TextOverflow.ellipsis)),
                              ),
                            if (simple)
                              const DropdownMenuItem(
                                value: _clientLogOnly,
                                child: Text("This app's error log", overflow: TextOverflow.ellipsis),
                              ),
                          ],
                        ),
                      ),
                    ],
                  ),
                ],
              ),
            action: FilledButton.icon(
              onPressed: _busy ? null : logOnly ? _saveClientLog : (api == null ? null : _run),
              icon: _busy
                  ? const SizedBox(
                      width: 16,
                      height: 16,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : Icon(logOnly ? Icons.save_alt : Icons.bug_report, size: 18),
              label: Text(logOnly ? 'Save client log' : 'Prepare & download'),
            ),
            ),
            if (review != null)
              InlineConfirm(
                id: 'bug-report-review',
                title: context.l10n.bugReviewTitle,
                body: _disclosure(review),
                confirmLabel: context.l10n.bugReviewDownload,
                cancelLabel: context.l10n.planCancel,
                onConfirm: () => _answerReview(true),
                onCancel: () => _answerReview(false),
              ),
          ],
        ),
      ),
    );
  }

  // Short, user-facing message — don't leak Dio's verbose toString (URLs etc.).
  static String _friendly(Object e) {
    // The card's own failures (packing failed or timed out) already read as a sentence for the user.
    if (e is _PackingFailed) return e.message;
    if (e is DioException) {
      return switch (e.type) {
        DioExceptionType.connectionTimeout ||
        DioExceptionType.sendTimeout ||
        DioExceptionType.receiveTimeout =>
          'Ara took too long to respond.',
        DioExceptionType.connectionError =>
          'could not reach Ara.',
        DioExceptionType.badResponse =>
          'Ara returned an error (${e.response?.statusCode}).',
        _ => 'something went wrong talking to Ara.',
      };
    }
    return 'an unexpected error occurred.';
  }

  // "• AlpacaBridge logs (camera, mount, focuser, wheel): 2 files, 31.2 MB, cut to
  // the newest part" — or why a source is not there.
  static String _sourceLine(AppLocalizations l, BugReportSourceSummary s) {
    final label = _sourceLabel(l, s.source);
    if (s.files == 0) {
      return '• $label: ${l.bugReviewNone}${s.note == null ? '' : ' (${s.note})'}';
    }
    final files = s.files == 1 ? l.bugReviewOneFile : l.bugReviewFiles('${s.files}');
    final cut = s.truncated ? l.bugReviewCut : '';
    final note = s.note == null ? '' : ' (${s.note})';
    return '• $label: $files, ${_humanSize(s.bytes)}$cut$note';
  }

  static String _humanSize(int bytes) {
    if (bytes < 1024) return '$bytes B';
    final kb = bytes / 1024;
    if (kb < 1024) return '${kb.toStringAsFixed(0)} KB';
    return '${(kb / 1024).toStringAsFixed(1)} MB';
  }
}

/// A whole-rig bundle that failed or timed out on the daemon: [message] is shown as is.
class _PackingFailed implements Exception {
  final String message;
  const _PackingFailed(this.message);
}
