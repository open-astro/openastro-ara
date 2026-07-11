import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../util/stream_save_location.dart';

import '../../models/log_entry.dart';
import '../../state/support/logs_state.dart';
import '../../theme/ara_colors.dart';
import '../../widgets/support/bug_report_card.dart';

/// §54 Support tab — a live tail of the daemon's §29.9 logs with a level +
/// substring filter and a "Download daemon log" action. (The §54 bug-report
/// bundle action lands in a following slice.)
class SupportTab extends ConsumerStatefulWidget {
  /// Test seam for the OS save-location dialog (no platform channel under
  /// widget tests). Returns the destination path, or null on cancel.
  /// Production leaves it null → [pickStreamSavePath] (directory picker +
  /// collision-safe file name, so the download can stream to the path).
  final Future<String?> Function(String dialogTitle, String suggestedName)?
      savePathPicker;

  const SupportTab({super.key, this.savePathPicker});

  @override
  ConsumerState<SupportTab> createState() => _SupportTabState();
}

class _SupportTabState extends ConsumerState<SupportTab> {
  // "All" maps to no min-level filter; the rest are Serilog level names the
  // daemon's LogService ranks.
  static const _levels = <String>['All', 'Debug', 'Information', 'Warning', 'Error'];
  static const _maxLines = 200;

  final _substringCtl = TextEditingController();
  String _minLevel = 'All';
  List<LogEntry>? _entries;
  bool _loading = false;
  bool _downloading = false;
  String? _error;
  // Monotonic refresh id: only the latest call applies its result/error, so a
  // superseded tail (e.g. the old server's request aborted by a mid-flight
  // switch) can't flash a stale error or overwrite newer entries.
  int _refreshGen = 0;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _refresh());
  }

  @override
  void dispose() {
    _substringCtl.dispose();
    super.dispose();
  }

  Future<void> _refresh() async {
    final api = ref.read(logsApiProvider);
    if (api == null) return;
    final gen = ++_refreshGen;
    // Don't clear _error here — keep the last outcome until this call resolves,
    // so a failed refresh leaves the prior entries (and only updates the banner).
    setState(() => _loading = true);
    try {
      final entries = await api.tail(
        maxLines: _maxLines,
        minLevel: _minLevel == 'All' ? null : _minLevel,
        containsSubstring: _substringCtl.text,
      );
      if (!mounted || gen != _refreshGen) return;
      setState(() {
        _entries = entries;
        _error = null;
      });
    } catch (e) {
      if (!mounted || gen != _refreshGen) return;
      setState(() => _error = 'Could not load logs: $e');
    } finally {
      if (mounted && gen == _refreshGen) setState(() => _loading = false);
    }
  }

  Future<void> _download() async {
    final api = ref.read(logsApiProvider);
    if (api == null) return;
    setState(() => _downloading = true);
    try {
      // Path first, then STREAM the download to it — the log is written chunk
      // by chunk instead of being buffered whole in memory (§29.9 follow-up).
      final pick = widget.savePathPicker ?? pickStreamSavePath;
      final savePath =
          await pick('Choose where to save the daemon log', 'openastroara-daemon.log');
      if (!mounted || savePath == null) return;
      final name = await api.downloadLogTo(savePath);
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text('Saved $name')),
      );
    } catch (e) {
      if (!mounted) return;
      ScaffoldMessenger.of(context).showSnackBar(
        SnackBar(content: Text('Download failed: $e')),
      );
    } finally {
      if (mounted) setState(() => _downloading = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    // Re-tail when the active server connects or switches while the tab is open
    // (initState only covers the already-connected-on-open case). The callback
    // fires on change, never during this build, so the setState in _refresh is safe.
    ref.listen(logsApiProvider, (previous, next) {
      if (next != null) {
        // Server connected/switched: drop the prior server's entries so the
        // spinner shows instead of flashing another server's logs while loading.
        // (A same-server refresh keeps its entries — that path is non-destructive.)
        setState(() {
          _entries = null;
          _error = null;
        });
        _refresh();
      }
    });
    final hasServer = ref.watch(logsApiProvider) != null;
    if (!hasServer) {
      return const Center(
        child: Text('Connect to a server to view daemon logs.'),
      );
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        const BugReportCard(),
        const Divider(height: 1),
        _toolbar(context),
        const Divider(height: 1),
        Expanded(child: _body(context)),
      ],
    );
  }

  Widget _toolbar(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(8),
      child: Row(
        children: [
          DropdownButton<String>(
            value: _minLevel,
            items: [
              for (final l in _levels)
                DropdownMenuItem(value: l, child: Text(l)),
            ],
            onChanged: _loading
                ? null
                : (v) {
                    if (v == null) return;
                    setState(() => _minLevel = v);
                    _refresh();
                  },
          ),
          const SizedBox(width: 8),
          Expanded(
            child: TextField(
              controller: _substringCtl,
              decoration: const InputDecoration(
                isDense: true,
                prefixIcon: Icon(Icons.search, size: 18),
                hintText: 'Filter by text…',
                helperText: 'Press Enter to filter',
                border: OutlineInputBorder(),
              ),
              // Re-check _loading at call time (not just the build-time gate) so a
              // submit in the frame before _loading flips can't fire a redundant
              // tail. _refreshGen would absorb it anyway, but this avoids the work.
              onSubmitted: (_) {
                if (!_loading) _refresh();
              },
            ),
          ),
          const SizedBox(width: 8),
          IconButton(
            icon: const Icon(Icons.refresh),
            tooltip: 'Refresh',
            onPressed: _loading ? null : _refresh,
          ),
          const SizedBox(width: 4),
          FilledButton.icon(
            onPressed: _downloading ? null : _download,
            icon: _downloading
                ? const SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.download, size: 18),
            label: const Text('Download log'),
          ),
        ],
      ),
    );
  }

  Widget _body(BuildContext context) {
    final entries = _entries;
    // Nothing loaded yet: a first-load error gets the full retry screen, else the
    // initial spinner.
    if (entries == null) {
      if (_error != null) {
        return Center(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text(_error!, textAlign: TextAlign.center),
              const SizedBox(height: 8),
              OutlinedButton(onPressed: _refresh, child: const Text('Retry')),
            ],
          ),
        );
      }
      return const Center(child: CircularProgressIndicator());
    }
    // Entries already loaded: keep them visible. A failed refresh shows an inline
    // banner above the (last good) list rather than hiding the logs.
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (_error != null) _errorBanner(context),
        Expanded(
          child: entries.isEmpty
              ? const Center(child: Text('No log entries match the filter.'))
              // SelectionArea makes every Text below selectable — drag across rows
              // and copy (Cmd/Ctrl+C) so a user can paste a log excerpt straight
              // into a bug report instead of only the whole-file download.
              : SelectionArea(
                  child: ListView.builder(
                    itemCount: entries.length,
                    itemBuilder: (context, i) => _LogRow(entry: entries[i]),
                  ),
                ),
        ),
      ],
    );
  }

  Widget _errorBanner(BuildContext context) {
    final theme = Theme.of(context);
    return Material(
      color: theme.colorScheme.errorContainer,
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
        child: Row(
          children: [
            Icon(Icons.error_outline,
                size: 18, color: theme.colorScheme.onErrorContainer),
            const SizedBox(width: 8),
            Expanded(
              child: Text(
                _error!,
                style: TextStyle(color: theme.colorScheme.onErrorContainer),
              ),
            ),
            TextButton(onPressed: _refresh, child: const Text('Retry')),
          ],
        ),
      ),
    );
  }
}

