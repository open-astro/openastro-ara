import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/time_sync_api.dart';
import '../../state/time_sync_state.dart';
import 'settings_row.dart';

/// §31.1 waterfall steps 4 + 5, surfaced in the Site panel: the daemon's
/// time-sync status, the "plug a USB GPS into the Pi" guidance with Retry
/// when unsynced, a manual push of this device's clock, and the manual
/// UTC + position entry modal as the last resort.
class TimeSyncSection extends ConsumerWidget {
  const TimeSyncSection({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final status = ref.watch(timeSyncStatusProvider);
    final theme = Theme.of(context);

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        ...status.when(
          loading: () => const [
            Padding(
              padding: EdgeInsets.symmetric(vertical: 12),
              child: Center(
                child: SizedBox(
                  width: 18,
                  height: 18,
                  child: CircularProgressIndicator(strokeWidth: 2),
                ),
              ),
            ),
          ],
          error: (e, _) => [
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 8),
              child: Text(
                'Could not read the server clock state: $e',
                style: TextStyle(color: theme.colorScheme.error),
              ),
            ),
          ],
          data: (s) => s == null
              ? [
                  Padding(
                    padding: const EdgeInsets.symmetric(vertical: 8),
                    child: Text(
                      'No active server — connect to a daemon first.',
                      style: theme.textTheme.bodySmall,
                    ),
                  ),
                ]
              : _statusRows(context, s),
        ),
        const SizedBox(height: 8),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            OutlinedButton.icon(
              key: const ValueKey('time_sync_refresh'),
              onPressed: () => ref.invalidate(timeSyncStatusProvider),
              icon: const Icon(Icons.refresh, size: 16),
              label: const Text('Retry'),
            ),
            OutlinedButton.icon(
              key: const ValueKey('time_sync_push_device'),
              onPressed: () => _pushDeviceTime(context, ref),
              icon: const Icon(Icons.schedule_send, size: 16),
              label: const Text('Push device time'),
            ),
            TextButton.icon(
              key: const ValueKey('time_sync_manual_open'),
              onPressed: () => showTimeSyncManualModal(context, ref),
              icon: const Icon(Icons.edit_calendar, size: 16),
              label: const Text('Set manually…'),
            ),
          ],
        ),
      ],
    );
  }

  List<Widget> _statusRows(BuildContext context, TimeSyncState s) {
    final theme = Theme.of(context);
    final loc = s.location;
    return [
      SettingsRow(
        label: 'Server clock',
        value: s.synced
            ? 'Synced (${s.source}, trust ${s.trust})'
            : 'Not synced',
      ),
      SettingsRow(
        label: 'Clock offset',
        value: '${s.systemTimeOffsetSeconds.toStringAsFixed(1)} s',
      ),
      SettingsRow(
        label: 'Known position',
        value: loc == null
            ? 'none'
            : '${loc.lat.toStringAsFixed(4)}°, ${loc.lng.toStringAsFixed(4)}°'
                  '${loc.alt == null ? '' : ', ${loc.alt!.toStringAsFixed(0)} m'}',
      ),
      SettingsRow(
        label: 'USB GPS on server',
        value: s.internalGpsAvailable ? 'detected' : 'not detected',
      ),
      if (s.syncedAtUtc != null)
        SettingsRow(
          label: 'Last sync (UTC)',
          value: s.syncedAtUtc!.toIso8601String().substring(0, 19),
        ),
      if (!s.synced)
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Text(
            'The server has no trustworthy time. Plug a USB GPS into the Pi '
            'and Retry, push this device’s clock, or set the time '
            'manually below.',
            style: theme.textTheme.bodySmall!.copyWith(
              color: theme.colorScheme.error,
            ),
          ),
        ),
    ];
  }

  Future<void> _pushDeviceTime(BuildContext context, WidgetRef ref) async {
    final api = ref.read(timeSyncApiProvider);
    final messenger = ScaffoldMessenger.of(context);
    if (api == null) {
      messenger.showSnackBar(
        const SnackBar(content: Text('No active server.')),
      );
      return;
    }
    try {
      await api.pushClientTime(DateTime.now().toUtc());
      messenger.showSnackBar(
        const SnackBar(content: Text('Device clock pushed to the server.')),
      );
    } catch (e) {
      messenger.showSnackBar(SnackBar(content: Text('Time push failed: $e')));
    }
    ref.invalidate(timeSyncStatusProvider);
  }
}

/// §31.1 step 5 — the manual entry modal: UTC date/time (pre-filled from this
/// device) + optional lat/lng/altitude, pushed as a low-trust `manual` sync.
Future<void> showTimeSyncManualModal(BuildContext context, WidgetRef ref) {
  final api = ref.read(timeSyncApiProvider);
  return showDialog<void>(
    context: context,
    builder: (dialogContext) => _TimeSyncManualDialog(
      api: api,
      onApplied: () => ref.invalidate(timeSyncStatusProvider),
    ),
  );
}

