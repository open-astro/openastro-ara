import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../state/rig_setup_state.dart';
import '../../state/saved_server_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../state/simple_messages_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import 'simple_device_windows.dart' show SimpleWindowButton;
import 'simple_shell.dart' show SimplePanel, simpleRailScale;

/// #1562 — Simple Mode's first-time Rig setup: the sheet that lists what the
/// rig still needs before a first night, each line with the button that opens
/// the setting that fixes it. It sits under the status strip and beside the
/// right rail, so those and Emergency Stop stay visible; on a window too narrow
/// to clear the left rail too (a phone) it covers that one.
///
/// It opens by itself once per shell mount per rig (the link up, the profile
/// read, something missing, "Not now" not chosen on this device), closes with
/// a "Rig set up" pill when nothing is missing. Settings › Rig heads with
/// [SimpleRigSetupHeader] while something is missing.
class SimpleRigSetupHost extends ConsumerStatefulWidget {
  const SimpleRigSetupHost({
    super.key,
    required this.autoOpened,
    required this.onOpenRig,
    required this.onOpenGuider,
  });

  /// The rigs this shell mount already opened the sheet for. The shell owns it:
  /// this widget is rebuilt when the layout changes, and must not open again.
  final Set<String> autoOpened;

  /// Simple Mode's Settings at the Rig page: where the site and the guide
  /// scope live. The sheet stays up under it, so its ticks follow the edits.
  final VoidCallback onOpenRig;

  /// Closes the sheet and shows Guider mode, where the calibration is made.
  final VoidCallback onOpenGuider;

  @override
  ConsumerState<SimpleRigSetupHost> createState() => _SimpleRigSetupHostState();
}

class _SimpleRigSetupHostState extends ConsumerState<SimpleRigSetupHost> {
  @override
  void initState() {
    super.initState();
    ref.listenManual(
      rigSetupGapsProvider,
      (_, _) => _autoOpen(),
      fireImmediately: true,
    );
    ref.listenManual(rigSetupDismissedProvider, (_, _) => _autoOpen());
    ref.listenManual(rigSetupFactsProvider, (_, facts) {
      // Nothing missing and trusted facts (not a dropped link): done.
      if (facts != null &&
          rigSetupGaps(facts).isEmpty &&
          ref.read(rigSetupSheetProvider)) {
        _close();
        ref
            .read(simpleMessagesProvider.notifier)
            .show(SimpleMessageKind.result, context.l10n.rigSetupDonePill);
      }
    });
  }

