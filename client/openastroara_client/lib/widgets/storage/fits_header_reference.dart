import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/settings/filenames_settings_state.dart';
import '../../state/settings/optics_settings_state.dart';
import '../../state/settings/site_settings_state.dart';
import '../../theme/ara_colors.dart';

/// §29.2 — the full FITS header, spelled out. The panel's switches say what
/// each group is *for*; this sheet says exactly what lands in the file:
/// every keyword, its long name, and a real example — so "powerful" never
/// slides into "mysterious". The keyword names themselves are the FITS
/// standard's 8-character convention (fits.gsfc.nasa.gov); the long names
/// are ours.
class FitsHeaderEntry {
  const FitsHeaderEntry(this.keyword, this.longName, this.example);

  final String keyword;
  final String longName;
  final String example;
}

class FitsHeaderGroup {
  const FitsHeaderGroup({
    required this.title,
    required this.controlledBy,
    required this.entries,
  });

  final String title;

  /// The switch that controls this group, or null for the essentials.
  final String? controlledBy;
  final List<FitsHeaderEntry> entries;
}

const List<FitsHeaderGroup> fitsHeaderReference = [
  FitsHeaderGroup(
    title: 'The essentials — always written',
    controlledBy: null,
    entries: [
      FitsHeaderEntry('IMAGETYP', 'Frame type', 'LIGHT'),
      FitsHeaderEntry('EXPTIME', 'Exposure time in seconds', '180.0'),
      FitsHeaderEntry('DATE-OBS', 'When the exposure started (UTC)',
          '2026-08-04T03:14:00.000'),
      FitsHeaderEntry('GAIN', 'Camera gain', '100'),
      FitsHeaderEntry('OFFSET', 'Camera offset (pedestal)', '30'),
      FitsHeaderEntry('XBINNING', 'Binning, horizontal', '1'),
      FitsHeaderEntry('YBINNING', 'Binning, vertical', '1'),
      FitsHeaderEntry('FILTER', 'Filter in the light path', 'L'),
      FitsHeaderEntry('FOCUSPOS', 'Focuser position in steps', '10877'),
      FitsHeaderEntry('BAYERPAT', 'Color sensor mosaic pattern', 'RGGB'),
      FitsHeaderEntry('INSTRUME', 'Camera model', 'ZWO ASI2600MM Pro'),
      FitsHeaderEntry('SWCREATE', 'Software that took the frame',
          'OpenAstro Ara'),
      FitsHeaderEntry('OBJECT', 'Target name (when a target is set)', 'M 31'),
    ],
  ),
  FitsHeaderGroup(
    title: 'Who took it',
    controlledBy: 'Who took it',
    entries: [
      FitsHeaderEntry('OBSERVER', 'Observer name', 'Jane Doe'),
      FitsHeaderEntry('TELESCOP', 'Telescope name', 'RedCat 51'),
    ],
  ),
  FitsHeaderGroup(
    title: 'Your site',
    controlledBy: 'Your site',
    entries: [
      FitsHeaderEntry('SITELAT', 'Observatory latitude in degrees', '32.780278'),
      FitsHeaderEntry(
          'SITELONG', 'Observatory longitude in degrees (east+)', '-105.820278'),
      FitsHeaderEntry('SITEELEV', 'Observatory elevation in meters', '2788.0'),
    ],
  ),
  FitsHeaderGroup(
    title: 'Optics',
    controlledBy: 'Optics',
    entries: [
      FitsHeaderEntry(
          'FOCALLEN', 'Effective focal length in millimeters', '448.0'),
      FitsHeaderEntry('APTDIA', 'Aperture diameter in millimeters', '91.0'),
      FitsHeaderEntry(
          'XPIXSZ', 'Pixel width in microns, after binning', '3.76'),
      FitsHeaderEntry(
          'YPIXSZ', 'Pixel height in microns, after binning', '3.76'),
    ],
  ),
  FitsHeaderGroup(
    title: 'Sensor temperature',
    controlledBy: 'Sensor temperature',
    entries: [
      FitsHeaderEntry('CCD-TEMP', 'Sensor temperature in °C', '-10.2'),
      FitsHeaderEntry('SET-TEMP', 'Cooler set point in °C', '-10.0'),
    ],
  ),
  FitsHeaderGroup(
    title: 'Sky & weather — when a station is connected',
    controlledBy: 'Sky & weather',
    entries: [
      FitsHeaderEntry('SQM', 'Sky quality in magnitudes per arcsec²', '21.3'),
      FitsHeaderEntry('SKYTEMP', 'Sky temperature in °C', '-25.0'),
      FitsHeaderEntry('AMBTEMP', 'Ambient temperature in °C', '12.5'),
      FitsHeaderEntry('HUMIDITY', 'Relative humidity in percent', '45.0'),
      FitsHeaderEntry('DEWPOINT', 'Dew point in °C', '3.2'),
      FitsHeaderEntry('PRESSURE', 'Barometric pressure in hPa', '998.6'),
      FitsHeaderEntry('WINDSPD', 'Wind speed in meters per second', '3.1'),
      FitsHeaderEntry('WINDGUST', 'Wind gust in meters per second', '6.8'),
      FitsHeaderEntry('WINDDIR', 'Wind direction in degrees', '225.0'),
      FitsHeaderEntry('CLOUDCVR', 'Cloud cover in percent', '0.0'),
    ],
  ),
  FitsHeaderGroup(
    title: 'Sun & moon — computed for your site',
    controlledBy: 'Sun & moon',
    entries: [
      FitsHeaderEntry('SUNALT', 'Sun altitude in degrees', '-32.5'),
      FitsHeaderEntry('MOONALT', 'Moon altitude in degrees', '19.5'),
      FitsHeaderEntry('MOONILL', 'Moon illumination in percent', '65.4'),
      FitsHeaderEntry('MOONPHSE', 'Moon phase by name', 'Waning Gibbous'),
    ],
  ),
];

