import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../services/dso_catalog_service.dart';
import '../../state/equipment/camera_state.dart';
import '../../state/equipment/focuser_state.dart';
import '../../state/equipment/mount_state.dart';
import '../../state/guider/guider_state.dart';
import '../../state/library/live_library_state.dart';
import '../../state/sky_atlas/dso_catalog_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import 'simple_plan_panel.dart' show SimpleSuggestion;
import 'simple_shell.dart'
    show SimplePanel, SimpleModeScreen, SimpleModeScreenLabels;

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

/// Simple Mode's settings by section: (title, section id, other words for
/// it). Camera, Mount, Focuser and Guider rows only count while that device is
/// there.
List<(String, String, String)> _settings(AppLocalizations l) => [
  (l.searchSectionRig, 'Rig', l.searchWordsRig),
  (l.searchSettingAperture, 'Rig', l.searchWordsAperture),
  (l.searchSettingFocalLength, 'Rig', l.searchWordsFocalLength),
  (l.shellGuideFocalLength, 'Rig', l.searchWordsGuideScope),
  (l.shellGuidePixelSize, 'Rig', l.searchWordsGuideScope),
  (l.searchSettingLatitude, 'Rig', l.searchWordsSite),
  (l.searchSettingLongitude, 'Rig', l.searchWordsSite),
  (l.searchSettingElevation, 'Rig', l.searchWordsElevation),
  (l.searchSettingLocationTime, 'Rig', l.searchWordsLocationTime),
  (l.searchSettingPermanentPier, 'Rig', l.searchWordsPermanentPier),
  (l.searchSettingSimpleMode, 'Rig', l.searchWordsSimpleMode),
  (l.searchSettingChangeProfile, 'Rig', l.searchWordsChangeProfile),
  (l.searchSettingChangeRig, 'Rig', l.searchWordsChangeRig),
  (l.searchSettingVersions, 'Rig', l.searchWordsVersions),
  (l.searchSettingLogs, 'Rig', l.searchWordsLogs),
  (l.searchSettingStorage, 'Rig', l.searchWordsStorage),
  (l.searchSectionDevices, 'Devices', l.searchWordsDevices),
  (l.searchSettingConnect, 'Devices', l.searchWordsDevices),
  (l.searchSettingDisconnect, 'Devices', l.searchWordsDevices),
  (l.searchSettingReconnect, 'Devices', l.searchWordsDevices),
  (l.searchSettingCooler, 'Camera', l.searchWordsCooler),
  (l.searchSettingCoolerTarget, 'Camera', l.searchWordsCooler),
  (l.searchSettingGain, 'Camera', l.searchWordsGain),
  (l.searchSettingTracking, 'Mount', l.searchWordsTracking),
  (l.searchSettingPark, 'Mount', l.searchWordsPark),
  (l.searchSettingMeridianFlip, 'Mount', l.searchWordsMeridianFlip),
  (l.searchSectionFocuser, 'Focuser', l.searchWordsFocuser),
  (l.searchSettingFocuserStep, 'Focuser', l.searchWordsFocuserStep),
  (l.searchSectionGuider, 'Guider', l.searchWordsGuider),
  // Dither lives in Guide mode, not the Guide Camera window (user, 2026-11-05).
  (l.searchSettingDither, 'Guide', l.searchWordsDither),
];

/// A Settings section id's name in [l]'s language.
String _sectionName(AppLocalizations l, String section) => switch (section) {
  'Devices' => l.searchSectionDevices,
  'Camera' => l.searchSectionCamera,
  'Mount' => l.searchSectionMount,
  'Focuser' => l.searchSectionFocuser,
  'Guider' => l.searchSectionGuider,
  'Guide' => l.shellModeGuide,
  _ => l.searchSectionRig,
};

