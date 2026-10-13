import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../help/registry.dart';
import '../models/sequence/draft_sequence.dart';
import '../models/sequence/sequence_summary.dart';
import '../screens/calibration/calibration_screen.dart';
import '../screens/library/image_library_screen.dart';
import '../screens/stats/stats_dashboard_screen.dart';
import '../screens/wizard/wizard_shell.dart';
import '../state/app_shell_state.dart';
import '../state/sequencer/sequence_list_state.dart';
import '../state/settings/settings_nav.dart';
import '../state/settings/settings_search.dart';
import '../theme/ara_colors.dart';
import 'backup/backup_restore_modal.dart';
import 'help_icon.dart';
import 'notifications/notification_center.dart';
import 'sequencer/sequencer_toolbar.dart';
import 'sky_atlas/session_plan_dialog.dart';

/// §61 ⌘K command palette. Opens as a centered dialog, indexes
/// `settingsTree` via `buildSearchIndex()`, and on enter/click jumps to
/// the matching settings panel (selects the Options tab + sets
/// `selectedSettingsPanelProvider`). §61.10 slice 1 adds tab NAVIGATION
/// hits ("Go to Run", …) that switch the main tab instead.
///
/// Future §61.10 phases extend the corpus further — sequence templates
/// (§38), sky-atlas targets (§36), equipment ops like "Park now", etc.
Future<void> showCommandPalette(BuildContext context) {
  return showDialog(
    context: context,
    barrierColor: Colors.black54,
    builder: (_) => const _CommandPaletteDialog(),
  );
}

class _CommandPaletteDialog extends ConsumerStatefulWidget {
  const _CommandPaletteDialog();

  @override
  ConsumerState<_CommandPaletteDialog> createState() =>
      _CommandPaletteDialogState();
}

class _CommandPaletteDialogState extends ConsumerState<_CommandPaletteDialog> {
  late final TextEditingController _controller;
  late final FocusNode _searchFocus;
  late final List<SettingsSearchEntry> _index;
  List<SettingsSearchEntry> _results = const <SettingsSearchEntry>[];
  int _selectedIndex = 0;

  @override
  void initState() {
    super.initState();
    _controller = TextEditingController();
    _searchFocus = FocusNode();
    _index = buildSearchIndex();
  }

  @override
  void dispose() {
    _controller.dispose();
    _searchFocus.dispose();
    super.dispose();
  }

  void _onChanged(String q) {
    setState(() {
      _results = searchSettings(_index, q);
      _selectedIndex = 0;
    });
  }

  void _navigate(int delta) {
    if (_results.isEmpty) return;
    setState(() {
      _selectedIndex =
          (_selectedIndex + delta).clamp(0, _results.length - 1).toInt();
    });
  }

  void _activate() {
    if (_results.isEmpty) return;
    final entry = _results[_selectedIndex];
    // §68.4 — an informational help hit opens the help sheet instead of
    // navigating. Grab a root context BEFORE popping the palette (this
    // widget's own context dies with the pop); the sheet carries its own
    // Consumer, so no disposed-ref hazard.
    if (entry.helpKey != null) {
      final help = helpRegistry[entry.helpKey!];
      final rootContext = Navigator.of(context, rootNavigator: true).context;
      Navigator.of(context).pop();
      if (help != null) showHelpSheet(rootContext, help);
      return;
    }
    // §61.10 slice 2 — an action hit runs its launcher on the ROOT navigator
    // (this widget's context dies with the pop, same as the help-sheet flow).
    if (entry.actionId != null) {
      final rootContext = Navigator.of(context, rootNavigator: true).context;
      // #1154 r1 — the run helpers await a dialog and read providers after
      // it; by then this widget (and its ref) is gone. Hand them the root
      // container, which outlives the palette.
      final container = ProviderScope.containerOf(context);
      Navigator.of(context).pop();
      _runAction(rootContext, container, entry.actionId!);
      return;
    }
    // §61.10 — a navigation hit just switches the main tab.
    if (entry.tabIndex != null) {
      ref.read(selectedTabIndexProvider.notifier).select(entry.tabIndex!);
      Navigator.of(context).pop();
      return;
    }
    if (entry.panelId != null) {
      ref.read(selectedSettingsPanelProvider.notifier).select(entry.panelId!);
    }
    if (entry.settingId != null) {
      ref.read(highlightedSettingProvider.notifier).highlight(entry.settingId!);
    }
    // Switch to the Options tab so the panel is visible.
    ref.read(selectedTabIndexProvider.notifier).select(kOptionsTabIndex);
    Navigator.of(context).pop();
  }

