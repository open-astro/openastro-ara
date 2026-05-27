import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/settings/settings_nav.dart';
import '../../theme/ara_colors.dart';
import 'panels/diagnostics_mode_panel.dart';
import 'panels/generic_placeholder_panel.dart';
import 'panels/imaging_defaults_panel.dart';
import 'panels/storage_panel.dart';

/// Settings shell per §25.5.5 — tree on the left, selected panel on the
/// right. Replaces the Phase 12a OptionsTab placeholder. Phase 12h.1 ships
/// 3 fully-rendered panels (Imaging Defaults, Storage, Diagnostics Mode) +
/// placeholders for the rest. Phase 12h.2 fills in the remaining panels +
/// wires persistence; 12h.3 layers the §61 ⌘K smart search on top.
class SettingsShell extends ConsumerWidget {
  const SettingsShell({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final selectedId = ref.watch(selectedSettingsPanelProvider);
    final selectedInfo = findPanelInfo(selectedId);

    return Row(
      children: [
        SizedBox(
          width: 260,
          child: Container(
            decoration: const BoxDecoration(
              color: AraColors.bgPanel,
              border: Border(right: BorderSide(color: AraColors.border)),
            ),
            child: _SettingsTree(
              selectedId: selectedId,
              onSelect: ref
                  .read(selectedSettingsPanelProvider.notifier)
                  .select,
            ),
          ),
        ),
        Expanded(
          child: Column(
            children: [
              _PanelHeader(info: selectedInfo),
              Expanded(child: _PanelBody(panelId: selectedId)),
            ],
          ),
        ),
      ],
    );
  }
}

class _SettingsTree extends StatelessWidget {
  final String selectedId;
  final ValueChanged<String> onSelect;

  const _SettingsTree({required this.selectedId, required this.onSelect});

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.symmetric(vertical: 8),
      children: [
        for (final group in settingsTree) ...[
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
            child: Text(
              group.label,
              style: Theme.of(context).textTheme.labelLarge?.copyWith(
                    color: AraColors.textSecondary,
                    letterSpacing: 0.5,
                  ),
            ),
          ),
          for (final panel in group.panels)
            _PanelRow(
              panel: panel,
              selected: panel.id == selectedId,
              onTap: () => onSelect(panel.id),
            ),
        ],
        const SizedBox(height: 24),
      ],
    );
  }
}

class _PanelRow extends StatelessWidget {
  final SettingsPanelInfo panel;
  final bool selected;
  final VoidCallback onTap;
  const _PanelRow({
    required this.panel,
    required this.selected,
    required this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return InkWell(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.fromLTRB(28, 8, 16, 8),
        color: selected ? AraColors.selectionBg.withValues(alpha: 0.25) : null,
        child: Row(
          children: [
            Expanded(
              child: Text(
                panel.label,
                style: Theme.of(context).textTheme.bodyMedium,
              ),
            ),
            if (selected)
              const Icon(Icons.chevron_right,
                  size: 16, color: AraColors.textSecondary),
          ],
        ),
      ),
    );
  }
}

class _PanelHeader extends StatelessWidget {
  final SettingsPanelInfo? info;
  const _PanelHeader({required this.info});

  @override
  Widget build(BuildContext context) {
    return Container(
      height: 48,
      padding: const EdgeInsets.symmetric(horizontal: 16),
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(bottom: BorderSide(color: AraColors.border)),
      ),
      child: Row(
        children: [
          Text(info?.label ?? 'Settings',
              style: Theme.of(context).textTheme.titleMedium),
          const Spacer(),
          // §61 smart search ⌘K stub — wires up in Phase 12h.3.
          TextButton.icon(
            onPressed: null,
            icon: const Icon(Icons.search, size: 16),
            label: const Text('Search settings (⌘K)'),
          ),
        ],
      ),
    );
  }
}

class _PanelBody extends StatelessWidget {
  final String panelId;
  const _PanelBody({required this.panelId});

  @override
  Widget build(BuildContext context) {
    return switch (panelId) {
      'img.defaults' => const ImagingDefaultsPanel(),
      'session.storage' => const StoragePanel(),
      'safety.diagnostics' => const DiagnosticsModePanel(),
      _ => GenericPlaceholderPanel(
          panelId: panelId,
          label: findPanelInfo(panelId)?.label ?? panelId,
          note:
              'This panel\'s form lands in Phase 12h.2. The settings tree '
              'navigation + Riverpod state are real today; selecting a panel '
              'here is enough to verify the routing.',
        ),
    };
  }
}
