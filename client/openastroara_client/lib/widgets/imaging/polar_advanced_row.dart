import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/polar_align.dart';
import '../../state/polar_align/polar_align_routine_state.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../state/profile_management_state.dart';
import '../../state/settings/loaded_section.dart';
import '../../state/settings/polar_align_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/friendly_error.dart';
import '../help_icon.dart';
import '../settings/section_load_state.dart';

/// #1736 The Ready step's collapsed Advanced row: the routine's two knobs
/// (`seed_rotation_deg`, `coarse_exposure_seconds`), edited
/// through the same module as the Settings panel
/// ([polarAlignSettingsProvider], ADR 0013 rule 3): nothing shows or saves
/// until the rig's copy is loaded, each edit saves at once, a failed save puts
/// the field back, and a value outside the range is refused in the field.
class PolarAdvancedRow extends ConsumerStatefulWidget {
  const PolarAdvancedRow({super.key});

  @override
  ConsumerState<PolarAdvancedRow> createState() => _PolarAdvancedRowState();
}

class _PolarAdvancedRowState extends ConsumerState<PolarAdvancedRow> {
  bool _open = false;
  String? _saveError;

  void _retry() {
    final api = ref.read(profileApiProvider);
    if (api != null) ref.read(polarAlignSettingsProvider.notifier).load(api);
  }

  /// [change] on the rig's copy as it is when the edit lands (ADR 0015); a
  /// 412 reloads the rig's copy (this row has no Reload of its own).
  Future<void> _save(
    PolarAlignSettings Function(PolarAlignSettings) change,
  ) async {
    final l10n = context.l10n;
    setState(() => _saveError = null);
    // Through the routine's action module (ADR 0013 rule 3): busy while the
    // PUT is in flight, so Start waits for it. The error line sits under the
    // grid, so describe says it here and returns null.
    await ref
        .read(polarAlignActionProvider.notifier)
        .run(
          (_) => ref
              .read(polarAlignSettingsProvider.notifier)
              .changeAndSave(change),
          describe: (e) {
            if (mounted) {
              setState(
                () => _saveError = l10n.polarAdvancedSaveFailed(
                  e is SectionNotLoadedException ||
                          e is SectionConflictException
                      ? e.toString()
                      : friendlyError(l10n, e, action: l10n.polarActionSave),
                ),
              );
            }
            return null;
          },
        );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final settings = ref.watch(polarAlignSettingsProvider);
    final s = settings.value;
    final loaded = s != null && ref.watch(polarAlignApiProvider) != null;
    return Container(
      key: const Key('polar-advanced-row'),
      decoration: const BoxDecoration(
        border: Border(top: BorderSide(color: AraColors.border)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Semantics(
            button: true,
            expanded: _open,
            child: InkWell(
              key: const Key('polar-advanced-toggle'),
              onTap: () => setState(() => _open = !_open),
              child: ConstrainedBox(
                constraints: const BoxConstraints(minHeight: 48),
                child: Row(
                  children: [
                    Icon(
                      _open ? Icons.expand_more : Icons.chevron_right,
                      size: 20,
                      color: AraColors.textSecondary,
                    ),
                    const SizedBox(width: 8),
                    Text(
                      l10n.polarAdvancedTitle,
                      style: const TextStyle(fontWeight: FontWeight.w600),
                    ),
                    if (loaded && !_open) ...[
                      const Spacer(),
                      Flexible(
                        child: Text(
                          l10n.polarAdvancedSummary(
                            polarKnobText(s.seedRotationDeg),
                            polarKnobText(s.coarseExposureSeconds),
                          ),
                          overflow: TextOverflow.ellipsis,
                          style: const TextStyle(
                            fontSize: 12,
                            color: AraColors.textSecondary,
                          ),
                        ),
                      ),
                    ],
                  ],
                ),
              ),
            ),
          ),
          if (_open)
            if (!loaded)
              SectionNotLoaded(section: settings, onRetry: _retry)
            else
              _knobs(l10n, s),
        ],
      ),
    );
  }

