import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/custom_targets_service.dart';
import '../../state/sequencer/create_imaging_run.dart';
import '../../state/sky_atlas/custom_targets_state.dart';
import '../../state/sky_atlas/sky_atlas_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/apparent_place.dart';
import '../../util/coord_format.dart';
import '../../util/coord_parse.dart';

/// Plan screen → "Target by coordinates" (#1267 item 1, plus the epoch
/// choice). Type or paste an RA/Dec from SIMBAD, Gaia, a planetarium or a
/// hand controller, say whether it is J2000 or JNow, name it, and either
/// frame it on the sky or add it to the plan. "Add to plan" builds the same
/// imaging run a Tonight's Sky row does ([createImagingRun]: cool, unpark,
/// slew, autofocus, exposures), so the run slews to the position; the daemon
/// precesses the stored J2000 to the mount's own equatorial system at slew
/// time (#1124), exactly as for a catalogue target.
///
/// A JNow entry is converted to J2000 here, client-side ([apparentToJ2000]),
/// because every stored coordinate in Ara is J2000 and planning compute lives
/// in the client (offline-first). The preview line shows both so the user can
/// check the conversion against where they copied the numbers from.
///
/// Recent targets persist on this device ([customTargetsProvider]) so a
/// position prepared at home is one tap away at the dark site.
class CustomTargetDialog extends ConsumerStatefulWidget {
  /// Text to pre-fill from the search box when it looked like coordinates.
  final String? initialText;

  const CustomTargetDialog({super.key, this.initialText});

  @override
  ConsumerState<CustomTargetDialog> createState() => _CustomTargetDialogState();
}

enum _Epoch { j2000, jnow }