  /// The Run tab's lifecycle-key gate (a saved, non-draft sequence selected
  /// on a connected rig, no command in flight), with a sentence where the
  /// keys stay silent — ⌘K has no toolbar in view to explain itself.
  static bool _runArmed(BuildContext rootContext, ProviderContainer ref) {
    final id = ref.read(selectedSequenceIdProvider);
    final armed = ref.read(sequenceApiProvider) != null &&
        id != null &&
        !isDraftSequenceId(id) &&
        !ref.read(sequenceCommandBusyProvider);
    if (!armed) {
      ScaffoldMessenger.maybeOf(rootContext)?.showSnackBar(
        const SnackBar(content: Text('Select a saved sequence on a connected rig first.')),
      );
    }
    return armed;
  }

  /// The run state is autoDispose and only the Run tab keeps it alive; when
  /// that tab was never built a bare read sees loading. Hold a listen and
  /// wait for the value before branching (r2 note). A failed load branches on
  /// null, which the helpers treat as "not running".
  static Future<void> _withRunState(
      ProviderContainer ref, Future<void> Function(SequenceRunState? state) then) async {
    final sub = ref.listen(sequenceRunStateProvider, (_, _) {});
    try {
      SequenceRunState? state;
      try {
        state = (await ref.read(sequenceRunStateProvider.future))?.state;
      } catch (_) {
        state = null;
      }
      // The helpers refresh the run state after the command, so the listen
      // must outlive them too.
      await then(state);
    } finally {
      sub.close();
    }
  }

  // The id → launcher switch for the palette's action hits. Mirrors the
  // top-bar buttons in app_shell.dart — same routes, same dialog. The run
  // actions (#1154) go through the Run tab's own helpers, so ⌘K and the key
  // shortcuts take exactly one path to the daemon.
  static void _runAction(BuildContext rootContext, ProviderContainer ref, String actionId) {
    switch (actionId) {
      case 'action.run.start':
        ref.read(selectedTabIndexProvider.notifier).select(kRunTabIndex);
        if (!_runArmed(rootContext, ref)) return;
        unawaited(_withRunState(ref, (state) async {
          if (state?.isAnyPaused ?? false) {
            await promptAndResumeSequence(rootContext, ref.read);
          } else if (state?.isActive ?? false) {
            ScaffoldMessenger.maybeOf(rootContext)?.showSnackBar(
              const SnackBar(content: Text('The sequence is already running — Space pauses it.')),
            );
          } else {
            await preflightAndRunSequence(rootContext, ref.read);
          }
        }));
      case 'action.run.pause':
        ref.read(selectedTabIndexProvider.notifier).select(kRunTabIndex);
        if (!_runArmed(rootContext, ref)) return;
        unawaited(_withRunState(ref, (state) async {
          if (state == SequenceRunState.running) {
            await runSequenceLifecycle(rootContext, ref.read, (api, id) => api.pause(id));
          } else if (state?.isAnyPaused ?? false) {
            await promptAndResumeSequence(rootContext, ref.read);
          } else {
            ScaffoldMessenger.maybeOf(rootContext)?.showSnackBar(
              const SnackBar(content: Text('No sequence is running — ⌘R starts the selected one.')),
            );
          }
        }));
      case 'action.plan.tonight':
        ref.read(selectedTabIndexProvider.notifier).select(kPlanningTabIndex);
        showDialog<void>(
          context: rootContext,
          builder: (_) => const SessionPlanDialog(),
        );
      case 'action.library':
        Navigator.of(rootContext).push(
          MaterialPageRoute<void>(builder: (_) => const ImageLibraryScreen()),
        );
      case 'action.calibration':
        Navigator.of(rootContext).push(
          MaterialPageRoute<void>(builder: (_) => const CalibrationScreen()),
        );
      case 'action.stats':
        Navigator.of(rootContext).push(
          MaterialPageRoute<void>(builder: (_) => const StatsDashboardScreen()),
        );
      case 'action.backup':
        showDialog<void>(
          context: rootContext,
          builder: (_) => const BackupRestoreModal(),
        );
      case 'action.notifications':
        showNotificationCenter(rootContext);
      case 'action.wizard':
        Navigator.of(rootContext).push(
          MaterialPageRoute<void>(
            builder: (_) => const WizardShell(),
            fullscreenDialog: true,
          ),
        );
      default:
        // An index entry naming an unknown action is a programming error, but
        // must not crash the palette in release — it just does nothing.
        assert(false, 'unknown palette action: $actionId');
    }
  }

