import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../models/equipment_device_status.dart';
import '../../../models/focuser_status.dart';
import '../../../services/autofocus_api.dart';
import '../../../services/equipment_device_api.dart';
import '../../../state/equipment/focuser_state.dart';
import '../../../state/settings/equipment_connection_state.dart';
import '../../../state/settings/settings_nav.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/equipment/equipment_connection_card.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// §37.4 Focuser panel. Shows the connected focuser's live position / temperature
/// and a move control via the shared connection card. The §37.11 autofocus
/// settings live in their own editable panel (Imaging → Autofocus); this links there.
class EquipmentFocuserPanel extends ConsumerWidget {
  const EquipmentFocuserPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connection = ref.watch(equipmentConnectionProvider);
    final n = ref.read(equipmentConnectionProvider.notifier);
    final status = ref.watch(focuserProvider);
    final notifier = ref.read(focuserProvider.notifier);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Connection'),
        EquipmentConnectionCard<FocuserStatus>(
          status: status,
          deviceType: EquipmentDeviceType.focuser,
          deviceTypeLabel: 'focuser',
          emptyLabel: 'No focuser connected.',
          onConnect: notifier.connect,
          onDisconnect: notifier.disconnect,
          onReconnect: notifier.reconnect,
          onRetry: notifier.refresh,
          connectedBody: (context, s) => _FocuserBody(status: s),
        ),
        SettingsSwitchRow(
          label: 'Auto-connect on boot',
          helpKey: 'eq.auto_connect_on_boot',
          value: connection.autoConnect(EquipmentDeviceType.focuser),
          onChanged: (v) => n.setAutoConnect(EquipmentDeviceType.focuser, v),
        ),
        const SettingsSectionHeader('Autofocus'),
        // §59 — trigger one V-curve sweep with the profile's autofocus settings.
        // Runs as a daemon background job; the row polls it to a terminal state.
        // Enabled only while a focuser is connected (the sweep also needs the
        // camera, but the daemon fail-louds that — the job row shows the reason).
        _RunAutofocusRow(
          focuserConnected: status.asData?.value?.isConnected ?? false,
        ),
        // The editable autofocus settings (temp compensation, AF-after-filter-change,
        // trigger temp delta — saved per profile) live in their own panel; link there
        // rather than duplicate them here.
        Align(
          alignment: Alignment.centerLeft,
          child: TextButton.icon(
            onPressed: () => ref
                .read(selectedSettingsPanelProvider.notifier)
                .select('img.autofocus'),
            icon: const Icon(Icons.tune, size: 16),
            label: const Text('Autofocus settings (Imaging → Autofocus)'),
          ),
        ),
      ],
    );
  }
}

/// The connected focuser's live body: position + temperature + temp-comp state,
/// and a move-to-target control. A ConsumerStatefulWidget so the target field has
/// a controller and can dispatch the move.
class _FocuserBody extends ConsumerStatefulWidget {
  final FocuserStatus status;
  const _FocuserBody({required this.status});

  @override
  ConsumerState<_FocuserBody> createState() => _FocuserBodyState();
}

class _FocuserBodyState extends ConsumerState<_FocuserBody> {
  // Seeded once from the current position. Intentionally NOT reseeded on live
  // updates — this is the user's target, not a live value (the live position is
  // shown separately), so a background poll can't clobber what they're typing.
  late final TextEditingController _target =
      TextEditingController(text: widget.status.position?.toString() ?? '');

  // Whether this move should enable the device's temperature compensation. Seeded
  // from the current state; the user toggles it per move (only shown when the
  // device supports temp-comp — it's the only way the daemon exposes setting it).
  late bool _useTempComp = widget.status.tempCompEnabled;

