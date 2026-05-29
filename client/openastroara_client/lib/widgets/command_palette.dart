import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/app_shell_state.dart';
import '../state/settings/settings_nav.dart';
import '../state/settings/settings_search.dart';
import '../theme/ara_colors.dart';

/// §61 ⌘K command palette. Opens as a centered dialog, indexes
/// `settingsTree` via `buildSearchIndex()`, and on enter/click jumps to
/// the matching settings panel (selects the Options tab + sets
/// `selectedSettingsPanelProvider`).
///
/// Future phases extend the indexed corpus beyond settings — sequence
/// templates (§38), sky-atlas targets (§36), commands like "Park now",
/// "Open Image Library", etc.
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
  List<SettingsSearchEntry> _results = const [];
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
    ref.read(selectedSettingsPanelProvider.notifier).select(entry.panelId);
    // Switch to the Options tab so the panel is visible.
    ref.read(selectedTabIndexProvider.notifier).select(4);
    Navigator.of(context).pop();
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
            child: Focus(
              autofocus: true,
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
    return Padding(
      padding: const EdgeInsets.all(12),
      child: TextField(
        controller: controller,
        focusNode: focusNode,
        autofocus: true,
        onChanged: onChanged,
        style: Theme.of(context).textTheme.bodyLarge,
        decoration: const InputDecoration(
          prefixIcon: Icon(Icons.search),
          hintText:
              'Search settings — try "dither", "park", "cooler", "filenames"…',
          border: InputBorder.none,
          isDense: true,
        ),
      ),
    );
  }
}

class _EmptyHint extends StatelessWidget {
  final bool hasQuery;
  const _EmptyHint({required this.hasQuery});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(24),
      child: Center(
        child: Text(
          hasQuery
              ? 'No matches. Try a different word — keywords cover sensor / '
                  'cooling / dither / park / autofocus / plate-solve / file '
                  'naming / safety / sky data, etc.'
              : 'Start typing to search settings. ↑↓ to navigate, Enter to '
                  'open, Esc to close.',
          textAlign: TextAlign.center,
          style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                color: AraColors.textSecondary,
              ),
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
            selected ? AraColors.selectionBg.withOpacity(0.25) : null,
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
        child: Row(
          children: [
            const Icon(Icons.tune, size: 16, color: AraColors.textSecondary),
            const SizedBox(width: 12),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(entry.label,
                      style: Theme.of(context).textTheme.bodyMedium),
                  Text(
                    '${entry.groupLabel} · ${entry.panelId}',
                    style: Theme.of(context).textTheme.labelSmall?.copyWith(
                          color: AraColors.textDisabled,
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