  Widget _knobs(AppLocalizations l10n, PolarAlignSettings s) {
    final fields = [
      _numberField(
        const Key('polar-advanced-seed'),
        l10n.polarKnobTurn,
        'img.polar.seed_rotation_deg',
        polarSeedRotationRange,
        (x) => x.seedRotationDeg,
        (x, v) => x.copyWith(seedRotationDeg: v),
        l10n,
      ),
      _numberField(
        const Key('polar-advanced-coarse'),
        l10n.polarKnobMainExposure,
        'img.polar.coarse_exposure_seconds',
        polarCoarseExposureRange,
        (x) => x.coarseExposureSeconds,
        (x, v) => x.copyWith(coarseExposureSeconds: v),
        l10n,
      ),
    ];
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          // Two columns, the name above each field; one on a phone.
          LayoutBuilder(
            builder: (context, box) {
              final two = box.maxWidth >= 480;
              final width = two ? (box.maxWidth - 16) / 2 : box.maxWidth;
              return Wrap(
                spacing: 16,
                runSpacing: 12,
                children: [
                  for (final f in fields) SizedBox(width: width, child: f),
                ],
              );
            },
          ),
          if (_saveError != null) ...[
            const SizedBox(height: 8),
            Semantics(
              liveRegion: true,
              child: Text(
                _saveError!,
                key: const Key('polar-advanced-save-error'),
                style: const TextStyle(color: AraColors.accentError),
              ),
            ),
          ],
        ],
      ),
    );
  }

  Widget _label(String text, String helpKey) => Row(
    children: [
      Flexible(
        child: Text(
          text,
          style: const TextStyle(color: AraColors.textSecondary),
        ),
      ),
      HelpIcon(helpKey: helpKey),
    ],
  );

  Widget _numberField(
    Key key,
    String label,
    String helpKey,
    PolarKnobRange range,
    double Function(PolarAlignSettings) read,
    PolarAlignSettings Function(PolarAlignSettings, double) apply,
    AppLocalizations l10n,
  ) {
    final current = ref.read(polarAlignSettingsProvider).value!;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _label(label, helpKey),
        const SizedBox(height: 4),
        _KnobField(
          key: key,
          label: label,
          value: polarKnobText(read(current)),
          onCommit: (text) {
            final v = double.tryParse(text.trim());
            if (v == null || v < range.min || v > range.max) {
              return l10n.polarAdvancedOutOfRange(
                l10n.polarAdvancedRange(
                  polarKnobText(range.min),
                  polarKnobText(range.max),
                ),
              );
            }
            final now = ref.read(polarAlignSettingsProvider).value ?? current;
            if (v != read(now)) _save((x) => apply(x, v));
            return null;
          },
        ),
      ],
    );
  }
}

/// A number field that commits on submit and on leaving it, and follows the
/// rig's value (a rollback, a save from the Settings panel) when not being
/// edited. A refused entry stays in the field, with its error under it, until
/// the next edit or a change of the rig's value.
class _KnobField extends StatefulWidget {
  const _KnobField({
    super.key,
    required this.label,
    required this.value,
    required this.onCommit,
  });

  final String label;
  final String value;

  /// Returns the refusal to show under the field, or null when it took the text.
  final String? Function(String text) onCommit;

  @override
  State<_KnobField> createState() => _KnobFieldState();
}

class _KnobFieldState extends State<_KnobField> {
  late final _controller = TextEditingController(text: widget.value);
  late final FocusNode _focus = FocusNode()
    ..addListener(() {
      if (!_focus.hasFocus) _commit();
    });

  String? _error;

  void _commit() {
    setState(() => _error = widget.onCommit(_controller.text));
    // After the rebuild: a refusal keeps the entry on screen (its error shows);
    // anything else shows the rig's value, so a rolled-back save puts it back.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted && _error == null) _controller.text = widget.value;
    });
  }

  @override
  void didUpdateWidget(covariant _KnobField old) {
    super.didUpdateWidget(old);
    if (old.value != widget.value) {
      _controller.text = widget.value;
      _error = null;
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    _focus.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Semantics(
    label: widget.label,
    child: TextField(
      controller: _controller,
      focusNode: _focus,
      keyboardType: const TextInputType.numberWithOptions(decimal: true),
      decoration: InputDecoration(isDense: true, errorText: _error),
      onSubmitted: (_) => _commit(),
      onTapOutside: (_) => _focus.unfocus(),
    ),
  );
}
