import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../services/session_live_stack_api.dart';
import '../../state/simple_live_stack_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';

/// "Live Stack · 3 lights", then "· 2 left out" for the lights the stack could
/// not add (not registered, or the stacker was busy).
String simpleLiveStackReadout(AppLocalizations l10n, SessionLiveStackStatus s) {
  final text = l10n.simpleLiveStackLights(s.lightsStacked);
  final out = s.lightsUnregistered + s.lightsSkippedBusy;
  return out == 0 ? text : '$text · ${l10n.simpleLiveStackLeftOut(out)}';
}

/// Why the stack cannot run with this camera, for the two reasons the user can
/// act on (`not_colour`, `too_large`); null for any other token.
String? simpleLiveStackReason(AppLocalizations l10n, String? token) =>
    switch (token) {
      'not_colour' => l10n.simpleLiveStackNotColour,
      'too_large' => l10n.simpleLiveStackTooLarge,
      _ => null,
    };

/// The Live Stack switch in the run panel: the switch, Reset, and (while on)
/// the choice between the stack and the last light. Hidden until the daemon
/// says the stack can run, or says why it cannot (`not_colour`, `too_large`).
class SimpleLiveStackRow extends ConsumerWidget {
  const SimpleLiveStackRow({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final s = ref.watch(simpleLiveStackProvider);
    final status = s.status;
    if (status == null) return const SizedBox.shrink();
    final reason = simpleLiveStackReason(l10n, status.unsupported);
    if (!s.supported && reason == null) return const SizedBox.shrink();
    final stack = ref.read(simpleLiveStackProvider.notifier);
    final dim = kSimpleText.copyWith(color: AraColors.textSecondary);
    final outcome = s.supported
        ? simpleLiveStackReason(l10n, status.lastOutcome)
        : null;
    return Column(
      key: const Key('simple-live-stack-row'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Row(
          children: [
            Expanded(
              child: Text(l10n.simpleLiveStackTitle, style: kSimpleText),
            ),
            if (s.supported)
              Semantics(
                label: l10n.simpleLiveStackTitle,
                child: Switch(
                  key: const Key('simple-live-stack-switch'),
                  value: s.on,
                  onChanged: stack.setOn,
                ),
              ),
          ],
        ),
        if (!s.supported)
          Text(
            reason!,
            key: const Key('simple-live-stack-unavailable'),
            style: dim,
          ),
        if (outcome != null)
          Text(
            outcome,
            key: const Key('simple-live-stack-outcome'),
            style: dim,
          ),
        if (s.supported) ...[
          if (s.on)
            Wrap(
              key: const Key('simple-live-stack-view'),
              spacing: AraSpace.s8,
              children: [
                ChoiceChip(
                  key: const Key('simple-live-stack-view-stack'),
                  label: Text(l10n.simpleLiveStackViewStack),
                  selected: s.view == SimpleLiveStackView.stack,
                  materialTapTargetSize: MaterialTapTargetSize.padded,
                  onSelected: (_) => stack.setView(SimpleLiveStackView.stack),
                ),
                ChoiceChip(
                  key: const Key('simple-live-stack-view-last'),
                  label: Text(l10n.simpleLiveStackViewLastLight),
                  selected: s.view == SimpleLiveStackView.lastLight,
                  materialTapTargetSize: MaterialTapTargetSize.padded,
                  onSelected: (_) =>
                      stack.setView(SimpleLiveStackView.lastLight),
                ),
              ],
            ),
          Align(
            alignment: Alignment.centerLeft,
            child: TextButton(
              key: const Key('simple-live-stack-reset'),
              onPressed: s.on ? stack.reset : null,
              child: Text(l10n.simpleLiveStackReset),
            ),
          ),
        ],
      ],
    );
  }
}

/// The count and target over the stacked picture, at the foot of the frame.
/// Empty until a light is stacked.
class SimpleLiveStackReadout extends ConsumerWidget {
  const SimpleLiveStackReadout({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final s = ref.watch(simpleLiveStackProvider);
    final status = s.status;
    if (!s.on || status == null || status.lightsStacked == 0) {
      return const SizedBox.shrink();
    }
    final target = status.targetName;
    final text = [
      simpleLiveStackReadout(context.l10n, status),
      if (target != null && target.isNotEmpty) target,
    ].join(' · ');
    return Semantics(
      key: const Key('simple-live-stack-readout'),
      container: true,
      label: text,
      child: ExcludeSemantics(
        child: DecoratedBox(
          decoration: BoxDecoration(
            color: AraColors.overlayScrim,
            borderRadius: BorderRadius.circular(AraRadius.container),
          ),
          child: Padding(
            padding: const EdgeInsets.symmetric(
              horizontal: AraSpace.s12,
              vertical: AraSpace.s8,
            ),
            child: Text(text, style: kSimpleText),
          ),
        ),
      ),
    );
  }
}
