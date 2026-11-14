import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:intl/intl.dart';

import '../../services/storage_devices_api.dart';
import '../../services/storage_space_api.dart';
import '../../state/profile_management_state.dart';
import '../../state/saved_server_state.dart';
import '../../state/settings/storage_settings_state.dart';
import '../../state/simple_messages_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../inline_choices.dart';
import '../../util/friendly_error.dart';
import '../../l10n/l10n.dart';
import '../adaptive_controls.dart';

/// §29.1.1/§29.1.3 — "choose a disk for your frames", after the Time Machine
/// model: one list of disks, select, confirm. Erasing (for disks ARA can't use
/// as-is) is a step *inside* this flow, not a separate concept the user has to
/// understand up front. Device paths and filesystem names appear only as
/// secondary detail.
Future<void> showStorageDriveDialog(BuildContext context, WidgetRef ref) async {
  if (ref.read(activeServerProvider) == null) {
    return;
  }
  await showDialog<void>(
    context: context,
    builder: (_) => const StorageDriveChooser(),
  );
  // The caller's element can be gone by the time the dialog closes —
  // touching its ref then throws (the classic Riverpod after-await footgun).
  if (!context.mounted) return;
  ref.invalidate(storageSpaceProvider);
  ref.invalidate(storageDevicesProvider);
}

/// What the disk chooser says when Use Disk fails, keyed on the helper's code.
String storageConfigureMessage(
  AppLocalizations l,
  StorageConfigureOutcome outcome,
  StorageDevice device,
) => switch (outcome.code) {
  'not_ext4' => l.storageConfigureNotExt4,
  'label_mismatch' => l.storageConfigureLabelMismatch,
  'device_busy' when outcome.detail == 'previous_store' =>
    l.storageConfigurePreviousBusy,
  'device_busy' => l.storageConfigureBusy,
  'system_disk' || 'refused' => l.storageConfigureSystemDisk,
  'uuid_not_found' => l.storageConfigureDisconnected,
  'helper_missing' => l.storageConfigureHelperMissing,
  'mkfs_failed' => l.storageConfigureMkfsFailed,
  'mount_failed' => l.storageConfigureMountFailed,
  'exfat_unsupported' => l.storageConfigureExfatUnsupported,
  _ => outcome.detail ?? l.storageConfigureFailed(device.friendlyName),
};

/// The list and its erase step: a dialog's content on its own, or laid out in
/// place when the host passes [onClose] (Simple Mode has no popups).
class StorageDriveChooser extends ConsumerStatefulWidget {
  const StorageDriveChooser({super.key, this.onClose});

  /// Inline: closes it, on Cancel or after Use Disk worked.
  final VoidCallback? onClose;

  @override
  ConsumerState<StorageDriveChooser> createState() =>
      _StorageDriveChooserState();
}

class _StorageDriveChooserState extends ConsumerState<StorageDriveChooser> {
  bool get _inline => widget.onClose != null;

  /// Simple Mode's 44 px floor; a dialog keeps its theme's size.
  Size? get _minSize => _inline ? const Size(0, 44) : null;

  void _close() => _inline ? widget.onClose!() : Navigator.of(context).pop();