  void _autoOpen() {
    final key = rigSetupRigKey(ref.read(activeServerProvider));
    if (key == null || widget.autoOpened.contains(key)) return;
    if (ref.read(rigSetupDismissedProvider).value != false) return;
    if (ref.read(rigSetupGapsProvider).isEmpty) return;
    widget.autoOpened.add(key);
    // A frame late: this can fire while the tree builds.
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (mounted) ref.read(rigSetupSheetProvider.notifier).set(true);
    });
  }

  void _close() => WidgetsBinding.instance.addPostFrameCallback((_) {
    if (mounted) ref.read(rigSetupSheetProvider.notifier).set(false);
  });

  Future<void> _notNow() async {
    ref.read(rigSetupSheetProvider.notifier).set(false);
    await ref.read(rigSetupDismissedProvider.notifier).dismiss();
  }

  @override
  Widget build(BuildContext context) {
    final facts = ref.watch(rigSetupFactsProvider);
    if (!ref.watch(rigSetupSheetProvider) || facts == null) {
      return const SizedBox.shrink();
    }
    final l10n = context.l10n;
    final items = _items(l10n, facts);
    final lines = [
      for (final item in items)
        _Line(
          id: item.id,
          label: item.label,
          missing: item.missing,
          action: item.action,
          onTap: switch (item.gap) {
            RigSetupGap.site || RigSetupGap.guideScope => widget.onOpenRig,
            RigSetupGap.mainOptics => () => openSettingsPanel(ref, 'img.optics'),
            RigSetupGap.guiderCalibration => () {
              ref.read(rigSetupSheetProvider.notifier).set(false);
              widget.onOpenGuider();
            },
          },
        ),
    ];
    return Center(
      child: ConstrainedBox(
        constraints: BoxConstraints(maxWidth: 460 * simpleRailScale(context)),
        child: SingleChildScrollView(
          child: SimplePanel(
            padding: const EdgeInsets.all(AraSpace.s16),
            child: Column(
              key: const Key('simple-rig-setup'),
              crossAxisAlignment: CrossAxisAlignment.stretch,
              spacing: AraSpace.s12,
              children: [
                Semantics(
                  header: true,
                  child: Text(l10n.rigSetupTitle, style: kSimpleHeading),
                ),
                Text(
                  l10n.rigSetupIntro,
                  style: kSimpleText.copyWith(color: AraColors.textSecondary),
                ),
                for (final line in lines) line,
                // The count, and Not now as plain text: the amber fixes above
                // are the actions.
                const Divider(height: 1, color: AraColors.border),
                Row(
                  children: [
                    Expanded(
                      child: Text(
                        _progress(l10n, items),
                        style: AraText.caption,
                      ),
                    ),
                    TextButton(
                      key: const Key('simple-rig-setup-not-now'),
                      style: TextButton.styleFrom(
                        foregroundColor: AraColors.textSecondary,
                        minimumSize: const Size(44, 44),
                      ),
                      onPressed: _notNow,
                      child: Text(l10n.rigSetupNotNow, style: kSimpleText),
                    ),
                  ],
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

/// A line narrower than this at 1x text puts its button under its words, so
/// no word breaks mid-word (pt-BR's longest label and button take about
/// 350 px side by side). It grows with the text, as the sheet does.
const double _kLineBesideWidth = 400;

/// One line: a tick when done, else what is missing and the button for it,
/// beside the words, or under them on a narrow sheet (a phone).
class _Line extends StatelessWidget {
  const _Line({
    required this.id,
    required this.label,
    required this.missing,
    required this.action,
    required this.onTap,
  });

  final String id;
  final String label;

  /// What is missing; null when the line is done.
  final String? missing;
  final String action;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final done = missing == null;
    final button = done
        ? null
        : SimpleWindowButton(
            key: Key('simple-rig-setup-$id-action'),
            label: action,
            onTap: onTap,
            accent: AraColors.accentWarning,
          );
    final row = LayoutBuilder(
      builder: (context, box) {
        final beside =
            box.maxWidth >= _kLineBesideWidth * simpleRailScale(context);
        return Row(
          key: Key('simple-rig-setup-$id'),
          crossAxisAlignment: beside
              ? CrossAxisAlignment.center
              : CrossAxisAlignment.start,
          spacing: AraSpace.s12,
          children: [
            Semantics(
              label: done ? l10n.rigSetupItemDone : null,
              child: Icon(
                done ? Icons.check_circle : Icons.radio_button_unchecked,
                key: Key('simple-rig-setup-$id-${done ? 'done' : 'missing'}'),
                color: done
                    ? AraColors.accentConnected
                    : AraColors.textSecondary,
              ),
            ),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    label,
                    style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
                  ),
                  if (missing != null)
                    Text(
                      missing!,
                      style: kSimpleText.copyWith(
                        color: AraColors.textSecondary,
                      ),
                    ),
                  // Under the words, as wide as they may run.
                  if (button != null && !beside)
                    Padding(
                      padding: const EdgeInsets.only(top: AraSpace.s8),
                      child: button,
                    ),
                ],
              ),
            ),
            if (button != null && beside) button,
          ],
        );
      },
    );
    if (done) {
      return Padding(
        padding: const EdgeInsets.symmetric(horizontal: AraSpace.s12),
        child: row,
      );
    }
    // A missing fact and its fix in one amber box, so the button reads as
    // this line's.
    return DecoratedBox(
      key: Key('simple-rig-setup-$id-box'),
      decoration: BoxDecoration(
        color: AraColors.accentWarning.withValues(alpha: 0.10),
        border: Border.all(
          color: AraColors.accentWarning.withValues(alpha: 0.45),
        ),
        borderRadius: BorderRadius.circular(AraRadius.container),
      ),
      child: Padding(
        padding: const EdgeInsets.all(AraSpace.s12),
        child: row,
      ),
    );
  }
}

/// One Rig setup item: its words, what is missing (null when done) and the
/// fix's label, in the sheet's order.
typedef _Item = ({
  RigSetupGap gap,
  String id,
  String label,
  String? missing,
  String action,
});

List<_Item> _items(AppLocalizations l10n, RigSetupFacts facts) {
  final gaps = rigSetupGaps(facts);
  return [
    (
      gap: RigSetupGap.site,
      id: 'site',
      label: l10n.rigSetupSite,
      missing: gaps.contains(RigSetupGap.site) ? l10n.rigSetupSiteMissing : null,
      action: l10n.rigSetupSiteAction,
    ),
    (
      gap: RigSetupGap.mainOptics,
      id: 'optics',
      label: l10n.rigSetupOptics,
      missing: gaps.contains(RigSetupGap.mainOptics)
          ? l10n.rigSetupOpticsMissing
          : null,
      action: l10n.rigSetupOpticsAction,
    ),
    if (facts.hasGuider) ...[
      (
        gap: RigSetupGap.guideScope,
        id: 'guide-scope',
        label: l10n.rigSetupGuideScope,
        missing: gaps.contains(RigSetupGap.guideScope)
            ? l10n.rigSetupGuideScopeMissing
            : null,
        action: l10n.rigSetupGuideScopeAction,
      ),
      (
        gap: RigSetupGap.guiderCalibration,
        id: 'calibration',
        label: l10n.rigSetupCalibration,
        missing: !gaps.contains(RigSetupGap.guiderCalibration)
            ? null
            : facts.calibrationUnchecked
            ? l10n.rigSetupCalibrationConnect
            : l10n.rigSetupCalibrationMissing,
        action: l10n.rigSetupCalibrationAction,
      ),
    ],
  ];
}

String _progress(AppLocalizations l10n, List<_Item> items) => l10n
    .rigSetupProgress(items.where((i) => i.missing == null).length, items.length);

/// #1562 — Settings › Rig's header while the rig still lacks a fact: the count,
/// a bar, and the next missing item with its fix. [onFix] runs the fix for
/// that item; it is gone once nothing is missing.
class SimpleRigSetupHeader extends ConsumerWidget {
  const SimpleRigSetupHeader({super.key, required this.onFix});

  final ValueChanged<RigSetupGap> onFix;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final facts = ref.watch(rigSetupFactsProvider);
    if (facts == null) return const SizedBox.shrink();
    final l10n = context.l10n;
    final items = _items(l10n, facts);
    final next = items.where((i) => i.missing != null).firstOrNull;
    if (next == null) return const SizedBox.shrink();
    final done = items.where((i) => i.missing == null).length;
    return DecoratedBox(
      key: const Key('simple-rig-setup-header'),
      decoration: BoxDecoration(
        color: AraColors.bgPanelAlt,
        border: Border.all(color: AraColors.border),
        borderRadius: BorderRadius.circular(AraRadius.container),
      ),
      child: Padding(
        padding: const EdgeInsets.all(AraSpace.s12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: AraSpace.s8,
          children: [
            Row(
              children: [
                Expanded(
                  child: Semantics(
                    header: true,
                    child: Text(
                      l10n.rigSetupTitle,
                      style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
                    ),
                  ),
                ),
                Text(_progress(l10n, items), style: AraText.caption),
              ],
            ),
            LinearProgressIndicator(
              value: done / items.length,
              minHeight: 4,
              color: AraColors.accentConnected,
              backgroundColor: AraColors.bgInput,
            ),
            Row(
              spacing: AraSpace.s12,
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        l10n.rigSetupNext(next.label),
                        style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
                      ),
                      Text(next.missing!, style: AraText.caption),
                    ],
                  ),
                ),
                SimpleWindowButton(
                  key: const Key('simple-rig-setup-header-action'),
                  label: next.action,
                  onTap: () => onFix(next.gap),
                  accent: AraColors.accentWarning,
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}
