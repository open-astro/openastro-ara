import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/guider_advanced_settings.dart';
import '../../models/server.dart';
import '../../services/guider_advanced_api.dart';
import '../../state/focus/autofocus_live_state.dart' show describeRequestError;
import '../../state/saved_server_state.dart';
import '../../theme/ara_colors.dart';

/// Builds a [GuiderAdvancedClient] for a server. Overridable in tests.
final guiderAdvancedApiFactoryProvider =
    Provider<GuiderAdvancedClient Function(AraServer)>(
      (ref) => GuiderAdvancedApi.new,
    );

final guiderAdvancedApiProvider = Provider<GuiderAdvancedClient?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = ref.watch(guiderAdvancedApiFactoryProvider)(server);
  ref.onDispose(api.close);
  return api;
});

Future<void> showGuiderAdvancedDialog(BuildContext context) => showDialog<void>(
  context: context,
  builder: (_) => const Dialog(
    insetPadding: EdgeInsets.all(24),
    child: SizedBox(width: 760, height: 640, child: GuiderAdvancedPanel()),
  ),
);

/// #1364 item 3 — every guider setting beyond Tune Guiding, from Ara. Each row
/// shows the profile's value, or "Guider's" with the guider's current value
/// when the profile leaves it alone. Apply saves the profile and pushes the
/// runtime-safe settings while guiding continues; multi-star lands on the
/// next guider connect.
/// Test access to the panel's pure helpers.
abstract final class GuiderAdvancedPanelTestHooks {
  static (Object?, String?) parse(GuiderAdvancedField f, String raw) =>
      _GuiderAdvancedPanelState.parseNumber(f, raw);
  static String? crossFieldProblem(GuiderAdvancedSettings d) =>
      _GuiderAdvancedPanelState.crossFieldProblem(d);
}

class GuiderAdvancedPanel extends ConsumerStatefulWidget {
  const GuiderAdvancedPanel({super.key});

  @override
  ConsumerState<GuiderAdvancedPanel> createState() =>
      _GuiderAdvancedPanelState();
}

class _GuiderAdvancedPanelState extends ConsumerState<GuiderAdvancedPanel> {
  GuiderAdvancedSettings _draft = GuiderAdvancedSettings.empty;
  GuiderAdvancedView _guider = GuiderAdvancedView.disconnected;
  final Map<String, TextEditingController> _text = {};
  final Map<String, String> _errors = {};
  bool _loading = true;
  // Apply PUTs the whole section, so it is only allowed once the profile's
  // block has actually loaded: from the empty draft a failed load leaves, it
  // would erase every saved value.
  bool _profileLoaded = false;
  bool _applying = false;
  String? _status;
  bool _statusIsError = false;

  @override
  void initState() {
    super.initState();
    for (final f in GuiderAdvancedField.all) {
      if (f.kind == GuiderAdvancedKind.integer ||
          f.kind == GuiderAdvancedKind.decimal) {
        _text[f.key] = TextEditingController();
      }
    }
    Future<void>.microtask(_load);
  }

  @override
  void dispose() {
    for (final c in _text.values) {
      c.dispose();
    }
    super.dispose();
  }

  Future<void> _load() async {
    final api = ref.read(guiderAdvancedApiProvider);
    if (api == null) {
      setState(() => _loading = false);
      return;
    }
    try {
      final profile = await api.getProfile();
      GuiderAdvancedView guider = GuiderAdvancedView.disconnected;
      try {
        guider = await api.getGuider();
      } catch (_) {
        // The guider's values are a hint; the profile still edits without them.
      }
      if (!mounted) return;
      setState(() {
        _draft = profile;
        _guider = guider;
        _loading = false;
        _profileLoaded = true;
        for (final e in _text.entries) {
          final v = profile[e.key];
          e.value.text = v == null ? '' : GuiderAdvancedField.editText(v);
        }
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _status = describeRequestError(e);
        _statusIsError = true;
      });
    }
  }

  /// Parse a number field: empty → null (keep the guider's); otherwise a
  /// positive number or an error message. Pure — tested.
  static (Object?, String?) parseNumber(GuiderAdvancedField f, String raw) {
    final t = raw.trim();
    if (t.isEmpty) return (null, null);
    final n = num.tryParse(t);
    if (n == null || !n.isFinite || n < 0 || (n == 0 && !f.allowZero)) {
      return (null, f.allowZero ? 'Zero or a positive number' : 'A positive number');
    }
    if ((f.min != null && n < f.min!) || (f.max != null && n > f.max!)) {
      return (null, 'The guider takes ${f.min ?? (f.allowZero ? 0 : '>0')}–${f.max ?? '…'}');
    }
    if (f.kind == GuiderAdvancedKind.integer) {
      if (n != n.roundToDouble()) return (null, 'A whole number');
      return (n.round(), null);
    }
    return (n.toDouble(), null);
  }

