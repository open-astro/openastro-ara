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

  /// §69 hardware-aware help: the CONNECTED device's name (e.g.
  /// "ZWO ASI2600MM Pro" from the Alpaca driver). When the entry carries a
  /// driver note matching this device's vendor, the sheet appends it as
  /// "For your hardware".
  final String? device;

  const HelpIcon({super.key, required this.helpKey, this.device});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final entry = helpRegistry[helpKey];
    if (entry == null) return const SizedBox.shrink();

    return Padding(
      padding: const EdgeInsets.only(left: 4),
      child: Tooltip(
        message: '${entry.title}: ${entry.body.split('.').first}.',
        child: InkWell(
          onTap: () => showHelpSheet(context, entry, device: device),
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
}

/// §69 help sheet, callable from any live context (the HelpIcon tap, or a
/// §68.4 help hit in the command palette AFTER the palette has popped). The
/// sheet carries its own [Consumer] so the related-settings chips never
/// depend on a caller's ref that may be disposed by the time they're tapped.
void showHelpSheet(BuildContext context, Help entry, {String? device}) {
  final driverNote = entry.noteFor(device);
  showModalBottomSheet(
    context: context,
    isScrollControlled: true,
    backgroundColor: AraColors.bgPanel,
    shape: const RoundedRectangleBorder(
      borderRadius: BorderRadius.vertical(top: Radius.circular(12)),
    ),
    builder: (context) => Consumer(
      builder: (context, ref, _) => DraggableScrollableSheet(
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
                  // Expanded (not bare Text + Spacer): a long title must
                  // ellipsize, not overflow the sheet on narrow layouts.
                  Expanded(
                    child: Text(
                      entry.title,
                      overflow: TextOverflow.ellipsis,
                      style: Theme.of(context).textTheme.titleLarge,
                    ),
                  ),
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
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        entry.body,
                        style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: AraColors.textSecondary,
                          height: 1.5,
                        ),
                      ),
                      if (driverNote != null) ...[
                        const SizedBox(height: 16),
                        Container(
                          width: double.infinity,
                          padding: const EdgeInsets.all(12),
                          decoration: BoxDecoration(
                            color: AraColors.accentInfo.withValues(alpha: 0.08),
                            border: Border.all(
                              color: AraColors.accentInfo.withValues(
                                alpha: 0.4,
                              ),
                            ),
                            borderRadius: BorderRadius.circular(6),
                          ),
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Row(
                                children: [
                                  const Icon(
                                    Icons.memory,
                                    size: 14,
                                    color: AraColors.accentInfo,
                                  ),
                                  const SizedBox(width: 6),
                                  Flexible(
                                    child: Text(
                                      'For your $device',
                                      overflow: TextOverflow.ellipsis,
                                      style: Theme.of(context)
                                          .textTheme
                                          .labelMedium
                                          ?.copyWith(
                                            color: AraColors.accentInfo,
                                          ),
                                    ),
                                  ),
                                ],
                              ),
                              const SizedBox(height: 6),
                              Text(
                                driverNote,
                                style: Theme.of(context).textTheme.bodySmall
                                    ?.copyWith(
                                      color: AraColors.textSecondary,
                                      height: 1.5,
                                    ),
                              ),
                            ],
                          ),
                        ),
                      ],
                    ],
                  ),
                ),
              ),
              if (entry.relatedSettings.isNotEmpty) ...[
                const SizedBox(height: 24),
                Text(
                  'Related settings:',
                  style: Theme.of(context).textTheme.labelSmall?.copyWith(
                    color: AraColors.textDisabled,
                  ),
                ),
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
                              keywords: <String>[],
                            ),
                          );

                          if (sEntry.panelId != null) {
                            final modalContext = context;
                            ref
                                .read(selectedSettingsPanelProvider.notifier)
                                .select(sEntry.panelId!);
                            ref
                                .read(highlightedSettingProvider.notifier)
                                .highlight(sId);
                            Navigator.of(
                              modalContext,
                            ).pop(); // Close help modal
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
    ),
  );
}
