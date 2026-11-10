import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../services/storage_devices_api.dart';
import '../../services/storage_space_api.dart';
import '../../state/settings/storage_settings_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../widgets/inline_choices.dart';
import '../../widgets/storage/storage_drive_dialog.dart';
import '../../widgets/support/bug_report_card.dart';
import '../settings/panels/storage_panel.dart';

/// A Full UI block hosted in Simple Mode: its small text (Material's 12 px
/// bodySmall) reads at Simple Mode's 14 px; the full UI keeps 12.
class SimpleHostedText extends StatelessWidget {
  const SimpleHostedText({super.key, required this.child});

  final Widget child;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Theme(
      data: theme.copyWith(
        textTheme: theme.textTheme.copyWith(
          bodySmall: theme.textTheme.bodySmall?.copyWith(
            fontSize: kSimpleText.fontSize,
          ),
        ),
      ),
      child: child,
    );
  }
}

/// Settings › Rig › Logs & bug report: the Full UI's bug report card, hosted
/// as it is. Its Sharing adds the client log, which needs no rig.
class SimpleLogsBlock extends StatelessWidget {
  const SimpleLogsBlock({super.key});

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    spacing: AraSpace.s8,
    children: [
      Text(
        context.l10n.shellLogs,
        style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
      ),
      const SimpleHostedText(child: BugReportCard()),
    ],
  );
}

/// Settings › Rig › Storage: where frames go, how much room is left, why
/// saving can't work, and the Full UI panel's three disk actions. Open Storage
/// from Plan sets [reveal] to scroll it into view.
class SimpleStorageBlock extends ConsumerStatefulWidget {
  const SimpleStorageBlock({super.key, this.reveal = false});

  final bool reveal;

  @override
  ConsumerState<SimpleStorageBlock> createState() => _SimpleStorageBlockState();
}

class _SimpleStorageBlockState extends ConsumerState<SimpleStorageBlock> {
  bool _choosing = false;

  /// The open Check disk question, answered in the block.
  Completer<bool>? _checkAsk;

  Future<bool> _askCheck() {
    if (_checkAsk != null) return Future.value(false);
    final ask = Completer<bool>();
    setState(() => _checkAsk = ask);
    return ask.future;
  }

  void _answerCheck(bool go) {
    _checkAsk?.complete(go);
    setState(() => _checkAsk = null);
  }

  void _closeChooser() {
    setState(() => _choosing = false);
    ref.invalidate(storageSpaceProvider);
    ref.invalidate(storageDevicesProvider);
  }

  @override
  void dispose() {
    _checkAsk?.complete(false);
    super.dispose();
  }

  @override
  void initState() {
    super.initState();
    if (widget.reveal) {
      WidgetsBinding.instance.addPostFrameCallback((_) {
        if (mounted) Scrollable.ensureVisible(context);
      });
    }
  }

  static String _size(int bytes) {
    final gb = bytes / (1000 * 1000 * 1000);
    return gb >= 1000
        ? '${(gb / 1000).toStringAsFixed(2)} TB'
        : '${gb.toStringAsFixed(gb >= 10 ? 0 : 1)} GB';
  }

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final space = ref.watch(storageSpaceProvider);
    final devicesRead = ref.watch(storageDevicesProvider);
    final devices = devicesRead.asData?.value;
    final value = space.asData?.value;
    final reading = value == null && space.isLoading && !space.hasError;
    // Only a missing server answers null: a failed read still has a rig.
    final noRig = value == null && !reading && !space.hasError;
    final connected = !reading && !noRig;
    final store = devices?.where((d) => d.isAraStore).firstOrNull;
    final name =
        store?.friendlyName ??
        (space.hasError || devices == null
            ? l10n.shellStorageUnknownDisk
            : l10n.shellStorageInternal);
    final free = value?.freeBytes;
    final total = value?.totalBytes;
    final hasFigures = free != null && total != null && total > 0;
    // The one reason saving can't work right now, said in red.
    final problem = space.hasError
        ? l10n.shellStorageUnreadable
        : value != null && !hasFigures
        ? l10n.shellStorageNotPlugged
        : hasFigures && free <= 0
        ? l10n.shellStorageFull
        : null;
    final warnBytes =
        ref.watch(storageSettingsProvider).minFreeDiskWarnGb * 1000000000;
    return Column(
      key: const Key('simple-storage'),
      crossAxisAlignment: CrossAxisAlignment.start,
      spacing: AraSpace.s8,
      children: [
        Text(
          l10n.shellStorageSavedTo,
          style: kSimpleText.copyWith(color: AraColors.textSecondary),
        ),
        Text(
          noRig
              ? l10n.shellRigNotConnected
              : reading || (devices == null && devicesRead.isLoading)
              ? l10n.shellStorageReading
              : name,
          style: kSimpleText.copyWith(
            fontSize: 20,
            fontWeight: FontWeight.w600,
          ),
        ),
        if (hasFigures) ...[
          ClipRRect(
            borderRadius: BorderRadius.circular(AraRadius.field),
            child: LinearProgressIndicator(
              value: (1 - free / total).clamp(0.0, 1.0),
              minHeight: 8,
              color: problem != null
                  ? AraColors.accentError
                  : free <= warnBytes
                  ? AraColors.accentWarning
                  : null,
              backgroundColor: AraColors.textSecondary.withValues(alpha: 0.2),
            ),
          ),
          Text(
            l10n.shellStorageFreeOf(_size(free), _size(total)),
            style: kSimpleText,
          ),
        ],
        if (problem != null)
          Text(
            problem,
            key: const Key('simple-storage-problem'),
            style: kSimpleText.copyWith(color: AraColors.errorText),
          ),
        SizedBox(
          width: double.infinity,
          child: FilledButton(
            key: const Key('simple-storage-choose'),
            style: FilledButton.styleFrom(minimumSize: const Size(0, 44)),
            onPressed: connected
                ? () => _choosing
                      ? _closeChooser()
                      : setState(() => _choosing = true)
                : null,
            child: Text(l10n.shellStorageChoose),
          ),
        ),
        if (_choosing && connected)
          SimpleHostedText(
            child: StorageDriveChooser(
              key: const Key('simple-storage-chooser'),
              onClose: _closeChooser,
            ),
          ),
        Row(
          spacing: AraSpace.s8,
          children: [
            Expanded(
              child: StorageEjectButton(
                key: const Key('simple-storage-eject'),
                label: l10n.shellStorageEject,
                enabled: connected,
                minHeight: 44,
              ),
            ),
            Expanded(
              child: StorageCheckDiskButton(
                key: const Key('simple-storage-check'),
                label: l10n.shellStorageCheck,
                enabled: connected,
                minHeight: 44,
                confirm: _askCheck,
              ),
            ),
          ],
        ),
        if (_checkAsk != null)
          InlineConfirm(
            id: 'simple-storage-check-ask',
            title: l10n.storageCheckTitle,
            body: Text(l10n.storageCheckBody),
            confirmLabel: l10n.shellStorageCheck,
            cancelLabel: l10n.planCancel,
            style: kSimpleText,
            onConfirm: () => _answerCheck(true),
            onCancel: () => _answerCheck(false),
          ),
      ],
    );
  }
}
