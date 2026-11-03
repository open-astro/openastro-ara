import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/dso_catalog_service.dart';
import '../../state/equipment/camera_state.dart';
import '../../state/equipment/mount_state.dart';
import '../../state/library/live_library_state.dart';
import '../../state/sky_atlas/dso_catalog_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import 'simple_plan_panel.dart' show SimpleSuggestion;
import 'simple_preview_panel.dart' show kSimpleText;
import 'simple_shell.dart'
    show SimpleFrostedPanel, SimpleModeScreen, SimpleModeScreenLabels;

/// What a search hit opens, all inside Simple Mode: an object in the sky, a
/// Settings section, a mode, or the Image Library on a target.
sealed class SimpleSearchHit {
  const SimpleSearchHit();
}

class SimpleSkyHit extends SimpleSearchHit {
  const SimpleSkyHit(this.suggestion);
  final DsoSuggestion suggestion;
}

class SimpleSettingHit extends SimpleSearchHit {
  const SimpleSettingHit(this.title, this.section);
  final String title;

  /// The Settings sheet's section it lives in ("Rig", "Camera"…).
  final String section;
}

class SimpleModeHit extends SimpleSearchHit {
  const SimpleModeHit(this.mode);
  final SimpleModeScreen mode;
}

class SimpleImageHit extends SimpleSearchHit {
  const SimpleImageHit(this.target);
  final String target;
}

/// Simple Mode's settings by section: (title, section, other words for it).
/// Camera and Mount rows only count while that device is there.
const _kSettings = <(String, String, String)>[
  ('Rig', 'Rig', 'system profile'),
  ('Aperture', 'Rig', 'telescope diameter'),
  ('Focal length', 'Rig', 'telescope'),
  ('Latitude', 'Rig', 'site location coordinates'),
  ('Longitude', 'Rig', 'site location coordinates'),
  ('Elevation', 'Rig', 'site altitude height'),
  ('Location and time', 'Rig', 'gps clock time zone site'),
  ('Permanent pier', 'Rig', 'polar alignment observatory'),
  ('Simple Mode', 'Rig', 'full ui advanced'),
  ('Change profile', 'Rig', 'profile'),
  ('Change rig', 'Rig', 'server'),
  ('Versions', 'Rig', 'version about firmware driver'),
  ('Logs & bug report', 'Rig', 'support logs'),
  ('Devices', 'Devices', 'equipment gear'),
  ('Connect', 'Devices', 'equipment gear'),
  ('Disconnect', 'Devices', 'equipment gear'),
  ('Reconnect', 'Devices', 'equipment gear'),
  ('Cooler', 'Camera', 'camera temperature cooling'),
  ('Cooler target', 'Camera', 'camera temperature cooling'),
  ('Gain', 'Camera', 'camera iso hcg'),
  ('Tracking', 'Mount', 'mount sidereal'),
  ('Park', 'Mount', 'mount unpark'),
  ('Meridian flip', 'Mount', 'mount pier side'),
];

/// The hits for [query] among Simple Mode's settings, modes and imaged
/// targets (the sky's come from the catalog search), at most [limit] of
/// each; nothing under two characters. Pure — unit-tested.
List<SimpleSearchHit> simpleSearch(
  String query, {
  Set<String> sections = const {'Rig', 'Devices'},
  Iterable<String> targets = const [],
  int limit = 5,
}) {
  final q = query.trim().toLowerCase();
  if (q.length < 2) return const [];
  bool hit(String s) => s.toLowerCase().contains(q);
  return [
    ...[
      for (final (title, section, words) in _kSettings)
        if (sections.contains(section) && (hit(title) || hit(words)))
          SimpleSettingHit(title, section),
    ].take(limit),
    for (final m in SimpleModeScreen.values)
      if (hit(m.label)) SimpleModeHit(m),
    ...{
      for (final t in targets)
        if (hit(t)) t,
    }.take(limit).map(SimpleImageHit.new),
  ];
}

/// The search panel under the strip: one field, then what matches in the
/// sky, in Settings, among the modes and in the Image Library. Nothing in it
/// leads out of Simple Mode.
class SimpleSearchPanel extends ConsumerStatefulWidget {
  const SimpleSearchPanel({super.key, required this.onHit});

  final ValueChanged<SimpleSearchHit> onHit;

  @override
  ConsumerState<SimpleSearchPanel> createState() => _SimpleSearchPanelState();
}

class _SimpleSearchPanelState extends ConsumerState<SimpleSearchPanel> {
  final _query = TextEditingController();

