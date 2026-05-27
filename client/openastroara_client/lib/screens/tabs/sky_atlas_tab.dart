import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/sky_atlas/sky_atlas_state.dart';
import '../../theme/ara_colors.dart';
import '../../widgets/sky_atlas/data_manager_modal.dart';
import '../../widgets/sky_atlas/sky_data_missing_banner.dart';

/// Sky Atlas per §25.5.4 + §36. Two modes (Catalog View / Tonight's Sky)
/// toggled at the top, universal search next to the mode toggle, then the
/// Aladin Lite area below. Phase 12e.1 ships the layout + the §36.13
/// sky-data-missing banner + the §36.2 Data Manager modal stub. Phase
/// 12e.2 embeds the real Aladin Lite via `webview_flutter` and wires the
/// universal search backend.
class SkyAtlasTab extends ConsumerWidget {
  const SkyAtlasTab({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final mode = ref.watch(skyAtlasModeProvider);

    return Column(
      children: [
        SkyDataMissingBanner(
          onOpenDataManager: () => _openDataManager(context),
        ),
        _SkyAtlasHeader(
          mode: mode,
          onModeChanged: ref.read(skyAtlasModeProvider.notifier).set,
          onOpenDataManager: () => _openDataManager(context),
        ),
        const _SearchBar(),
        const Expanded(child: _AladinPlaceholder()),
      ],
    );
  }

  void _openDataManager(BuildContext context) {
    showDialog<void>(
      context: context,
      builder: (_) => const DataManagerModal(),
    );
  }
}

class _SkyAtlasHeader extends StatelessWidget {
  final SkyAtlasMode mode;
  final ValueChanged<SkyAtlasMode> onModeChanged;
  final VoidCallback onOpenDataManager;

  const _SkyAtlasHeader({
    required this.mode,
    required this.onModeChanged,
    required this.onOpenDataManager,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      height: 48,
      padding: const EdgeInsets.symmetric(horizontal: 12),
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(bottom: BorderSide(color: AraColors.border)),
      ),
      child: Row(
        children: [
          const Icon(Icons.public, size: 18),
          const SizedBox(width: 8),
          Text('Sky Atlas', style: Theme.of(context).textTheme.titleMedium),
          const SizedBox(width: 16),
          SegmentedButton<SkyAtlasMode>(
            segments: const [
              ButtonSegment(
                value: SkyAtlasMode.catalogView,
                label: Text('Catalog'),
                icon: Icon(Icons.satellite_alt, size: 16),
              ),
              ButtonSegment(
                value: SkyAtlasMode.tonightsSky,
                label: Text('Tonight\'s Sky'),
                icon: Icon(Icons.nights_stay_outlined, size: 16),
              ),
            ],
            selected: {mode},
            onSelectionChanged: (set) => onModeChanged(set.first),
          ),
          const Spacer(),
          TextButton.icon(
            onPressed: onOpenDataManager,
            icon: const Icon(Icons.download_for_offline_outlined, size: 16),
            label: const Text('Data Manager'),
          ),
        ],
      ),
    );
  }
}

class _SearchBar extends ConsumerStatefulWidget {
  const _SearchBar();

  @override
  ConsumerState<_SearchBar> createState() => _SearchBarState();
}

class _SearchBarState extends ConsumerState<_SearchBar> {
  final _ctrl = TextEditingController();

  @override
  void dispose() {
    _ctrl.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(bottom: BorderSide(color: AraColors.border)),
      ),
      child: Row(
        children: [
          Expanded(
            child: TextField(
              controller: _ctrl,
              decoration: const InputDecoration(
                labelText: 'Universal search — name (M42), catalog ID, '
                    'RA/Dec, comet, asteroid',
                isDense: true,
                prefixIcon: Icon(Icons.search, size: 18),
              ),
              onSubmitted: (s) => ref
                  .read(skyAtlasSearchProvider.notifier)
                  .set(s.trim()),
            ),
          ),
        ],
      ),
    );
  }
}

class _AladinPlaceholder extends ConsumerWidget {
  const _AladinPlaceholder();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final mode = ref.watch(skyAtlasModeProvider);
    final query = ref.watch(skyAtlasSearchProvider);

    return Container(
      color: AraColors.bgPrimary,
      child: Center(
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              mode == SkyAtlasMode.catalogView
                  ? Icons.public
                  : Icons.nights_stay_outlined,
              size: 96,
              color: AraColors.textDisabled,
            ),
            const SizedBox(height: 12),
            Text(
              mode == SkyAtlasMode.catalogView
                  ? 'Catalog View — Aladin Lite (CDS) embedding here'
                  : 'Tonight\'s Sky — zenith-centered planetarium view',
              style: Theme.of(context).textTheme.titleMedium,
            ),
            const SizedBox(height: 6),
            Text(
              query.isEmpty
                  ? 'webview_flutter Aladin Lite embed lands in Phase 12e.2'
                  : 'Search query: "$query" — backend lookup in Phase 12e.2',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AraColors.textSecondary,
                  ),
            ),
          ],
        ),
      ),
    );
  }
}