  @override
  void dispose() {
    _target.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final s = widget.status;
    if (s.isConnecting) return const Text('Reading…');
    if (s.connectionState == EquipmentConnectionState.error) {
      return const Row(children: [
        Icon(Icons.error_outline, color: AraColors.accentError, size: 20),
        SizedBox(width: 8),
        Expanded(child: Text('Focuser read failed — check the device.')),
      ]);
    }
    final caps = s.capabilities;
    // Absolute focusers take a destination (0..max, digits only); relative focusers
    // take a signed step delta (negative = inward), so allow a leading '-' and
    // don't clamp to the absolute range.
    final absolute = caps?.absoluteFocuser ?? true;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _row('Position', s.position?.toString() ?? '—'),
        if (s.temperature != null)
          _row('Temperature', '${s.temperature!.toStringAsFixed(1)} °C'),
        _row('Temp. compensation', s.tempCompEnabled ? 'On' : 'Off'),
        if (s.isMoving)
          const Padding(
            padding: EdgeInsets.symmetric(vertical: 4),
            child: Text('Moving…', style: TextStyle(color: AraColors.accentBusy)),
          ),
        const SizedBox(height: 8),
        Row(
          children: [
            SizedBox(
              width: 140,
              child: TextField(
                controller: _target,
                keyboardType: TextInputType.number,
                inputFormatters: [
                  // Anchored so a dash is only ever a leading sign (no '50-00').
                  FilteringTextInputFormatter.allow(
                      absolute ? RegExp(r'^[0-9]*$') : RegExp(r'^-?[0-9]*$')),
                ],
                decoration: InputDecoration(
                  isDense: true,
                  labelText: absolute ? 'Target' : 'Steps (±)',
                  helperText: absolute
                      ? (caps != null && caps.maxPosition > caps.minPosition
                          ? 'Range ${caps.minPosition}–${caps.maxPosition}'
                          : null)
                      : 'Relative move (− inward)',
                ),
              ),
            ),
            const SizedBox(width: 12),
            FilledButton(
              onPressed: s.isMoving ? null : () => _move(caps, absolute),
              child: const Text('Move'),
            ),
          ],
        ),
        if (caps?.canTempComp ?? false)
          Padding(
            padding: const EdgeInsets.only(top: 4),
            child: Row(children: [
              Checkbox(
                value: _useTempComp,
                onChanged: (v) => setState(() => _useTempComp = v ?? false),
              ),
              const Text('Use temperature compensation for this move'),
            ]),
          ),
      ],
    );
  }

  Widget _row(String label, String value) => Padding(
        padding: const EdgeInsets.symmetric(vertical: 2),
        child: Row(children: [
          Expanded(child: Text(label)),
          Text(value),
        ]),
      );

  Future<void> _move(FocuserCapabilities? caps, bool absolute) async {
    final messenger = ScaffoldMessenger.of(context);
    final raw = int.tryParse(_target.text.trim());
    if (raw == null) {
      messenger.showSnackBar(
          const SnackBar(content: Text('Enter a target position.')));
      return;
    }
    // Clamp an absolute target to the device range (a typo can't drive past
    // limits) — but only when the range is actually known (max > min); a missing
    // max_position must not collapse the clamp to [0,0] and command a move to 0.
    // A relative step delta is sent as-is. Reflect the value actually sent back
    // into the field so it doesn't read a stale out-of-range number.
    final target = (absolute && caps != null && caps.maxPosition > caps.minPosition)
        ? raw.clamp(caps.minPosition, caps.maxPosition)
        : raw;
    if (mounted && target != raw) _target.text = target.toString();
    try {
      final performed = await ref
          .read(focuserProvider.notifier)
          .move(target, useTempComp: _useTempComp);
      if (!performed) {
        messenger.showSnackBar(const SnackBar(
          content: Text('Another action is still in progress.'),
        ));
      }
    } catch (e) {
      messenger.showSnackBar(SnackBar(
        content: Text("Couldn't move: ${describeEquipmentError(e)}"),
        backgroundColor: AraColors.accentError,
      ));
    }
  }
}


/// §59 — the "Run autofocus" control: starts the daemon's V-curve sweep as a
/// background job and polls it to a terminal state, surfacing progress and the
/// failure reason inline. A duplicate tap while a sweep runs joins the running
/// job (daemon single-job-per-type), so the button simply disables while busy.
class _RunAutofocusRow extends ConsumerStatefulWidget {
  final bool focuserConnected;
  const _RunAutofocusRow({required this.focuserConnected});

  @override
  ConsumerState<_RunAutofocusRow> createState() => _RunAutofocusRowState();
}

class _RunAutofocusRowState extends ConsumerState<_RunAutofocusRow> {
  bool _running = false;
  String? _result; // last terminal outcome, shown under the button
  bool _lastFailed = false;
  String _runningLabel = 'Autofocusing…';