  @override
  void dispose() {
    _query.dispose();
    super.dispose();
  }

  Widget _group(String title) => Padding(
    padding: const EdgeInsets.fromLTRB(
      AraSpace.s12,
      AraSpace.s8,
      AraSpace.s12,
      AraSpace.s4,
    ),
    child: Text(
      title,
      style: kSimpleText.copyWith(
        fontSize: 12,
        color: AraColors.textSecondary,
        fontWeight: FontWeight.w600,
      ),
    ),
  );

  Widget _row(
    String key,
    IconData icon,
    String title,
    String? detail,
    SimpleSearchHit hit,
  ) => InkWell(
    key: Key(key),
    borderRadius: BorderRadius.circular(8),
    onTap: () => widget.onHit(hit),
    child: ConstrainedBox(
      constraints: const BoxConstraints(minHeight: 44),
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: AraSpace.s12),
        child: Row(
          spacing: AraSpace.s12,
          children: [
            Icon(icon, size: 18, color: AraColors.textSecondary),
            Text(
              title,
              style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
            ),
            if (detail != null)
              Expanded(
                child: Text(
                  detail,
                  style: kSimpleText.copyWith(color: AraColors.textSecondary),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                ),
              ),
          ],
        ),
      ),
    ),
  );

  @override
  Widget build(BuildContext context) {
    final q = _query.text;
    final sky = searchSuggestions(
      ref.watch(bundledCatalogProvider).value ?? const [],
      q,
      limit: 5,
    );
    final hits = simpleSearch(
      q,
      sections: {
        'Rig',
        'Devices',
        if (ref.watch(cameraStatusProvider).value != null) 'Camera',
        if (ref.watch(mountProvider).value != null) 'Mount',
      },
      // The library is only asked once there is something to look for.
      targets: q.trim().length < 2
          ? const []
          : [
              for (final s
                  in ref.watch(liveLibrarySessionsProvider).value ?? const [])
                s.targetName,
            ],
    );
    final settings = hits.whereType<SimpleSettingHit>().toList();
    final modes = hits.whereType<SimpleModeHit>().toList();
    final images = hits.whereType<SimpleImageHit>().toList();
    final none =
        q.trim().length >= 2 &&
        sky.isEmpty &&
        settings.isEmpty &&
        modes.isEmpty &&
        images.isEmpty;
    return SimpleFrostedPanel(
      key: const Key('simple-search'),
      padding: const EdgeInsets.all(AraSpace.s12),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          TextField(
            key: const Key('simple-search-field'),
            controller: _query,
            autofocus: true,
            style: kSimpleText,
            textInputAction: TextInputAction.search,
            onChanged: (_) => setState(() {}),
            onSubmitted: (_) {
              final first = [
                ...sky.map(SimpleSkyHit.new),
                ...settings,
                ...modes,
                ...images,
              ].firstOrNull;
              if (first != null) widget.onHit(first);
            },
            decoration: const InputDecoration(
              hintText: 'Objects, settings, modes, images',
              prefixIcon: Icon(Icons.search, size: 20),
            ),
          ),
          Flexible(
            child: ListView(
              shrinkWrap: true,
              children: [
                if (sky.isNotEmpty) _group('Sky'),
                for (final (i, s) in sky.indexed)
                  SimpleSuggestion(
                    key: Key('simple-search-sky-$i'),
                    hit: s,
                    onTap: () => widget.onHit(SimpleSkyHit(s)),
                  ),
                if (settings.isNotEmpty) _group('Settings'),
                for (final (i, s) in settings.indexed)
                  _row(
                    'simple-search-setting-$i',
                    Icons.tune,
                    s.title,
                    s.title == s.section ? null : s.section,
                    s,
                  ),
                if (modes.isNotEmpty) _group('Modes'),
                for (final (i, m) in modes.indexed)
                  _row(
                    'simple-search-mode-$i',
                    m.mode.icon,
                    m.mode.label,
                    null,
                    m,
                  ),
                if (images.isNotEmpty) _group('Images'),
                for (final (i, t) in images.indexed)
                  _row(
                    'simple-search-image-$i',
                    Icons.photo_library_outlined,
                    t.target,
                    'Image Library',
                    t,
                  ),
                if (none)
                  Padding(
                    padding: const EdgeInsets.all(AraSpace.s12),
                    child: Text(
                      'Nothing in Simple Mode matches "${q.trim()}".',
                      style: kSimpleText.copyWith(
                        color: AraColors.textSecondary,
                      ),
                    ),
                  ),
              ],
            ),
          ),
        ],
      ),
    );
  }
}
