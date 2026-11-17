import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/polar_align.dart';
import '../../state/polar_align/polar_align_routine_state.dart';
import '../../state/polar_align/polar_align_state.dart';
import '../../state/settings/loaded_section.dart';
import '../../theme/ara_colors.dart';
import '../../util/friendly_error.dart';
import '../help_icon.dart';
import '../settings/section_load_state.dart';

/// #1736 The Start card's collapsed Advanced row: the routine's four knobs
/// (`use_home`, `check_step_deg`, `coarse_exposure_seconds`,
/// `seed_rotation_deg`), edited through the same module as the Settings panel
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

  /// Each field's refusal, keyed by its field key; absent: no complaint.
  final _refused = <Key, String>{};

  void _retry() => ref.invalidate(polarAlignSettingsProvider);

  Future<void> _save(PolarAlignSettings next) async {
    final l10n = context.l10n;
    setState(() => _saveError = null);
    try {
      await ref.read(polarAlignSettingsProvider.notifier).save(next);
    } catch (e) {
      if (!mounted) return;
      setState(
        () => _saveError = l10n.polarAdvancedSaveFailed(
          friendlyError(l10n, e, action: l10n.polarActionSave),
        ),
      );
    }
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
                            polarKnobText(s.checkStepDeg),
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
              SectionNotLoaded(
                section: LoadedSection(lastFailure: settings.error),
                onRetry: _retry,
              )
            else
              _knobs(l10n, s),
        ],
      ),
    );
  }

  Widget _knobs(AppLocalizations l10n, PolarAlignSettings s) {
    final fields = [
      _homeField(l10n, s),
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
        const Key('polar-advanced-check-step'),
        l10n.polarKnobVerifyStep,
        'img.polar.check_step_deg',
        polarCheckStepRange,
        (x) => x.checkStepDeg,
        (x, v) => x.copyWith(checkStepDeg: v),
        l10n,
      ),
      _numberField(
        const Key('polar-advanced-coarse'),
        l10n.polarKnobRoughExposure,
        'img.polar.coarse_exposure_seconds',
        polarCoarseExposureRange,
        (x) => x.coarseExposureSeconds,
        (x, v) => x.copyWith(coarseExposureSeconds: v),
        l10n,
      ),
    ];
    final running = ref.watch(polarAlignLiveProvider).isActive;
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
          if (running) ...[
            const SizedBox(height: 8),
            Text(
              l10n.polarAdvancedRunning,
              key: const Key('polar-advanced-running-note'),
              style: const TextStyle(color: AraColors.textSecondary),
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

  Widget _homeField(AppLocalizations l10n, PolarAlignSettings s) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      _label(l10n.polarKnobUseHome, 'img.polar.use_home'),
      Row(
        children: [
          Switch(
            key: const Key('polar-advanced-use-home'),
            value: s.useHome,
            onChanged: (v) => _save(s.copyWith(useHome: v)),
          ),
          Expanded(
            child: Text(
              s.useHome ? l10n.polarKnobUseHomeOn : l10n.polarKnobUseHomeOff,
              style: const TextStyle(
                fontSize: 12,
                color: AraColors.textSecondary,
              ),
            ),
          ),
        ],
      ),
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
          value: polarKnobText(read(current)),
          error: _refused[key],
          onCommit: (text) {
            final v = double.tryParse(text.trim());
            if (v == null || v < range.min || v > range.max) {
              setState(
                () => _refused[key] = l10n.polarAdvancedOutOfRange(
                  l10n.polarAdvancedRange(
                    polarKnobText(range.min),
                    polarKnobText(range.max),
                  ),
                ),
              );
              return;
            }
            if (_refused.containsKey(key)) setState(() => _refused.remove(key));
            final now = ref.read(polarAlignSettingsProvider).value ?? current;
            if (v != read(now)) _save(apply(now, v));
          },
        ),
      ],
    );
  }
}

/// A number field that commits on submit and on leaving it, and follows the
/// rig's value (a rollback, a save from the Settings panel) when not being
/// edited. A refused entry stays in the field, with [error] under it, until
/// the next edit or a change of the rig's value.
class _KnobField extends StatefulWidget {
  const _KnobField({
    super.key,
    required this.value,
    required this.error,
    required this.onCommit,
  });

  final String value;
  final String? error;
  final void Function(String text) onCommit;

  @override
  State<_KnobField> createState() => _KnobFieldState();
}

class _KnobFieldState extends State<_KnobField> {
  late final _controller = TextEditingController(text: widget.value);
  late final FocusNode _focus = FocusNode()
    ..addListener(() {
      if (!_focus.hasFocus) _commit();
    });

  void _commit() {
    widget.onCommit(_controller.text);
    // After the rebuild: a refusal keeps the entry on screen (its error shows);
    // anything else shows the rig's value, so a rolled-back save puts it back.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted && widget.error == null) _controller.text = widget.value;
    });
  }

  @override
  void didUpdateWidget(covariant _KnobField old) {
    super.didUpdateWidget(old);
    if (old.value != widget.value) _controller.text = widget.value;
  }

  @override
  void dispose() {
    _controller.dispose();
    _focus.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => TextField(
    controller: _controller,
    focusNode: _focus,
    keyboardType: const TextInputType.numberWithOptions(decimal: true),
    decoration: InputDecoration(isDense: true, errorText: widget.error),
    onSubmitted: (_) => _commit(),
    onTapOutside: (_) => _focus.unfocus(),
  );
}