/// Opens the reference as a scrollable sheet.
Future<void> showFitsHeaderReference(BuildContext context) => showDialog<void>(
      context: context,
      builder: (_) => const _ReferenceDialog(),
    );

class _ReferenceDialog extends ConsumerWidget {
  const _ReferenceDialog();

  /// Keyword → the user's own value, for the headers that come straight from
  /// settings (already hydrated by the panel that opens this sheet). Anything
  /// unset stays on the catalog's generic example.
  Map<String, String> _liveValues(WidgetRef ref) {
    final site = ref.watch(siteSettingsProvider);
    final optics = ref.watch(opticsSettingsProvider);
    final live = <String, String>{};
    if (site.observerName.isNotEmpty) live['OBSERVER'] = site.observerName;
    if (optics.telescopeName.isNotEmpty) {
      live['TELESCOP'] = optics.telescopeName;
    }
    if (site.latitudeDeg != 0 || site.longitudeDeg != 0) {
      live['SITELAT'] = site.latitudeDeg.toStringAsFixed(6);
      live['SITELONG'] = site.longitudeDeg.toStringAsFixed(6);
      live['SITEELEV'] = site.elevationM.toStringAsFixed(1);
    }
    if (optics.focalLengthMm > 0) {
      final reducer = optics.reducerFactor > 0 ? optics.reducerFactor : 1.0;
      live['FOCALLEN'] = (optics.focalLengthMm * reducer).toStringAsFixed(1);
    }
    if (optics.apertureMm > 0) {
      live['APTDIA'] = optics.apertureMm.toStringAsFixed(1);
    }
    if (optics.pixelSizeUm > 0) {
      live['XPIXSZ'] = optics.pixelSizeUm.toString();
      live['YPIXSZ'] = optics.pixelSizeUm.toString();
    }
    return live;
  }