/// The hits for [query] among Simple Mode's settings, modes and imaged
/// targets (the sky's come from the catalog search), at most [limit] of
/// each; nothing under two characters. Settings and modes are matched in
/// [l10n]'s language. Pure — unit-tested.
List<SimpleSearchHit> simpleSearch(
  AppLocalizations l10n,
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
      for (final (title, section, words) in _settings(l10n))
        if (sections.contains(section) && (hit(title) || hit(words)))
          SimpleSettingHit(title, section),
    ].take(limit),
    for (final m in SimpleModeScreen.values)
      if (hit(m.labelOf(l10n))) SimpleModeHit(m),
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
            // A long target name wraps to two lines; the detail gives way.
            Flexible(
              flex: 3,
              child: Text(
                title,
                style: kSimpleText.copyWith(fontWeight: FontWeight.w600),
                maxLines: 2,
                overflow: TextOverflow.ellipsis,
              ),
            ),
            if (detail != null)
              Flexible(
                flex: 2,
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
    final l10n = context.l10n;
    final q = _query.text;
    final sky = searchSuggestions(
      ref.watch(bundledCatalogProvider).value ?? const [],
      q,
      limit: 5,
    );
    final hits = simpleSearch(
      l10n,
      q,
      sections: {
        // Only a device appearing or going rebuilds this, not each reading.
        'Rig',
        'Devices',
        if (ref.watch(cameraStatusProvider.select((v) => v.value != null)))
          'Camera',
        if (ref.watch(mountProvider.select((v) => v.value != null))) 'Mount',
        if (ref.watch(focuserProvider.select((v) => v.value != null)))
          'Focuser',
        if (ref.watch(guiderStatusProvider.select((v) => v.value != null))) ...[
          'Guider',
          'Guide',
        ],
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
    return SimplePanel(
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
            decoration: InputDecoration(
              hintText: l10n.searchHint,
              prefixIcon: const Icon(Icons.search, size: 20),
            ),
          ),
          // What each keystroke found, for screen readers (WCAG 4.1.3).
          if (q.trim().length >= 2)
            Semantics(
              key: const Key('simple-search-status'),
              container: true,
              liveRegion: true,
              label: none
                  ? l10n.searchNoMatch(q.trim())
                  : l10n.searchMatches(
                      sky.length +
                          settings.length +
                          modes.length +
                          images.length,
                    ),
              child: const SizedBox(height: 1),
            ),
          Flexible(
            child: ListView(
              shrinkWrap: true,
              children: [
                if (sky.isNotEmpty) _group(l10n.searchGroupSky),
                for (final (i, s) in sky.indexed)
                  SimpleSuggestion(
                    key: Key('simple-search-sky-$i'),
                    hit: s,
                    onTap: () => widget.onHit(SimpleSkyHit(s)),
                  ),
                if (settings.isNotEmpty) _group(l10n.searchGroupSettings),
                for (final (i, s) in settings.indexed)
                  _row(
                    'simple-search-setting-$i',
                    Icons.tune,
                    s.title,
                    s.title == _sectionName(l10n, s.section)
                        ? null
                        : _sectionName(l10n, s.section),
                    s,
                  ),
                if (modes.isNotEmpty) _group(l10n.searchGroupModes),
                for (final (i, m) in modes.indexed)
                  _row(
                    'simple-search-mode-$i',
                    m.mode.icon,
                    m.mode.labelOf(context.l10n),
                    null,
                    m,
                  ),
                if (images.isNotEmpty) _group(l10n.searchGroupImages),
                for (final (i, t) in images.indexed)
                  _row(
                    'simple-search-image-$i',
                    Icons.photo_library_outlined,
                    t.target,
                    l10n.searchImageLibrary,
                    t,
                  ),
                if (none)
                  // The status above already says it.
                  ExcludeSemantics(
                    child: Padding(
                      padding: const EdgeInsets.all(AraSpace.s12),
                      child: Text(
                        l10n.searchNoMatch(q.trim()),
                        style: kSimpleText.copyWith(
                          color: AraColors.textSecondary,
                        ),
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
