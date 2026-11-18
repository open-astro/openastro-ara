import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/alpacabridge_form.dart';
import '../../models/alpacabridge_setup.dart';
import '../../state/equipment/alpacabridge_setup_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/friendly_error.dart';
import '../adaptive_controls.dart';
import 'alpacabridge_driver_form.dart';

/// #1580 — add-your-devices: what the bridge found, ticked rows, Add selected, a form for a
/// row that needs details, and Add a device by hand. One panel for the setup wizard and Simple
/// Mode (ADR 0009); every rule lives in [foundDevicesProvider]. Hidden when there is no AlpacaBridge catalog.
///
/// [showRetry]: the panel draws its own Retry detection (the wizard has one of its own).
class AlpacaBridgeSetupPanel extends ConsumerStatefulWidget {
  const AlpacaBridgeSetupPanel({super.key, this.showRetry = true});
  final bool showRetry;

  @override
  ConsumerState<AlpacaBridgeSetupPanel> createState() => _PanelState();
}

enum _Hand { list, pick, form }

// Fields the pick (or the detection) already fills.
const _detected = {'serialNumber', 'cameraName'};

class _PanelState extends ConsumerState<AlpacaBridgeSetupPanel> {
  // The needs-details walk: row ids, the one shown, and whether the last Continue sends.
  List<String>? _walk;
  var _at = 0;
  var _sendAfter = true;

  _Hand? _hand;
  BridgeDriver? _driver;
  List<AttachedCamera> _cams = const [];
  AttachedCamera? _picked;
  String? _handResult;