  /// Group title → whether its switch is currently on. The essentials
  /// (controlledBy == null) are always on.
  bool _groupEnabled(FilenamesSettings fs, FitsHeaderGroup group) =>
      switch (group.controlledBy) {
        null => true,
        'Who took it' => fs.headerIdentity,
        'Your site' => fs.headerSite,
        'Optics' => fs.headerOptics,
        'Sensor temperature' => fs.headerTemperature,
        'Sky & weather' => fs.headerWeather,
        'Sun & moon' => fs.headerEphemeris,
        // A group this hand-maintained map doesn't know renders as OFF —
        // a visibly-dimmed group gets fixed; a silently-"on" one would lie.
        _ => false,
      };

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final live = _liveValues(ref);
    final fs = ref.watch(filenamesSettingsProvider);
    return Dialog(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 640, maxHeight: 620),
        child: Column(
          children: [
            Padding(
              padding: const EdgeInsets.fromLTRB(20, 16, 10, 10),
              child: Row(
                children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text('Everything a frame can carry',
                            style: theme.textTheme.titleMedium
                                ?.copyWith(fontWeight: FontWeight.w600)),
                        const SizedBox(height: 2),
                        Text(
                          'The FITS header travels inside the file — stackers, '
                          'plate solvers and archives read it decades later. '
                          'Values in white are yours, from your settings; the '
                          'rest are examples measured at capture time.',
                          style: theme.textTheme.bodySmall
                              ?.copyWith(color: AraColors.textSecondary),
                        ),
                      ],
                    ),
                  ),
                  IconButton(
                    icon: const Icon(Icons.close, size: 16),
                    tooltip: 'Close',
                    onPressed: () => Navigator.of(context).pop(),
                  ),
                ],
              ),
            ),
            const Divider(height: 1),
            Expanded(
              child: ListView(
                padding: const EdgeInsets.fromLTRB(20, 8, 20, 20),
                children: [
                  for (final group in fitsHeaderReference) ...[
                    Padding(
                      padding: const EdgeInsets.only(top: 14, bottom: 6),
                      child: Row(
                        children: [
                          Expanded(
                            child: Text(group.title.toUpperCase(),
                                style: theme.textTheme.labelSmall?.copyWith(
                                    color: AraColors.textDisabled,
                                    fontSize: 10,
                                    letterSpacing: 0.8,
                                    fontWeight: FontWeight.w600)),
                          ),
                          if (group.controlledBy != null)
                            Text(
                                _groupEnabled(fs, group)
                                    ? 'switch: ${group.controlledBy}'
                                    : 'off — not written',
                                style: theme.textTheme.labelSmall?.copyWith(
                                    color: _groupEnabled(fs, group)
                                        ? AraColors.textDisabled
                                        : AraColors.accentBusy,
                                    fontSize: 10)),
                        ],
                      ),
                    ),
                    // A group whose switch is off renders dimmed and struck
                    // through — those keywords will not land in the file.
                    Opacity(
                      opacity: _groupEnabled(fs, group) ? 1.0 : 0.35,
                      child: Column(
                        children: [
                          for (final e in group.entries)
                            Padding(
                              padding:
                                  const EdgeInsets.symmetric(vertical: 3),
                              child: Row(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                children: [
                                  SizedBox(
                                    width: 92,
                                    child: Text(e.keyword,
                                        style: theme.textTheme.bodySmall
                                            ?.copyWith(
                                                fontFamily: 'monospace',
                                                fontWeight: FontWeight.w600,
                                                decoration:
                                                    _groupEnabled(fs, group)
                                                        ? null
                                                        : TextDecoration
                                                            .lineThrough)),
                                  ),
                                  Expanded(
                                    child: Text(e.longName,
                                        style: theme.textTheme.bodySmall),
                                  ),
                                  const SizedBox(width: 12),
                                  // An off group shows the generic example,
                                  // not the user's value — "off — not
                                  // written" and real data side by side
                                  // would contradict each other.
                                  Text(
                                      _groupEnabled(fs, group)
                                          ? (live[e.keyword] ?? e.example)
                                          : e.example,
                                      style: theme.textTheme.bodySmall
                                          ?.copyWith(
                                              fontFamily: 'monospace',
                                              color: _groupEnabled(fs, group) &&
                                                      live.containsKey(
                                                          e.keyword)
                                                  ? null
                                                  : AraColors.textSecondary)),
                                ],
                              ),
                            ),
                        ],
                      ),
                    ),
                  ],
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}