class _CustomTargetDialogState extends ConsumerState<CustomTargetDialog> {
  final _name = TextEditingController();
  final _ra = TextEditingController();
  final _dec = TextEditingController();
  _Epoch _epoch = _Epoch.j2000;
  RaUnit _raUnit = RaUnit.degrees;
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    final t = widget.initialText?.trim();
    if (t != null && t.isNotEmpty) _fillFromLine(t);
    _ra.addListener(_onRaChanged);
  }

  @override
  void dispose() {
    _ra.removeListener(_onRaChanged);
    _name.dispose();
    _ra.dispose();
    _dec.dispose();
    super.dispose();
  }

  String _lastRa = '';

  /// A whole "RA Dec" line pasted into the RA box splits across both fields.
  /// Only a PASTE (the text grew by more than one character at once) or
  /// Enter triggers the split: typing "02 37 31.5 +71 18 16" key by key
  /// passes through "02 3", which also reads as a pair, and splitting there
  /// would strand the rest of the keystrokes in the wrong field.
  void _onRaChanged() {
    final text = _ra.text;
    final pasted = text.length - _lastRa.length > 1;
    _lastRa = text;
    if (pasted && _dec.text.trim().isEmpty && parseRaDec(text) != null) {
      _fillFromLine(text);
      return;
    }
    setState(() {});
  }

  /// Enter in the RA box: split a typed pair, else move on to Dec.
  void _onRaSubmitted() {
    if (_dec.text.trim().isEmpty && parseRaDec(_ra.text) != null) {
      _fillFromLine(_ra.text);
    }
  }

  void _fillFromLine(String line) {
    final halves = splitRaDec(line);
    if (halves == null) return;
    _ra.removeListener(_onRaChanged);
    _ra.text = halves.$1;
    _lastRa = _ra.text;
    _dec.text = halves.$2;
    _ra.addListener(_onRaChanged);
    if (mounted) setState(() {});
  }

  void _fillFromRecent(CustomTarget t) {
    _ra.removeListener(_onRaChanged);
    _name.text = t.name;
    _ra.text = formatRaHms(t.raDeg / 15);
    _lastRa = _ra.text;
    _dec.text = formatDecDms(t.decDeg);
    _ra.addListener(_onRaChanged);
    setState(() => _epoch = _Epoch.j2000);
  }

  /// The typed position as entered (in the chosen epoch), or null.
  ParsedCoordinates? get _typed {
    final ra = parseRa(_ra.text, decimalUnit: _raUnit);
    final dec = parseDec(_dec.text);
    if (ra == null || dec == null) return null;
    return ParsedCoordinates(ra, dec);
  }

  /// The J2000 position the run will carry.
  ParsedCoordinates? get _j2000 {
    final t = _typed;
    if (t == null) return null;
    if (_epoch == _Epoch.j2000) return t;
    final c = apparentToJ2000(t.raDeg, t.decDeg, atUtc: DateTime.now().toUtc());
    return ParsedCoordinates(c.raDeg, c.decDeg);
  }

  String get _targetName {
    final n = _name.text.trim();
    if (n.isNotEmpty) return n;
    final c = _j2000;
    if (c == null) return 'Custom target';
    return '${formatRaHms(c.raDeg / 15)} ${formatDecDms(c.decDeg)}';
  }

  CustomTarget _asCustomTarget(ParsedCoordinates c) => CustomTarget(
        name: _targetName,
        raDeg: c.raDeg,
        decDeg: c.decDeg,
        typedAsJNow: _epoch == _Epoch.jnow,
        savedUtc: DateTime.now().toUtc(),
      );

  Future<void> _showOnSky() async {
    final c = _j2000;
    if (c == null) return;
    final name = _targetName;
    ref.read(planetariumCommandProvider.notifier).send({
      'type': 'goto',
      'ra': c.raDeg,
      'dec': c.decDeg,
      'name': name,
      'frame': true,
    });
    final remember =
        ref.read(customTargetsProvider.notifier).remember(_asCustomTarget(c));
    Navigator.of(context).pop();
    await remember;
  }

  Future<void> _addToPlan() async {
    final c = _j2000;
    if (c == null || _busy) return;
    final name = _targetName;
    final messenger = ScaffoldMessenger.of(context);
    final recents = ref.read(customTargetsProvider.notifier);
    setState(() => _busy = true);
    ImagingRunResult? result;
    try {
      result = await createImagingRun(
        ref,
        raDeg: c.raDeg,
        decDeg: c.decDeg,
        targetName: name,
        jumpToRun: false,
      );
    } catch (e, st) {
      debugPrint('[custom-target] create-run failed: $e\n$st');
      if (mounted) {
        setState(() => _busy = false);
        showImagingRunFeedback(messenger, targetName: name, failed: true);
      }
      return;
    }
    if (!mounted) return;
    if (result?.cancelled ?? false) {
      setState(() => _busy = false);
      return;
    }
    final remember = recents.remember(_asCustomTarget(c));
    Navigator.of(context).pop();
    // The SnackBar, not the Tonight's Sky confirmation card: the dialog opens
    // from the search bar with the panel usually closed, and a card inside a
    // closed panel is no confirmation at all (seen on the first live run).
    showImagingRunFeedback(messenger, targetName: name, result: result);
    await remember;
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final typed = _typed;
    final j2000 = _j2000;
    final raOk = parseRa(_ra.text, decimalUnit: _raUnit) != null;
    final decOk = parseDec(_dec.text) != null;
    final recents = ref.watch(customTargetsProvider).value ?? const [];
    final hint = theme.textTheme.bodySmall?.copyWith(
      color: AraColors.textSecondary,
    );

    return AlertDialog(
      title: const Text('Target by coordinates'),
      content: SizedBox(
        width: 440,
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              TextField(
                controller: _name,
                textInputAction: TextInputAction.next,
                onChanged: (_) => setState(() {}),
                decoration: const InputDecoration(
                  isDense: true,
                  labelText: 'Name',
                  hintText: 'AB Cas, TOI-700 field, …',
                ),
              ),
              const SizedBox(height: 12),
              Row(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Expanded(
                    child: TextField(
                      key: const Key('custom-target-ra'),
                      controller: _ra,
                      autofocus: widget.initialText == null,
                      textInputAction: TextInputAction.next,
                      onSubmitted: (_) => _onRaSubmitted(),
                      decoration: InputDecoration(
                        isDense: true,
                        labelText: 'RA',
                        hintText: '05 35 17.3 · 5h35m17s · 83.822',
                        errorText: _ra.text.trim().isEmpty || raOk
                            ? null
                            : "Can't read this RA",
                      ),
                    ),
                  ),
                  const SizedBox(width: 8),
                  Expanded(
                    child: TextField(
                      key: const Key('custom-target-dec'),
                      controller: _dec,
                      textInputAction: TextInputAction.done,
                      onChanged: (_) => setState(() {}),
                      onSubmitted: (_) => _addToPlan(),
                      decoration: InputDecoration(
                        isDense: true,
                        labelText: 'Dec',
                        hintText: '-05 23 28 · -5.391',
                        errorText: _dec.text.trim().isEmpty || decOk
                            ? null
                            : "Can't read this Dec",
                      ),
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 12),
              Wrap(
                spacing: 12,
                runSpacing: 8,
                crossAxisAlignment: WrapCrossAlignment.center,
                children: [
                  Tooltip(
                    message: 'J2000: catalogues, SIMBAD, Gaia, plate solves. '
                        'JNow: a hand controller or planetarium readout for '
                        'tonight — converted to J2000 for the run.',
                    child: SegmentedButton<_Epoch>(
                      showSelectedIcon: false,
                      style: const ButtonStyle(
                        visualDensity: VisualDensity.compact,
                      ),
                      segments: const [
                        ButtonSegment(
                          value: _Epoch.j2000,
                          label: Text('J2000'),
                        ),
                        ButtonSegment(value: _Epoch.jnow, label: Text('JNow')),
                      ],
                      selected: {_epoch},
                      onSelectionChanged: (s) =>
                          setState(() => _epoch = s.first),
                    ),
                  ),
                  if (raUnitIsAmbiguous(_ra.text))
                    Tooltip(
                      message: 'A single decimal RA at or below 24 could be '
                          'hours or degrees — pick which.',
                      child: SegmentedButton<RaUnit>(
                        showSelectedIcon: false,
                        style: const ButtonStyle(
                          visualDensity: VisualDensity.compact,
                        ),
                        segments: const [
                          ButtonSegment(
                            value: RaUnit.degrees,
                            label: Text('RA in °'),
                          ),
                          ButtonSegment(
                            value: RaUnit.hours,
                            label: Text('RA in h'),
                          ),
                        ],
                        selected: {_raUnit},
                        onSelectionChanged: (s) =>
                            setState(() => _raUnit = s.first),
                      ),
                    ),
                ],
              ),
              const SizedBox(height: 12),
              // Live read-back: what the run will carry (J2000), and for a
              // JNow entry the typed position too, so a wrong epoch choice
              // is visible before anything is slewed.
              if (j2000 != null && typed != null) ...[
                Text(
                  'J2000  ${formatRaHms(j2000.raDeg / 15)}  '
                  '${formatDecDms(j2000.decDeg)}',
                  key: const Key('custom-target-preview'),
                  style: theme.textTheme.bodyMedium,
                ),
                if (_epoch == _Epoch.jnow)
                  Text(
                    'from JNow ${formatRaHms(typed.raDeg / 15)}  '
                    '${formatDecDms(typed.decDeg)} (today)',
                    style: hint,
                  ),
              ] else
                Text(
                  'Paste a position from SIMBAD, Gaia or your mount — '
                  'sexagesimal or decimal.',
                  style: hint,
                ),
              if (recents.isNotEmpty) ...[
                const SizedBox(height: 16),
                Text('Recent', style: theme.textTheme.labelLarge),
                for (final t in recents.take(6))
                  ListTile(
                    dense: true,
                    contentPadding: EdgeInsets.zero,
                    title: Text(t.name),
                    subtitle: Text(
                      '${formatRaHms(t.raDeg / 15)}  '
                      '${formatDecDms(t.decDeg)}',
                      style: hint,
                    ),
                    onTap: () => _fillFromRecent(t),
                    trailing: IconButton(
                      tooltip: 'Forget',
                      iconSize: 18,
                      icon: const Icon(Icons.close),
                      onPressed: () =>
                          ref.read(customTargetsProvider.notifier).forget(t),
                    ),
                  ),
              ],
            ],
          ),
        ),
      ),
      actions: [
        TextButton(
          onPressed: _busy ? null : () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        OutlinedButton.icon(
          icon: const Icon(Icons.my_location, size: 16),
          label: const Text('Show on sky'),
          onPressed: j2000 == null || _busy ? null : _showOnSky,
        ),
        FilledButton.icon(
          icon: _busy
              ? const SizedBox(
                  width: 14,
                  height: 14,
                  child: CircularProgressIndicator(strokeWidth: 2),
                )
              : const Icon(Icons.playlist_add, size: 16),
          label: const Text('Add to plan'),
          onPressed: j2000 == null || _busy ? null : _addToPlan,
        ),
      ],
    );
  }
}

/// Open the dialog; [initialText] pre-fills a pasted "RA Dec" line.
Future<void> showCustomTargetDialog(BuildContext context,
        {String? initialText}) =>
    showDialog<void>(
      context: context,
      builder: (_) => CustomTargetDialog(initialText: initialText),
    );