  /// The server's rules across fields, checked before Apply so the user sees
  /// them by the fields rather than as a 400: the variable delay is all three
  /// values or none (the guider takes them together), and each min ≤ max.
  /// Pure — tested.
  static String? crossFieldProblem(GuiderAdvancedSettings d) {
    final delay = [
      d['variable_delay_enabled'],
      d['variable_delay_short_s'],
      d['variable_delay_long_s'],
    ].where((v) => v != null).length;
    if (delay == 1 || delay == 2) {
      return 'Variable delay needs all three: on/off, short and long delay.';
    }
    String? order(String min, String max, String what) {
      final lo = d[min], hi = d[max];
      return lo is num && hi is num && lo > hi
          ? '$what: the minimum is above the maximum.'
          : null;
    }

    return order('min_star_hfd', 'max_star_hfd', 'Star HFD') ??
        order(
          'auto_exposure_min_ms',
          'auto_exposure_max_ms',
          'Auto exposure',
        ) ??
        order('backlash_floor_ms', 'backlash_ceiling_ms', 'Backlash') ??
        order(
          'variable_delay_short_s',
          'variable_delay_long_s',
          'Variable delay',
        );
  }

  void _onText(GuiderAdvancedField f, String raw) {
    final (value, error) = parseNumber(f, raw);
    setState(() {
      if (error != null) {
        _errors[f.key] = error;
      } else {
        _errors.remove(f.key);
        _draft = _draft.withValue(f.key, value);
      }
    });
  }

