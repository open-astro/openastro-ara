import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/wizard_state.dart';
import '../../theme/ara_colors.dart';

/// Shared chrome + field widgets for the real §37 wizard screens. Every
/// gear screen renders inside [WizardScreenScaffold] so the stage header,
/// the "this screen was skipped" banner, and the 560px reading column match
/// the placeholder layout they replace.

/// Standard scaffold for a wizard screen body: stage label + title + the
/// skipped banner (driven by the live draft) + a scrolling column of form
/// rows constrained to a comfortable reading width.
class WizardScreenScaffold extends ConsumerWidget {
  final int step;
  final List<Widget> children;

  /// Optional one-line intro under the title (the "what this screen does"
  /// line — keeps screens self-explanatory without a tooltip).
  final String? intro;

  const WizardScreenScaffold({
    super.key,
    required this.step,
    required this.children,
    this.intro,
  });

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    assert(ProfileWizard.steps.containsKey(step), 'Unknown wizard step $step');
    final info = ProfileWizard.steps[step]!;
    // Watch only the skipped-set membership so the banner appears/clears when
    // the user toggles Skip, without rebuilding on unrelated draft edits.
    final wasSkipped = ref.watch(wizardControllerProvider
        .select((s) => s.draft.skippedScreens.contains(step)));

    return Align(
      alignment: Alignment.topCenter,
      child: SingleChildScrollView(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 560),
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 24, vertical: 24),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Stage ${info.stage} — ${info.stageLabel}',
                  style: Theme.of(context).textTheme.labelLarge?.copyWith(
                        color: AraColors.textSecondary,
                        letterSpacing: 0.6,
                      ),
                ),
                const SizedBox(height: 4),
                Text(info.title,
                    style: Theme.of(context).textTheme.headlineSmall),
                if (intro != null) ...[
                  const SizedBox(height: 8),
                  Text(
                    intro!,
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: AraColors.textSecondary,
                        ),
                  ),
                ],
                if (wasSkipped) ...[
                  const SizedBox(height: 16),
                  const _SkippedBanner(),
                ],
                const SizedBox(height: 20),
                ...children,
                // Trailing breathing room so the last field clears the nav bar.
                const SizedBox(height: 8),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _SkippedBanner extends StatelessWidget {
  const _SkippedBanner();

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      decoration: BoxDecoration(
        color: AraColors.accentBusy.withValues(alpha: 0.15),
        borderRadius: BorderRadius.circular(4),
        border: Border.all(color: AraColors.accentBusy),
      ),
      child: Text(
        'This screen was skipped — defaults will fill in until you review it '
        'in Settings.',
        style: Theme.of(context)
            .textTheme
            .bodySmall
            ?.copyWith(color: AraColors.accentBusy),
      ),
    );
  }
}

/// A labelled text field bound to the draft. Writes back on every change so
/// the draft is always current when the user navigates away or hits Save &
/// Exit — wizard screens never rely on an explicit "apply" step.
class WizardTextField extends StatefulWidget {
  final String label;
  final String? hint;
  final String? helperText;
  final String? errorText;
  final String? initialValue;
  final bool required;
  final TextInputType? keyboardType;
  final List<TextInputFormatter>? inputFormatters;
  final ValueChanged<String> onChanged;

  const WizardTextField({
    super.key,
    required this.label,
    required this.onChanged,
    this.hint,
    this.helperText,
    this.errorText,
    this.initialValue,
    this.required = false,
    this.keyboardType,
    this.inputFormatters,
  });

  @override
  State<WizardTextField> createState() => _WizardTextFieldState();
}

class _WizardTextFieldState extends State<WizardTextField> {
  late final TextEditingController _controller;

  @override
  void initState() {
    super.initState();
    _controller = TextEditingController(text: widget.initialValue);
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 16),
      child: TextField(
        controller: _controller,
        keyboardType: widget.keyboardType,
        inputFormatters: widget.inputFormatters,
        onChanged: widget.onChanged,
        style: const TextStyle(color: AraColors.textPrimary),
        decoration: InputDecoration(
          labelText: widget.required ? '${widget.label} *' : widget.label,
          hintText: widget.hint,
          helperText: widget.helperText,
          errorText: widget.errorText,
          helperMaxLines: 3,
          filled: true,
          fillColor: AraColors.bgInput,
          border: const OutlineInputBorder(),
        ),
      ),
    );
  }
}

/// A labelled dropdown bound to an enum/value. Selecting writes back to the
/// draft via [onChanged].
class WizardDropdown<T> extends StatelessWidget {
  final String label;
  final T value;
  final List<DropdownMenuEntry<T>> entries;
  final ValueChanged<T?> onChanged;
  final String? helperText;

  const WizardDropdown({
    super.key,
    required this.label,
    required this.value,
    required this.entries,
    required this.onChanged,
    this.helperText,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          DropdownMenu<T>(
            initialSelection: value,
            label: Text(label),
            expandedInsets: EdgeInsets.zero,
            onSelected: onChanged,
            dropdownMenuEntries: entries,
          ),
          if (helperText != null) ...[
            const SizedBox(height: 4),
            Text(
              helperText!,
              style: Theme.of(context)
                  .textTheme
                  .bodySmall
                  ?.copyWith(color: AraColors.textSecondary),
            ),
          ],
        ],
      ),
    );
  }
}

/// A small read-only "derived value" chip — used for focal ratio, image
/// scale, etc. that the wizard computes from the user's inputs.
class WizardDerivedValue extends StatelessWidget {
  final String label;
  final String value;

  const WizardDerivedValue({super.key, required this.label, required this.value});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 16),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
        decoration: BoxDecoration(
          color: AraColors.bgPanelAlt,
          borderRadius: BorderRadius.circular(4),
          border: Border.all(color: AraColors.border),
        ),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(label,
                style: Theme.of(context)
                    .textTheme
                    .bodyMedium
                    ?.copyWith(color: AraColors.textSecondary)),
            const SizedBox(width: 12),
            // Long derived strings (e.g. "1.49 arcsec/pixel — wide-field DSO")
            // must wrap instead of overflowing the row.
            Expanded(
              child: Text(value,
                  textAlign: TextAlign.right,
                  style: Theme.of(context)
                      .textTheme
                      .bodyMedium
                      ?.copyWith(color: AraColors.textPrimary)),
            ),
          ],
        ),
      ),
    );
  }
}

/// Section heading inside a screen (e.g. "Backlash compensation").
class WizardSectionHeader extends StatelessWidget {
  final String text;
  const WizardSectionHeader(this.text, {super.key});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 12, top: 4),
      child: Text(
        text,
        style: Theme.of(context).textTheme.titleSmall?.copyWith(
              color: AraColors.textPrimary,
              fontWeight: FontWeight.w600,
            ),
      ),
    );
  }
}

/// Input formatters shared by the numeric fields.
class WizardInput {
  WizardInput._();

  /// Signed decimals (latitude, slope, temperatures).
  static final List<TextInputFormatter> signedDecimal = [
    FilteringTextInputFormatter.allow(RegExp(r'[0-9.\-]')),
  ];

  /// Unsigned decimals (focal length, aperture, pixel size).
  static final List<TextInputFormatter> unsignedDecimal = [
    FilteringTextInputFormatter.allow(RegExp(r'[0-9.]')),
  ];

  /// Unsigned integers (gain, offset, steps).
  static final List<TextInputFormatter> unsignedInt = [
    FilteringTextInputFormatter.digitsOnly,
  ];
}