class _TimeSyncManualDialog extends StatefulWidget {
  final TimeSyncClient? api;
  final VoidCallback onApplied;

  const _TimeSyncManualDialog({required this.api, required this.onApplied});

  @override
  State<_TimeSyncManualDialog> createState() => _TimeSyncManualDialogState();
}

class _TimeSyncManualDialogState extends State<_TimeSyncManualDialog> {
  late final TextEditingController _time;
  final _lat = TextEditingController();
  final _lng = TextEditingController();
  final _alt = TextEditingController();
  String? _error;
  bool _applying = false;

  @override
  void initState() {
    super.initState();
    final now = DateTime.now().toUtc();
    _time = TextEditingController(
      text: now.toIso8601String().substring(0, 19),
    );
  }

  @override
  void dispose() {
    _time.dispose();
    _lat.dispose();
    _lng.dispose();
    _alt.dispose();
    super.dispose();
  }

  Future<void> _apply() async {
    final api = widget.api;
    if (api == null) {
      setState(() => _error = 'No active server.');
      return;
    }
    final parsed = DateTime.tryParse('${_time.text.trim()}Z');
    if (parsed == null) {
      setState(
        () => _error = 'Time must be UTC in the form 2026-07-11T04:30:00.',
      );
      return;
    }
    final lat = double.tryParse(_lat.text.trim());
    final lng = double.tryParse(_lng.text.trim());
    final alt = double.tryParse(_alt.text.trim());
    final latGiven = _lat.text.trim().isNotEmpty;
    final lngGiven = _lng.text.trim().isNotEmpty;
    if (latGiven != lngGiven) {
      setState(
        () => _error = 'Latitude and longitude go together — fill both '
            'or leave both empty.',
      );
      return;
    }
    if (latGiven && (lat == null || lng == null)) {
      setState(() => _error = 'Latitude/longitude must be decimal degrees.');
      return;
    }
    setState(() {
      _applying = true;
      _error = null;
    });
    final navigator = Navigator.of(context);
    final messenger = ScaffoldMessenger.of(context);
    try {
      final result = await api.pushManual(
        timeUtc: parsed,
        lat: lat,
        lng: lng,
        alt: latGiven ? alt : null,
      );
      // The barrier is tappable, so the dialog can be dismissed mid-push;
      // popping via the pre-await NavigatorState would then close whatever
      // route is behind it (r1).
      if (!mounted) return;
      widget.onApplied();
      navigator.pop();
      final clockMsg = result.clockSet
          ? 'Server clock set (manual, low trust).'
          : 'Time recorded, but the server could not set its system '
                'clock — the offset is being tracked instead.';
      final locationMsg = latGiven && !result.locationUpdated
          ? ' The position was NOT applied — check the site settings.'
          : '';
      messenger.showSnackBar(SnackBar(content: Text('$clockMsg$locationMsg')));
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _applying = false;
        _error = 'Push failed: $e';
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return AlertDialog(
      title: const Text('Set time manually'),
      content: SizedBox(
        width: 380,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'Last-resort sync (§31): the entered time is applied at low '
              'trust — schedule-based instructions will warn before running.',
              style: theme.textTheme.bodySmall,
            ),
            const SizedBox(height: 12),
            TextField(
              key: const ValueKey('time_sync_manual_time'),
              controller: _time,
              decoration: const InputDecoration(
                labelText: 'UTC date & time',
                hintText: '2026-07-11T04:30:00',
              ),
            ),
            const SizedBox(height: 8),
            Row(
              children: [
                Expanded(
                  child: TextField(
                    key: const ValueKey('time_sync_manual_lat'),
                    controller: _lat,
                    decoration: const InputDecoration(
                      labelText: 'Latitude (°)',
                      hintText: 'optional',
                    ),
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: TextField(
                    key: const ValueKey('time_sync_manual_lng'),
                    controller: _lng,
                    decoration: const InputDecoration(
                      labelText: 'Longitude (°)',
                      hintText: 'optional',
                    ),
                  ),
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: TextField(
                    key: const ValueKey('time_sync_manual_alt'),
                    controller: _alt,
                    decoration: const InputDecoration(
                      labelText: 'Alt (m)',
                      hintText: 'optional',
                    ),
                  ),
                ),
              ],
            ),
            if (_error != null) ...[
              const SizedBox(height: 8),
              Text(
                _error!,
                style: TextStyle(color: theme.colorScheme.error),
              ),
            ],
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: _applying ? null : () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          key: const ValueKey('time_sync_manual_apply'),
          onPressed: _applying ? null : _apply,
          child: Text(_applying ? 'Applying…' : 'Apply'),
        ),
      ],
    );
  }
}