  @override
  Widget build(BuildContext context) {
    return Dialog(
      alignment: Alignment.topCenter,
      insetPadding: const EdgeInsets.only(top: 80, left: 24, right: 24),
      backgroundColor: AraColors.bgPanel,
      shape:
          RoundedRectangleBorder(borderRadius: BorderRadius.circular(8)),
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 640, maxHeight: 480),
        child: Shortcuts(
          shortcuts: const <ShortcutActivator, Intent>{
            SingleActivator(LogicalKeyboardKey.arrowDown): _MoveIntent(1),
            SingleActivator(LogicalKeyboardKey.arrowUp): _MoveIntent(-1),
            SingleActivator(LogicalKeyboardKey.enter): _ActivateIntent(),
            SingleActivator(LogicalKeyboardKey.escape): _DismissIntent(),
          },
          child: Actions(
            actions: <Type, Action<Intent>>{
              _MoveIntent: CallbackAction<_MoveIntent>(
                onInvoke: (i) {
                  _navigate(i.delta);
                  return null;
                },
              ),
              _ActivateIntent: CallbackAction<_ActivateIntent>(
                onInvoke: (_) {
                  _activate();
                  return null;
                },
              ),
              _DismissIntent: CallbackAction<_DismissIntent>(
                onInvoke: (_) {
                  Navigator.of(context).pop();
                  return null;
                },
              ),
            },
            // No autofocus here: two autofocus nodes in one scope race, and
            // this wrapper used to win — leaving the search field unfocused
            // until clicked. The TextField's own autofocus is the one that
            // must land (type-to-search immediately on open).
            child: Focus(
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  _SearchField(
                    controller: _controller,
                    focusNode: _searchFocus,
                    onChanged: _onChanged,
                  ),
                  const Divider(height: 1),
                  Flexible(
                    child: _results.isEmpty
                        ? _EmptyHint(hasQuery: _controller.text.isNotEmpty)
                        : ListView.builder(
                            itemCount: _results.length,
                            itemBuilder: (_, i) => _ResultRow(
                              entry: _results[i],
                              selected: i == _selectedIndex,
                              onTap: () {
                                setState(() => _selectedIndex = i);
                                _activate();
                              },
                            ),
                          ),
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _SearchField extends StatelessWidget {
  final TextEditingController controller;
  final FocusNode focusNode;
  final ValueChanged<String> onChanged;
  const _SearchField({
    required this.controller,
    required this.focusNode,
    required this.onChanged,
  });

  @override
  Widget build(BuildContext context) {
    // Deliberately unstyled (no fill, no border): the palette's search row is
    // part of the dialog chrome — the theme's boxy filled TextField reads as a
    // form control and its dense prefix icon sits off-baseline from the text.
    return TextField(
      controller: controller,
      focusNode: focusNode,
      autofocus: true,
      onChanged: onChanged,
      style: Theme.of(context).textTheme.bodyLarge,
      textAlignVertical: TextAlignVertical.center,
      decoration: InputDecoration(
        filled: false,
        prefixIcon: const Padding(
          padding: EdgeInsets.only(left: 16, right: 12),
          child: Icon(Icons.search, size: 20, color: AraColors.textSecondary),
        ),
        prefixIconConstraints:
            const BoxConstraints(minWidth: 0, minHeight: 0),
        hintText:
            'Search settings or navigate — try "dither", "park", "go to run"…',
        hintStyle: Theme.of(context)
            .textTheme
            .bodyLarge
            ?.copyWith(color: AraColors.textDisabled),
        border: InputBorder.none,
        enabledBorder: InputBorder.none,
        focusedBorder: InputBorder.none,
        contentPadding: const EdgeInsets.symmetric(vertical: 18),
      ),
    );
  }
}

class _EmptyHint extends StatelessWidget {
  final bool hasQuery;
  const _EmptyHint({required this.hasQuery});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Padding(
      padding: const EdgeInsets.fromLTRB(32, 40, 32, 40),
      child: Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              hasQuery ? Icons.search_off : Icons.manage_search,
              size: 28,
              color: AraColors.textDisabled,
            ),
            const SizedBox(height: 12),
            Text(
              hasQuery
                  ? 'No matches'
                  : 'Search settings and actions',
              style: theme.textTheme.bodyLarge
                  ?.copyWith(color: AraColors.textSecondary),
            ),
            const SizedBox(height: 6),
            ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 420),
              child: Text(
                hasQuery
                    ? 'Try another word — sensor, cooling, dither, park, '
                        'autofocus, plate-solve, file naming, safety, sky '
                        'data, profiles…'
                    : '↑ ↓ to navigate ·  Enter to open  ·  Esc to close  ·  '
                        '⌘K from anywhere',
                textAlign: TextAlign.center,
                style: theme.textTheme.bodySmall
                    ?.copyWith(color: AraColors.textDisabled),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _ResultRow extends StatelessWidget {
  final SettingsSearchEntry entry;
  final bool selected;
  final VoidCallback onTap;
  const _ResultRow({
    required this.entry,
    required this.selected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return InkWell(
      onTap: onTap,
      child: Container(
        color:
            selected ? AraColors.selectionBg.withValues(alpha: 0.25) : null,
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
        child: Row(
          children: [
            Icon(
              entry.actionId != null
                  ? Icons.bolt
                  : entry.tabIndex != null
                      ? Icons.arrow_forward
                      : entry.helpKey != null
                          ? Icons.help_outline
                          : Icons.tune,
              size: 16,
              color: AraColors.textSecondary,
            ),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(entry.label,
                      style: Theme.of(context).textTheme.bodyMedium),
                  Text(
                    '${entry.groupLabel} · ${entry.id}',
                    style: Theme.of(context).textTheme.labelSmall?.copyWith(
                          color: AraColors.textDisabled,
                        ),
                  ),
                  if (selected && entry.description != null)
                    Padding(
                      padding: const EdgeInsets.only(top: 8),
                      child: Text(
                        // Clamp to a two-line PREVIEW with paragraph breaks
                        // collapsed: a §68.4 help hit carries its full
                        // multi-paragraph body as the description (so a body
                        // phrase still matches in search), which must not dump
                        // raw newlines/shell commands into the compact row —
                        // the full text lives in the help sheet it opens.
                        entry.description!.replaceAll(RegExp(r'\s+'), ' '),
                        maxLines: 2,
                        overflow: TextOverflow.ellipsis,
                        style: Theme.of(context).textTheme.bodySmall?.copyWith(
                              color: AraColors.textSecondary,
                              fontStyle: FontStyle.italic,
                            ),
                      ),
                    ),
                  if (selected && entry.relatedSettings.isNotEmpty)
                    Padding(
                      padding: const EdgeInsets.only(top: 8),
                      child: Wrap(
                        spacing: 8,
                        children: [
                          Text('Related:',
                              style: Theme.of(context)
                                  .textTheme
                                  .labelSmall
                                  ?.copyWith(color: AraColors.textDisabled)),
                          for (final rel in entry.relatedSettings)
                            Text(rel,
                                style: Theme.of(context)
                                    .textTheme
                                    .labelSmall
                                    ?.copyWith(color: AraColors.selectionBg)),
                        ],
                      ),
                    ),
                ],
              ),
            ),
            if (selected)
              const Icon(Icons.keyboard_return,
                  size: 14, color: AraColors.textSecondary),
          ],
        ),
      ),
    );
  }
}

class _MoveIntent extends Intent {
  final int delta;
  const _MoveIntent(this.delta);
}

class _ActivateIntent extends Intent {
  const _ActivateIntent();
}

class _DismissIntent extends Intent {
  const _DismissIntent();
}
