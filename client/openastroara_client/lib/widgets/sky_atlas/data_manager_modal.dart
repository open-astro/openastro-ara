import 'package:flutter/material.dart';

import '../../theme/ara_colors.dart';

/// §36.2 Data Manager — 4-tab modal. Phase 12e.1 ships placeholder content
/// per tab (lists the asset categories + sizes from §36.2.1–§36.2.4).
/// Phase 12e.2 wires real download/pause/resume/cancel/remove controls
/// against `/api/v1/data-manager/*` once those endpoints come online.
class DataManagerModal extends StatelessWidget {
  const DataManagerModal({super.key});

  @override
  Widget build(BuildContext context) {
    return DefaultTabController(
      length: 4,
      child: Dialog.fullscreen(
        child: Scaffold(
          appBar: AppBar(
            title: const Text('Data Manager'),
            leading: IconButton(
              icon: const Icon(Icons.close),
              onPressed: () => Navigator.of(context).pop(),
            ),
            bottom: const TabBar(
              tabs: [
                Tab(icon: Icon(Icons.photo_library_outlined), text: 'Sky Imagery'),
                Tab(icon: Icon(Icons.star_outline), text: 'Star Catalogs'),
                Tab(icon: Icon(Icons.image_outlined), text: 'Thumbnails'),
                Tab(icon: Icon(Icons.public), text: 'Solar System'),
              ],
            ),
            actions: [
              Center(
                child: Padding(
                  padding: const EdgeInsets.symmetric(horizontal: 16),
                  child: Text(
                    '0 GB used / 1.2 TB',
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: AraColors.textSecondary,
                        ),
                  ),
                ),
              ),
            ],
          ),
          body: const TabBarView(
            children: [
              _SkyImageryTab(),
              _StarCatalogsTab(),
              _ThumbnailsTab(),
              _SolarSystemTab(),
            ],
          ),
        ),
      ),
    );
  }
}

class _SkyImageryTab extends StatelessWidget {
  const _SkyImageryTab();

  // §36.2.1 — 21 surveys grouped by wavelength. Sizes from the playbook.
  static const List<({String group, List<({String name, String size})> entries})>
      _surveys = [
    (
      group: 'Optical (broadband)',
      entries: [
        (name: 'DSS2 (color)', size: '~47 GB'),
        (name: 'DSS2 blue', size: '~30 GB'),
        (name: 'DSS2 red', size: '~30 GB'),
        (name: 'Mellinger (color)', size: '~4 GB'),
        (name: 'SDSS9', size: '~120 GB'),
        (name: 'PanSTARRS DR1 color', size: '~280 GB'),
        (name: 'DECaPS DR2', size: '~150 GB'),
        (name: 'DESI Legacy DR10', size: '~290 GB'),
      ],
    ),
    (
      group: 'Hα',
      entries: [
        (name: 'Finkbeiner Hα', size: '~8 GB'),
        (name: 'VTSS Hα', size: '~6 GB'),
      ],
    ),
    (
      group: 'Infrared',
      entries: [
        (name: '2MASS (J+H+K)', size: '~38 GB'),
        (name: 'GLIMPSE360', size: '~52 GB'),
        (name: 'Spitzer', size: '~58 GB'),
        (name: 'allWISE', size: '~64 GB'),
        (name: 'IRIS', size: '~7 GB'),
        (name: 'AKARI FIS', size: '~14 GB'),
      ],
    ),
    (
      group: 'Ultraviolet',
      entries: [(name: 'GALEX GR6/7', size: '~16 GB')],
    ),
    (
      group: 'X-ray',
      entries: [
        (name: 'eROSITA DR1', size: '~8 GB'),
        (name: 'XMM-Newton (PN)', size: '~7 GB'),
        (name: 'Chandra', size: '~5 GB'),
      ],
    ),
    (
      group: 'Gamma-ray',
      entries: [(name: 'Fermi', size: '~3 GB')],
    ),
    (
      group: 'Extras',
      entries: [(name: 'Nebula contour vectors', size: '~20 MB')],
    ),
  ];

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        for (final group in _surveys) ...[
          Text(group.group, style: Theme.of(context).textTheme.titleSmall),
          const SizedBox(height: 4),
          ...group.entries.map((s) => _AssetRow(name: s.name, size: s.size)),
          const SizedBox(height: 16),
        ],
      ],
    );
  }
}

class _StarCatalogsTab extends StatelessWidget {
  const _StarCatalogsTab();

  // §36.2.2 — SkySafari-style on-demand catalog downloader.
  static const _entries = [
    (name: 'Tycho-2 brightest subset (~2.5M stars)', size: '~30–50 MB'),
    (name: 'GAIA DR3 brightest subset (~10M stars)', size: '~80–100 MB'),
    (name: 'UCAC4 brightest', size: '~15–25 MB'),
    (name: 'HD designation index', size: '~5 MB'),
    (name: 'HIP designation index', size: '~3 MB'),
    (name: 'Bayer + Flamsteed extensions', size: '~2 MB'),
  ];

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(16),
      children: _entries
          .map((e) => _AssetRow(name: e.name, size: e.size))
          .toList(),
    );
  }
}

class _ThumbnailsTab extends StatelessWidget {
  const _ThumbnailsTab();

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: const [
          _AssetRow(
            name: 'Famous Targets Pack (~500 popular DSOs)',
            size: '~150 MB',
          ),
          SizedBox(height: 16),
          Text(
            'Per-target download — search any DSO and download just that one '
            'preview. Useful for niche or obscure targets the famous pack '
            'doesn\'t include. Wired in Phase 12e.2.',
          ),
        ],
      ),
    );
  }
}

class _SolarSystemTab extends StatelessWidget {
  const _SolarSystemTab();

  @override
  Widget build(BuildContext context) {
    return ListView(
      padding: const EdgeInsets.all(16),
      children: const [
        _AssetRow(
          name: 'Full DE440 ephemerides',
          size: '~50 MB',
          recommendedNote:
              'Required for accurate planet positions in Tonight\'s Sky + '
              'comet motion trails + occultation events.',
        ),
        SizedBox(height: 12),
        _AssetRow(
          name: 'MPC asteroid catalog (bulk)',
          size: 'placeholder',
          recommendedNote: 'Deferred to v0.1.0 per §36.8.',
        ),
      ],
    );
  }
}

class _AssetRow extends StatelessWidget {
  final String name;
  final String size;
  final String? recommendedNote;

  const _AssetRow({
    required this.name,
    required this.size,
    this.recommendedNote,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.center,
        children: [
          const Icon(Icons.check_box_outline_blank,
              size: 18, color: AraColors.textSecondary),
          const SizedBox(width: 8),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(name, style: Theme.of(context).textTheme.bodyMedium),
                if (recommendedNote != null)
                  Text(
                    recommendedNote!,
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: AraColors.textSecondary,
                        ),
                  ),
              ],
            ),
          ),
          const SizedBox(width: 12),
          Text(
            size,
            style: Theme.of(context).textTheme.bodySmall?.copyWith(
                  color: AraColors.textDisabled,
                ),
          ),
          const SizedBox(width: 12),
          TextButton(
            onPressed: null,
            child: const Text('Download'),
          ),
        ],
      ),
    );
  }
}
