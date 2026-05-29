import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import '../help/registry.dart';
import '../state/settings/settings_nav.dart';
import '../state/settings/settings_search.dart';
import '../theme/ara_colors.dart';

/// §69 HelpIcon widget. Renders as a small ⓘ glyph.
/// - Tooltip on hover/long-press.
/// - Full modal help sheet on tap.
class HelpIcon extends ConsumerWidget {
  final String helpKey;

  const HelpIcon({super.key, required this.helpKey});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final entry = helpRegistry[helpKey];
    if (entry == null) return const SizedBox.shrink();

    return Padding(
      padding: const EdgeInsets.only(left: 4),
      child: Tooltip(
        message: '${entry.title}: ${entry.body.split('.').first}.',
        child: InkWell(
          onTap: () => _showHelpModal(context, ref, entry),
          borderRadius: BorderRadius.circular(12),
          child: const Padding(
            padding: EdgeInsets.all(4),
            child: Icon(
              Icons.info_outline,
              size: 14,
              color: AraColors.textDisabled,
            ),
          ),
        ),
      ),
    );
  }

  void _showHelpModal(BuildContext context, WidgetRef ref, Help entry) {
    showModalBottomSheet(
      context: context,
      isScrollControlled: true,
      backgroundColor: AraColors.bgPanel,
      shape: const RoundedRectangleBorder(
        borderRadius: BorderRadius.vertical(top: Radius.circular(12)),
      ),
      builder: (context) => DraggableScrollableSheet(
        initialChildSize: 0.4,
        minChildSize: 0.2,
        maxChildSize: 0.8,
        expand: false,
        builder: (context, scrollController) => Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  const Icon(Icons.help_outline, size: 20),
                  const SizedBox(width: 12),
                  Text(entry.title,
                      style: Theme.of(context).textTheme.titleLarge),
                  const Spacer(),
                  IconButton(
                    onPressed: () => Navigator.pop(context),
                    icon: const Icon(Icons.close),
                  ),
                ],
              ),
              const Divider(height: 32),
              Expanded(
                child: SingleChildScrollView(
                  controller: scrollController,
                  child: Text(
                    entry.body,
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: AraColors.textSecondary,
                          height: 1.5,
                        ),
                  ),
                ),
              ),
              if (entry.relatedSettings.isNotEmpty) ...[
                const SizedBox(height: 24),
                Text('Related settings:',
                    style: Theme.of(context).textTheme.labelSmall?.copyWith(
                          color: AraColors.textDisabled,
                        )),
                const SizedBox(height: 8),
                Wrap(
                  spacing: 8,
                  children: [
                    for (final sId in entry.relatedSettings)
                      ActionChip(
                        label: Text(sId),
                        onPressed: () {
                          // Jump to setting logic per §61.
                          final index = buildSearchIndex();
                          final sEntry = index.firstWhere(
                              (e) => e.settingId == sId,
                              orElse: () => const SettingsSearchEntry(
                                  label: '',
                                  groupLabel: '',
                                  keywords: <String>[]));

                          if (sEntry.panelId != null) {
                            final modalContext = context;
                            ref
                                .read(selectedSettingsPanelProvider.notifier)
                                .select(sEntry.panelId!);
                            ref
                                .read(highlightedSettingProvider.notifier)
                                .highlight(sId);
                            Navigator.of(modalContext).pop(); // Close help modal
                          }
                        },
                      ),
                  ],
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}
