import 'package:file_picker/file_picker.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/log_entry.dart';
import '../../state/support/logs_state.dart';

/// §54 Support tab — a live tail of the daemon's §29.9 logs with a level +
/// substring filter and a "Download daemon log" action. (The §54 bug-report
/// bundle action lands in a following slice.)
class SupportTab extends ConsumerStatefulWidget {
  const SupportTab({super.key});

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
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final entries = await api.tail(
        maxLines: _maxLines,
        minLevel: _minLevel == 'All' ? null : _minLevel,
        containsSubstring: _substringCtl.text,
      );
      if (!mounted) return;
      setState(() => _entries = entries);
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = 'Could not load logs: $e');
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _download() async {
    final api = ref.read(logsApiProvider);
    if (api == null) return;
    setState(() => _downloading = true);
    try {
      final dl = await api.downloadLog();
      final saved = await FilePicker.saveFile(
        dialogTitle: 'Save daemon log',
        fileName: dl.fileName,
        bytes: dl.bytes,
      );
      if (!mounted) return;
      if (saved != null) {
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text('Saved ${dl.fileName}')),
        );
      }
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
    final hasServer = ref.watch(logsApiProvider) != null;
    if (!hasServer) {
      return const Center(
        child: Text('Connect to a server to view daemon logs.'),
      );
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
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
                border: OutlineInputBorder(),
              ),
              // Guard against the in-flight load like the level dropdown — pressing
              // Enter mid-request would otherwise race a second tail whose later
              // completion could show stale results.
              onSubmitted: _loading ? null : (_) => _refresh(),
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
    final entries = _entries;
    if (entries == null) {
      return const Center(child: CircularProgressIndicator());
    }
    if (entries.isEmpty) {
      return const Center(child: Text('No log entries match the filter.'));
    }
    return ListView.builder(
      itemCount: entries.length,
      itemBuilder: (context, i) => _LogRow(entry: entries[i]),
    );
  }
}

class _LogRow extends StatelessWidget {
  const _LogRow({required this.entry});

  final LogEntry entry;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final color = _levelColor(entry.level, theme);
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
                  ?.copyWith(fontFeatures: const [], color: theme.disabledColor),
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
            child: Text(
              entry.message,
              style: theme.textTheme.bodySmall,
            ),
          ),
        ],
      ),
    );
  }

  static Color _levelColor(String level, ThemeData theme) => switch (level) {
        'Error' || 'Fatal' => theme.colorScheme.error,
        'Warning' => Colors.orange,
        'Debug' || 'Verbose' => theme.disabledColor,
        _ => theme.colorScheme.onSurface,
      };

  // Local wall-clock HH:mm:ss; the daemon stamps UTC.
  static String _hms(DateTime utc) {
    final t = utc.toLocal();
    String two(int n) => n.toString().padLeft(2, '0');
    return '${two(t.hour)}:${two(t.minute)}:${two(t.second)}';
  }
}