  Future<void> _run() async {
    final api = ref.read(autofocusApiProvider);
    if (api == null) return;
    setState(() {
      _running = true;
      _result = null;
      _lastFailed = false;
      _runningLabel = 'Autofocusing…';
    });
    try {
      final started = await api.start();
      var job = started;
      // Poll to terminal. A sweep is minutes of moves + probe exposures; 2s is
      // plenty responsive without hammering the daemon. Stop polling if this
      // panel is disposed mid-sweep — the daemon job keeps running regardless.
      //
      // Transient poll errors must NOT end the tracking (matching the device
      // liveness-poll convention): the daemon job keeps running through a
      // dropped request, and declaring failure on one blip would mislead the
      // user about a sweep that may complete moments later. Only a sustained
      // outage (~30s of consecutive failures) gives up — on the TRACKING, with
      // a message that says exactly that.
      var consecutivePollFailures = 0;
      const maxConsecutivePollFailures = 15;
      while (!job.isTerminal) {
        await Future<void>.delayed(const Duration(seconds: 2));
        if (!mounted) return;
        AutofocusJob? polled;
        try {
          polled = await api.job(job.jobId);
          consecutivePollFailures = 0;
        } catch (e) {
          if (++consecutivePollFailures >= maxConsecutivePollFailures) {
            if (!mounted) return;
            setState(() {
              _running = false;
              _lastFailed = true;
              _result =
                  'Lost contact with the server while autofocusing — the sweep may still be running on the daemon; check the Focuser panel or the daemon log.';
            });
            debugPrint('[autofocus] poll gave up after $consecutivePollFailures consecutive failures: $e');
            return;
          }
          continue; // transient blip — keep tracking
        }
        // Live probe count from the daemon's per-probe job ticks (3/9 …).
        if (polled != null && polled.total > 0 && mounted) {
          setState(() =>
              _runningLabel = 'Autofocusing… (${polled!.done}/${polled.total})');
        }
        if (polled == null) {
          // The daemon no longer knows the job. Its in-memory store never
          // evicts, so this means the daemon LOST STATE mid-sweep (a restart)
          // — the sweep was never confirmed terminal and the focuser may sit
          // at an arbitrary probe position. Say so; never report "finished".
          if (!mounted) return;
          setState(() {
            _running = false;
            _lastFailed = true;
            _result =
                'Lost track of the sweep — the server may have restarted mid-autofocus. Check the focuser position before imaging.';
          });
          return;
        }
        job = polled;
      }
      if (!mounted) return;
      setState(() {
        _running = false;
        _lastFailed = job.state == 'failed';
        _result = switch (job.state) {
          'complete' => 'Autofocus complete — focuser is at the fitted best position.',
          'failed' => job.errorMessage ?? 'Autofocus failed — see the daemon log.',
          'cancelled' => 'Autofocus was cancelled.',
          _ => 'Autofocus finished.',
        };
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _running = false;
        _lastFailed = true;
        _result = 'Could not run autofocus: check the connection and try again.';
      });
      debugPrint('[autofocus] run failed: $e');
    }
  }

  @override
  Widget build(BuildContext context) {
    final canRun =
        widget.focuserConnected && !_running && ref.watch(autofocusApiProvider) != null;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Align(
          alignment: Alignment.centerLeft,
          child: FilledButton.tonalIcon(
            onPressed: canRun ? _run : null,
            icon: _running
                ? const SizedBox(
                    width: 14,
                    height: 14,
                    child: CircularProgressIndicator(strokeWidth: 2))
                : const Icon(Icons.center_focus_strong, size: 16),
            label: Text(_running ? _runningLabel : 'Run autofocus'),
          ),
        ),
        if (!widget.focuserConnected)
          const Padding(
            padding: EdgeInsets.only(top: 6),
            child: Text('Connect a focuser to run autofocus.',
                style: TextStyle(fontSize: 12, color: AraColors.textSecondary)),
          ),
        if (_result != null)
          Padding(
            padding: const EdgeInsets.only(top: 6),
            child: Text(
              _result!,
              style: TextStyle(
                  fontSize: 12,
                  color: _lastFailed ? AraColors.accentError : AraColors.textSecondary),
            ),
          ),
      ],
    );
  }
}