  Future<void> _apply() async {
    final api = ref.read(guiderAdvancedApiProvider);
    if (api == null ||
        !_profileLoaded ||
        _errors.isNotEmpty ||
        crossFieldProblem(_draft) != null) {
      return;
    }
    setState(() {
      _applying = true;
      _status = null;
    });
    try {
      final saved = await api.putProfile(_draft);
      var pushed = false;
      if (_guider.connected) {
        await api.pushTuning();
        pushed = true;
      }
      if (!mounted) return;
      setState(() {
        _draft = saved;
        _applying = false;
        _statusIsError = false;
        _status = pushed
            ? 'Saved and sent to the guider. Multi-star applies on the next guider connect.'
            : 'Saved to the profile — sent to the guider when it connects.';
      });
      try {
        final guider = await api.getGuider();
        if (mounted) setState(() => _guider = guider);
      } catch (_) {}
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _applying = false;
        _statusIsError = true;
        _status = describeRequestError(e);
      });
    }
  }

  void _resetAll() {
    setState(() {
      _draft = GuiderAdvancedSettings.empty;
      _errors.clear();
      for (final c in _text.values) {
        c.clear();
      }
    });
  }

  @override
  Widget build(BuildContext context) {
    final text = Theme.of(context).textTheme;
    final small = text.bodySmall?.copyWith(color: AraColors.textSecondary);
    return Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              const Icon(Icons.tune, size: 18, color: AraColors.textSecondary),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  'Guider settings',
                  style: text.titleMedium,
                  overflow: TextOverflow.ellipsis,
                ),
              ),
              Text(
                _guider.connected ? 'Guider connected' : 'Guider not connected',
                style: small?.copyWith(
                  color: _guider.connected
                      ? AraColors.accentConnected
                      : AraColors.accentDisconnected,
                ),
              ),
              IconButton(
                tooltip: 'Close',
                icon: const Icon(Icons.close, size: 18),
                onPressed: () => Navigator.of(context).maybePop(),
              ),
            ],
          ),
          Text(
            'Blank or "Guider\'s" leaves the setting to the guider — Ara stops sending it, and the guider keeps what it has now, '
            'including a value Ara sent earlier. Anything you set here is kept in this profile.',
            style: small,
          ),
          const SizedBox(height: 8),
          Expanded(
            child: _loading
                ? const Center(child: CircularProgressIndicator())
                : ListView(
                    children: [
                      for (final group in GuiderAdvancedField.groups) ...[
                        Padding(
                          padding: const EdgeInsets.only(top: 12, bottom: 4),
                          child: Text(group, style: text.titleSmall),
                        ),
                        for (final f in GuiderAdvancedField.all.where(
                          (f) => f.group == group,
                        ))
                          _row(f, small),
                      ],
                    ],
                  ),
          ),
          if (_status != null)
            Padding(
              padding: const EdgeInsets.only(top: 6),
              child: Text(
                _status!,
                key: const Key('guider-advanced-status'),
                style: text.bodySmall?.copyWith(
                  color: _statusIsError
                      ? AraColors.accentError
                      : AraColors.textSecondary,
                ),
              ),
            ),
          if (crossFieldProblem(_draft) case final problem?)
            Padding(
              padding: const EdgeInsets.only(top: 6),
              child: Text(
                problem,
                key: const Key('guider-advanced-cross-field'),
                style: text.bodySmall?.copyWith(color: AraColors.accentError),
              ),
            ),
          const SizedBox(height: 8),
          Row(
            children: [
              TextButton(
                onPressed: _loading || _applying || !_profileLoaded
                    ? null
                    : _resetAll,
                // Clears the profile's values only; nothing already sent is undone.
                child: const Text("All guider's"),
              ),
              const Spacer(),
              TextButton(
                onPressed: () => Navigator.of(context).maybePop(),
                child: const Text('Done'),
              ),
              const SizedBox(width: 8),
              FilledButton.icon(
                onPressed:
                    _loading ||
                        _applying ||
                        !_profileLoaded ||
                        _errors.isNotEmpty ||
                        crossFieldProblem(_draft) != null
                    ? null
                    : _apply,
                icon: _applying
                    ? const SizedBox(
                        width: 16,
                        height: 16,
                        child: CircularProgressIndicator(strokeWidth: 2),
                      )
                    : const Icon(Icons.send, size: 18),
                label: const Text('Apply'),
              ),
            ],
          ),
        ],
      ),
    );
  }

  Widget _row(GuiderAdvancedField f, TextStyle? small) {
    final guiderValue = _guider.current[f.key];
    final guiderLabel = _guider.connected
        ? "Guider's (${f.format(guiderValue)})"
        : "Guider's";
    final Widget editor = switch (f.kind) {
      GuiderAdvancedKind.toggle => DropdownButton<bool?>(
        key: Key('ga-${f.key}'),
        isDense: true,
        value: _draft[f.key] as bool?,
        items: [
          DropdownMenuItem<bool?>(value: null, child: Text(guiderLabel)),
          const DropdownMenuItem<bool?>(value: true, child: Text('On')),
          const DropdownMenuItem<bool?>(value: false, child: Text('Off')),
        ],
        onChanged: (v) => setState(() => _draft = _draft.withValue(f.key, v)),
      ),
      GuiderAdvancedKind.choice => _choice(f, guiderLabel),
      GuiderAdvancedKind.integer || GuiderAdvancedKind.decimal => SizedBox(
        width: 150,
        child: TextField(
          key: Key('ga-${f.key}'),
          controller: _text[f.key],
          keyboardType: const TextInputType.numberWithOptions(decimal: true),
          decoration: InputDecoration(
            isDense: true,
            hintText: _guider.connected
                ? 'guider: ${f.format(guiderValue)}'
                : "guider's",
            suffixText: f.unit,
            errorText: _errors[f.key],
          ),
          onChanged: (raw) => _onText(f, raw),
        ),
      ),
    };
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(
        children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(f.label),
                Text(
                  f.help,
                  style: small,
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                ),
              ],
            ),
          ),
          const SizedBox(width: 12),
          editor,
        ],
      ),
    );
  }

  Widget _choice(GuiderAdvancedField f, String guiderLabel) {
    final offered =
        f.options ??
        (f.key == 'ra_algorithm'
            ? _guider.raAlgorithms
            : f.key == 'dec_algorithm'
            ? _guider.decAlgorithms
            : const <String>[]);
    final current = _draft[f.key] as String?;
    final options = [
      ...offered,
      if (current != null && !offered.contains(current)) current,
    ];
    return DropdownButton<String?>(
      key: Key('ga-${f.key}'),
      isDense: true,
      value: current,
      items: [
        DropdownMenuItem<String?>(value: null, child: Text(guiderLabel)),
        for (final o in options)
          DropdownMenuItem<String?>(value: o, child: Text(o)),
      ],
      onChanged: (v) => setState(() => _draft = _draft.withValue(f.key, v)),
    );
  }
}