  FoundDevicesNotifier get _n => ref.read(foundDevicesProvider.notifier);

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) _n.panelOpened();
    });
  }

  Future<void> _addSelected(List<FoundRow> rows) async {
    final need = [
      for (final r in rows)
        if (r.ticked && r.status == FoundStatus.needsDetails) r.id,
    ];
    if (need.isEmpty) return _n.addSelected();
    setState(() {
      _walk = need;
      _at = 0;
      _sendAfter = true;
    });
  }

  void _edit(String id) => setState(() {
    _walk = [id];
    _at = 0;
    _sendAfter = false;
  });

  void _submitRow(
    FoundRow row,
    Map<String, Object?> values,
    Map<String, String> secrets,
  ) {
    _n.setDetails(row.id, values, secrets: secrets);
    final walk = _walk!;
    if (_at + 1 < walk.length) {
      setState(() => _at++);
      return;
    }
    setState(() => _walk = null);
    if (_sendAfter) _n.addSelected();
  }

  void _leaveHand() => setState(() {
    _hand = null;
    _driver = null;
    _picked = null;
  });

  Future<void> _chooseDriver(BridgeDriver d) async {
    final cams = d.deviceType == 'camera'
        ? await _n.camerasOf(d.vendor)
        : const <AttachedCamera>[];
    if (!mounted) return;
    setState(() {
      _driver = d;
      _cams = cams;
      _picked = null;
      _hand = cams.isEmpty ? _Hand.form : _Hand.pick;
    });
  }

  Future<void> _submitHand(
    Map<String, Object?> values,
    Map<String, String> secrets,
  ) async {
    final d = _driver!;
    final body = {
      ...BridgeForm(d).body(values),
      if (_picked != null) 'serialNumber': _picked!.serial,
      if (_picked != null) 'cameraName': _picked!.name,
      ...secrets,
    };
    final out = await _n.addByHand(body);
    if (!mounted || out == null) return;
    setState(() {
      _handResult = switch (out) {
        AddAdded() => context.l10n.bridgeSetupAdded,
        AddRefused(:final detail) => _refusal(detail),
        AddUnconfirmed() => context.l10n.bridgeSetupMayExist,
      };
      _hand = null;
      _driver = null;
      _picked = null;
    });
  }

  // The bridge's own words, else ours (a 4xx with no detail).
  String _refusal(String detail) =>
      detail.isEmpty ? context.l10n.bridgeSetupRefused : detail;

  @override
  Widget build(BuildContext context) {
    final l = context.l10n;
    final found = ref.watch(foundDevicesProvider);
    final rows = found.value;
    if (found.hasError) {
      return _Block(
        key: const Key('found-error'),
        children: [
          Text(
            friendlyError(l, found.error!, action: l.actionLookForEquipment),
          ),
          if (widget.showRetry) _retry(),
        ],
      );
    }
    if (rows == null) {
      return Row(
        key: const Key('found-loading'),
        spacing: 12,
        children: [
          const SizedBox(
            width: 16,
            height: 16,
            child: AdaptiveProgressIndicator(strokeWidth: 2),
          ),
          Expanded(child: Text(l.bridgeSetupLooking)),
        ],
      );
    }
    // No catalog (not an AlpacaBridge, or too old): today's screen.
    if (_n.drivers.isEmpty) return const SizedBox.shrink();

    final walk = _walk;
    if (walk != null && _at < walk.length) {
      final row = rows.where((r) => r.id == walk[_at]).firstOrNull;
      if (row != null) return _walkView(row);
    }
    if (_hand == _Hand.list) return _handList();
    if (_hand == _Hand.pick) return _handPick();
    if (_hand == _Hand.form) return _handForm();

    final running = isRunning(rows);
    final busy = ref.watch(foundDevicesBusyProvider);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: 8,
      children: [
        if (rows.isEmpty)
          _Block(
            key: const Key('found-none'),
            children: [
              Text(l.bridgeSetupNothingFound),
              if (widget.showRetry) _retry(),
            ],
          )
        else ...[
          Text(
            l.bridgeSetupFoundTitle,
            style: Theme.of(context).textTheme.titleSmall,
          ),
          for (final r in rows) _rowView(r, busy),
          Align(
            alignment: Alignment.centerLeft,
            child: FilledButton(
              key: const Key('add-selected'),
              style: FilledButton.styleFrom(minimumSize: const Size(0, 48)),
              onPressed: busy || selectedCount(rows) == 0
                  ? null
                  : () => _addSelected(rows),
              child: Text(
                running
                    ? l.bridgeSetupAdding
                    : l.bridgeSetupAddSelected(selectedCount(rows)),
              ),
            ),
          ),
        ],
        if (_handResult != null)
          Text(_handResult!, key: const Key('by-hand-result')),
        Align(
          alignment: Alignment.centerLeft,
          child: OutlinedButton(
            key: const Key('add-by-hand'),
            style: OutlinedButton.styleFrom(minimumSize: const Size(0, 48)),
            onPressed: busy
                ? null
                : () => setState(() {
                    _handResult = null;
                    _hand = _Hand.list;
                  }),
            child: Text(l.bridgeSetupByHand),
          ),
        ),
        if (rows.isNotEmpty && widget.showRetry)
          Align(alignment: Alignment.centerLeft, child: _retry()),
      ],
    );
  }

  Widget _retry() {
    final busy = ref.watch(foundDevicesBusyProvider);
    return TextButton(
      key: const Key('retry-detection'),
      style: TextButton.styleFrom(minimumSize: const Size(0, 48)),
      onPressed: busy ? null : () => _n.redetect(),
      child: Text(context.l10n.wizardDiscoveryRetryDetection),
    );
  }

  Widget _rowView(FoundRow r, bool busy) {
    final l = context.l10n;
    final canTick =
        !busy &&
        (r.status == FoundStatus.found || r.status == FoundStatus.needsDetails);
    final status = switch (r.status) {
      FoundStatus.found => null,
      FoundStatus.needsDetails => l.bridgeSetupNeedsDetails,
      FoundStatus.waiting => l.bridgeSetupWaiting,
      FoundStatus.adding => l.bridgeSetupAdding,
      FoundStatus.added => l.bridgeSetupAdded,
      FoundStatus.failed => l.bridgeSetupFailed,
    };
    final failed = r.status == FoundStatus.failed;
    return InkWell(
      key: Key('found-row-${r.id}'),
      onTap: canTick ? () => _n.toggle(r.id) : null,
      child: ConstrainedBox(
        constraints: const BoxConstraints(minHeight: 48),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          spacing: 8,
          children: [
            // One screen-reader node: the tick with the device's name (Retry and Edit stay apart).
            Expanded(
              child: MergeSemantics(
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  spacing: 8,
                  children: [
                    Checkbox(
                      value: r.ticked,
                      onChanged: canTick ? (_) => _n.toggle(r.id) : null,
                    ),
                    Expanded(
                      child: Padding(
                        padding: const EdgeInsets.symmetric(vertical: 12),
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Text(
                              r.name,
                              style: failed
                                  ? const TextStyle(
                                      color: AraColors.textDisabled,
                                    )
                                  : null,
                            ),
                            if (r.serial.isNotEmpty)
                              Text(
                                r.serial,
                                style: Theme.of(context).textTheme.bodySmall
                                    ?.copyWith(color: AraColors.textSecondary),
                              ),
                            if (r.error != null)
                              Text(
                                _refusal(r.error!),
                                style: const TextStyle(
                                  color: AraColors.accentError,
                                ),
                              ),
                            if (r.unconfirmed) Text(l.bridgeSetupMayExist),
                            if (r.warning != null)
                              Text(
                                r.warning!,
                                style: const TextStyle(
                                  color: AraColors.accentWarning,
                                ),
                              ),
                          ],
                        ),
                      ),
                    ),
                  ],
                ),
              ),
            ),
            if (status != null)
              Padding(
                key: Key('found-status-${r.id}'),
                padding: const EdgeInsets.symmetric(vertical: 12),
                child: Text(status),
              ),
            if (failed)
              TextButton(
                key: Key('row-retry-${r.id}'),
                style: TextButton.styleFrom(minimumSize: const Size(0, 48)),
                onPressed: busy ? null : () => _n.retry(r.id),
                child: Text(l.bridgeSetupRetry),
              ),
            if (failed || r.status == FoundStatus.needsDetails)
              TextButton(
                key: Key('row-edit-${r.id}'),
                style: TextButton.styleFrom(minimumSize: const Size(0, 48)),
                onPressed: busy ? null : () => _edit(r.id),
                child: Text(l.bridgeSetupEdit),
              ),
          ],
        ),
      ),
    );
  }

  Widget _walkView(FoundRow row) {
    return _Block(
      children: [
        Text(row.name, style: Theme.of(context).textTheme.titleSmall),
        AlpacaBridgeDriverForm(
          key: ValueKey(row.id),
          driver: _n.driverOf(row),
          hide: _detected,
          initialValues: row.values,
          busy: ref.watch(foundDevicesBusyProvider),
          onSubmit: (v, s) => _submitRow(row, v, s),
        ),
        _back(() => setState(() => _walk = null)),
      ],
    );
  }

  Widget _back(VoidCallback onTap) => Align(
    alignment: Alignment.centerLeft,
    child: TextButton(
      key: const Key('form-back'),
      style: TextButton.styleFrom(minimumSize: const Size(0, 48)),
      onPressed: onTap,
      child: Text(context.l10n.bridgeSetupBack),
    ),
  );

  Widget _handList() {
    return _Block(
      children: [
        Column(
          key: const Key('by-hand-list'),
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            for (final d in _n.drivers.where((d) => d.available))
              ListTile(
                key: Key('by-hand-driver-${d.key}'),
                minTileHeight: 48,
                title: Text(d.displayName),
                subtitle: Text(d.vendorLabel),
                onTap: () => _chooseDriver(d),
              ),
          ],
        ),
        _back(_leaveHand),
      ],
    );
  }

  Widget _handPick() {
    final l = context.l10n;
    return _Block(
      children: [
        Text(
          l.bridgeSetupWhichCamera,
          style: Theme.of(context).textTheme.titleSmall,
        ),
        for (final c in _cams)
          ListTile(
            key: Key(
              'by-hand-camera-${c.serial.isEmpty ? '#${c.index}' : c.serial}',
            ),
            minTileHeight: 48,
            title: Text(c.name),
            subtitle: c.serial.isEmpty ? null : Text(c.serial),
            onTap: () => setState(() {
              _picked = c;
              _hand = _Hand.form;
            }),
          ),
        _back(_leaveHand),
      ],
    );
  }

  Widget _handForm() {
    final l = context.l10n;
    final d = _driver!;
    return _Block(
      children: [
        Text(d.displayName, style: Theme.of(context).textTheme.titleSmall),
        AlpacaBridgeDriverForm(
          key: ValueKey(d.key),
          driver: d,
          hide: _picked == null ? const {} : _detected,
          submitLabel: l.bridgeSetupAdd,
          busy: ref.watch(foundDevicesBusyProvider),
          onSubmit: _submitHand,
        ),
        _back(_leaveHand),
      ],
    );
  }
}

class _Block extends StatelessWidget {
  const _Block({super.key, required this.children});
  final List<Widget> children;

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.stretch,
    spacing: 8,
    children: children,
  );
}