class _LogRow extends StatelessWidget {
  const _LogRow({required this.entry});

  final LogEntry entry;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final color = _levelColor(entry.level);
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 64,
            child: Text(
              _hms(entry.timestamp),
              style: theme.textTheme.bodySmall
                  ?.copyWith(color: AraColors.textDisabled),
            ),
          ),
          SizedBox(
            width: 80,
            child: Text(
              entry.level,
              style: theme.textTheme.bodySmall
                  ?.copyWith(color: color, fontWeight: FontWeight.w600),
            ),
          ),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(entry.message, style: theme.textTheme.bodySmall),
                if (entry.source.isNotEmpty)
                  Text(
                    entry.source,
                    style: theme.textTheme.bodySmall?.copyWith(
                        color: AraColors.textDisabled, fontSize: 11),
                  ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  // The app's fixed dark palette (AraColors) — same semantic accents the §51
  // diagnostics panel uses, so log levels read consistently across the app.
  static Color _levelColor(String level) => switch (level) {
        'Error' || 'Fatal' => AraColors.accentError,
        'Warning' => AraColors.accentBusy,
        'Debug' || 'Verbose' => AraColors.textDisabled,
        _ => AraColors.textPrimary,
      };

  // Local wall-clock HH:mm:ss; the daemon stamps UTC.
  static String _hms(DateTime utc) {
    final t = utc.toLocal();
    String two(int n) => n.toString().padLeft(2, '0');
    return '${two(t.hour)}:${two(t.minute)}:${two(t.second)}';
  }
}