  StorageDevice? _selected;
  final TextEditingController _confirm = TextEditingController();
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _confirm.dispose();
    super.dispose();
  }

  /// An unsupported filesystem forces the erase; a mountable drive can also
  /// be erased DELIBERATELY (the ext4 → exFAT migration path).
  bool get _mustErase => _selected != null && !_selected!.isMountable;
  bool _eraseAnyway = false;
  bool get _needsErase => _mustErase || (_selected != null && _eraseAnyway);

  /// Format choice for the erase path. exFAT is the default — the drive
  /// plugs straight into Windows/macOS at home (the §29 field workflow);
  /// ext4 is the rig-resident choice.
  String _newFilesystem = 'exfat';

  /// What erasing the picked disk would lose. A blank disk (no filesystem, no
  /// UUID) holds nothing to read.
  AsyncValue<StorageContents>? get _contents {
    final uuid = _selected?.uuid;
    return _needsErase && uuid != null
        ? ref.watch(storageContentsProvider(uuid))
        : null;
  }

  /// A labelled disk asks the user to type its name — the standard guard
  /// against erasing the wrong one. An unlabelled disk still holds someone's
  /// data (stock USB sticks ship label-less), so it demands typing ERASE:
  /// a destructive click is never one static warning away.
  bool get _confirmSatisfied {
    if (!_needsErase) {
      return true;
    }
    // The user sees what is on the disk before the erase is offered.
    if (_contents?.isLoading ?? false) {
      return false;
    }
    final label = _selected!.label ?? '';
    return label.isEmpty ? _confirm.text == 'ERASE' : _confirm.text == label;
  }

  Future<void> _useDisk() async {
    final device = _selected;
    final server = ref.read(activeServerProvider);
    if (device == null || server == null) {
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    final api = ref.read(storageDevicesApiFactoryProvider)(server);
    try {
      final outcome = await api.configure(
        // A brand-new blank disk has no filesystem yet, hence no UUID — the
        // server accepts its /dev/ path for exactly this onboarding case.
        uuid: device.uuid ?? device.path,
        format: _needsErase,
        // What the user actually typed — the server re-checks it against the
        // drive's real label, so a stale client record can't erase the wrong
        // disk. An unlabelled drive's typed ERASE is a client-side bar only;
        // the server must see the empty label the drive actually has.
        confirmLabel: _needsErase
            ? ((device.label ?? '').isEmpty ? '' : _confirm.text)
            : null,
        filesystem: _needsErase ? _newFilesystem : null,
      );
      if (!mounted) {
        return;
      }
      if (outcome.success) {
        // The daemon wrote the Storage section itself (new save directory,
        // new ETag) and no profile.updated follows this route: reload it, or
        // the next Save sends the old tag and gets a false 412 (ADR 0015).
        final profile = ref.read(profileApiProvider);
        if (profile != null) {
          unawaited(ref.read(storageSettingsProvider.notifier).load(profile));
        }
        final notify = userNotifier(context);
        final done = context.l10n.storageDiskNowUsed(device.friendlyName);
        _close();
        notify(done);
        return;
      }
      final problem = storageConfigureMessage(context.l10n, outcome, device);
      if (_inline) {
        userNotifier(context)(problem, error: true);
      } else {
        setState(() => _error = problem);
      }
    } finally {
      api.close();
      if (mounted) {
        setState(() => _busy = false);
      }
    }
  }

  List<Widget> _contentsLines(
    AsyncValue<StorageContents> contents,
    AppLocalizations l10n,
    ThemeData theme,
  ) {
    final body = theme.textTheme.bodySmall;
    final c = contents.asData?.value;
    if (contents.isLoading) {
      return [Text(l10n.storageDiskContentsChecking, style: body)];
    }
    // A request that failed is not a disk that isn't connected.
    if (contents.hasError) {
      return [Text(l10n.storageDiskContentsReadFailed, style: body)];
    }
    if (c == null || !c.readable) {
      return [Text(l10n.storageDiskContentsUnknown, style: body)];
    }
    if (c.frames == 0) {
      return [Text(l10n.storageDiskContentsNone, style: body)];
    }
    final newest = c.newestFrameUtc;
    return [
      Text(
        newest == null
            ? l10n.storageDiskContentsFramesUndated(c.frames)
            : l10n.storageDiskContentsFrames(
                c.frames,
                DateFormat.yMMMd(l10n.localeName).format(newest.toLocal()),
              ),
        style: body,
      ),
      // What #1579 is for: as loud as the erase heading, never a footnote.
      if (c.framesNotCopied > 0)
        Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          spacing: 6,
          children: [
            const Icon(
              Icons.warning_amber_rounded,
              size: 20,
              color: AraColors.accentBusy,
            ),
            Expanded(
              child: Text(
                l10n.storageDiskContentsNotCopied(c.framesNotCopied),
                style: theme.textTheme.titleSmall?.copyWith(
                  color: AraColors.accentBusy,
                ),
              ),
            ),
          ],
        ),
    ];
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final l10n = context.l10n;
    final async = ref.watch(storageDevicesProvider);
    final body = async.when(
      loading: () => const SizedBox(
        height: 140,
        child: Center(child: AdaptiveProgressIndicator()),
      ),
      error: (e, _) => SizedBox(
        height: 140,
        child: Center(
          child: Text(
            friendlyError(l10n, e, action: l10n.storageDiskReadAction),
            textAlign: TextAlign.center,
          ),
        ),
      ),
      data: (all) {
        final disks = all.where((d) => !d.isSystemDisk).toList();
        // A rescan hands out fresh device objects — re-point the
        // selection at its current record so the erase/label logic never
        // reads pre-rescan state. A disk that vanished deselects.
        final selectedPath = _selected?.path;
        if (selectedPath != null) {
          _selected = null;
          for (final d in disks) {
            if (d.path == selectedPath) {
              _selected = d;
              break;
            }
          }
        }
        if (disks.isEmpty) {
          return SizedBox(
            height: 140,
            child: Center(
              child: Text(
                l10n.storageDiskNoneFound,
                textAlign: TextAlign.center,
              ),
            ),
          );
        }
        return Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Flexible(
              child: ListView.builder(
                shrinkWrap: true,
                // Inline, the Settings page does the scrolling.
                physics: _inline ? const NeverScrollableScrollPhysics() : null,
                itemCount: disks.length,
                itemBuilder: (_, i) {
                  final d = disks[i];
                  final selected =
                      identical(d, _selected) ||
                      (_selected != null && d.path == _selected!.path);
                  return ListTile(
                    selected: selected,
                    // Inline it sits on Simple Mode's tiles: the theme blue reads at 2:1.
                    selectedColor: _inline ? AraColors.linkText : null,
                    selectedTileColor: _inline
                        ? AraColors.selectionTint
                        : AraColors.textSecondary.withValues(alpha: 0.12),
                    // A check too: the tint alone barely shows in Night mode.
                    leading: Icon(
                      selected
                          ? Icons.check_circle
                          : d.removable
                          ? Icons.usb
                          : Icons.storage,
                      size: 28,
                    ),
                    title: Text(d.friendlyName),
                    subtitle: Text(d.detailLine(l10n)),
                    trailing: d.isAraStore
                        ? Text(
                            l10n.storageDiskInUse,
                            style: theme.textTheme.bodySmall,
                          )
                        : null,
                    onTap: _busy
                        ? null
                        : () => setState(() {
                            _selected = d;
                            _confirm.clear();
                            _error = null;
                            _eraseAnyway = false;
                          }),
                  );
                },
              ),
            ),
            // A mountable drive offers the deliberate reformat path —
            // how an existing ext4 store becomes a take-home exFAT one.
            if (_selected != null && !_mustErase) ...[
              const Divider(height: 24),
              SwitchListTile(
                dense: !_inline,
                contentPadding: EdgeInsets.zero,
                title: Text(l10n.storageDiskEraseChange),
                subtitle: Text(
                  l10n.storageDiskEraseChangeNote('${_selected!.fileSystem}'),
                  style: theme.textTheme.bodySmall,
                ),
                value: _eraseAnyway,
                onChanged: _busy
                    ? null
                    : (v) => setState(() {
                        _eraseAnyway = v;
                        _confirm.clear();
                      }),
              ),
            ],
            if (_needsErase) ...[
              // What would be lost comes first, before how to erase.
              if (_contents case final contents?) ...[
                const SizedBox(height: 10),
                ..._contentsLines(contents, l10n, theme),
              ],
              if (_mustErase) ...[
                const Divider(height: 24),
                Text(
                  l10n.storageDiskMustErase,
                  style: theme.textTheme.titleSmall?.copyWith(
                    color: AraColors.accentBusy,
                  ),
                ),
                const SizedBox(height: 6),
                Text(
                  l10n.storageDiskMustEraseNote,
                  style: theme.textTheme.bodySmall,
                ),
              ],
              const SizedBox(height: 8),
              RadioGroup<String>(
                groupValue: _newFilesystem,
                onChanged: (v) => setState(() => _newFilesystem = v ?? 'exfat'),
                child: Column(
                  children: [
                    RadioListTile<String>(
                      value: 'exfat',
                      dense: !_inline,
                      contentPadding: EdgeInsets.zero,
                      title: Text(l10n.storageDiskExfat),
                      subtitle: Text(
                        l10n.storageDiskExfatNote,
                        style: theme.textTheme.bodySmall,
                      ),
                    ),
                    RadioListTile<String>(
                      value: 'ext4',
                      dense: !_inline,
                      contentPadding: EdgeInsets.zero,
                      title: Text(l10n.storageDiskExt4),
                      subtitle: Text(
                        l10n.storageDiskExt4Note,
                        style: theme.textTheme.bodySmall,
                      ),
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 10),
              Text(
                (_selected!.label ?? '').isNotEmpty
                    ? l10n.storageDiskTypeLabel(_selected!.label!)
                    : l10n.storageDiskTypeErase,
                style: theme.textTheme.bodySmall,
              ),
              const SizedBox(height: 4),
              TextField(
                controller: _confirm,
                autofocus: true,
                enabled: !_busy,
                onChanged: (_) => setState(() {}),
                decoration: InputDecoration(isDense: !_inline),
              ),
            ],
            if (_error != null) ...[
              const SizedBox(height: 12),
              Text(
                _error!,
                style: theme.textTheme.bodySmall?.copyWith(
                  color: AraColors.accentBusy,
                ),
              ),
            ],
            if (_busy) ...[
              const SizedBox(height: 12),
              Row(
                children: [
                  const SizedBox(
                    width: 14,
                    height: 14,
                    child: AdaptiveProgressIndicator(strokeWidth: 2),
                  ),
                  const SizedBox(width: 10),
                  Text(
                    _needsErase
                        ? l10n.storageDiskErasing
                        : l10n.storageDiskConnecting,
                    style: theme.textTheme.bodySmall,
                  ),
                ],
              ),
            ],
          ],
        );
      },
    );
    final actions = [
      TextButton(
        style: TextButton.styleFrom(minimumSize: _minSize),
        onPressed: _busy ? null : () => ref.invalidate(storageDevicesProvider),
        child: Text(l10n.storageDiskRescan),
      ),
      TextButton(
        style: TextButton.styleFrom(minimumSize: _minSize),
        onPressed: _busy ? null : _close,
        child: Text(l10n.planCancel),
      ),
      FilledButton(
        style: _needsErase
            ? FilledButton.styleFrom(
                backgroundColor: AraColors.emergencyStop,
                foregroundColor: AraColors.onEmergencyStop,
                minimumSize: _minSize,
              )
            : _inline
            ? FilledButton.styleFrom(minimumSize: _minSize)
            : null,
        // A failed list can't vouch for the disk the confirmation named.
        onPressed:
            _busy ||
                async.hasError ||
                _selected == null ||
                !_selected!.selectable ||
                !_confirmSatisfied
            ? null
            : _useDisk,
        child: Text(
          _needsErase ? l10n.storageDiskEraseUse : l10n.storageDiskUse,
        ),
      ),
    ];
    final title = l10n.storageDiskChooseTitle;
    if (!_inline) {
      return AlertDialog(
        title: Text(title),
        content: SizedBox(width: 520, child: body),
        actions: actions,
      );
    }
    return InlineAttention(
      title: title,
      child: Material(
        type: MaterialType.transparency,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          spacing: AraSpace.s8,
          children: [
            Text(title, style: theme.textTheme.titleSmall),
            body,
            Wrap(
              alignment: WrapAlignment.end,
              spacing: AraSpace.s8,
              children: actions,
            ),
          ],
        ),
      ),
    );
  }
}
