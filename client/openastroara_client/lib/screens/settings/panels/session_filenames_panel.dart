import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/settings/filenames_settings_state.dart';
import '../../../state/settings/optics_settings_state.dart';
import '../../../state/settings/panel_save_registry.dart';
import '../../../state/settings/site_settings_state.dart';
import '../../../state/settings/storage_settings_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../util/frame_naming.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/section_panel.dart';
import '../../../widgets/settings/settings_row.dart';
import '../../../widgets/storage/fits_header_reference.dart';

/// §29.2 File naming. The old panel was a wall of `$$TOKEN$$` strings copied
/// from NINA — a programming language where a preference belongs. This one
/// shows what tonight's frame will be called and offers the choices that
/// matter: how folders are organized, and what goes in the name. The template
/// string still exists underneath (the server expands it at capture time, and
/// imported NINA profiles keep working) but nobody has to read it — it lives
/// behind Advanced, and a hand-written template the builder doesn't recognize
/// simply stays as it is.
class SessionFilenamesPanel extends ConsumerStatefulWidget {
  const SessionFilenamesPanel({super.key});

  @override
  ConsumerState<SessionFilenamesPanel> createState() =>
      _SessionFilenamesPanelState();
}

class _SessionFilenamesPanelState extends ConsumerState<SessionFilenamesPanel>
    with PanelSaveRegistration, SectionPanel {
  String? _lastError;

  // The template rides with storage settings; the separator + compression
  // toggles ride with filenames; the telescope name rides with optics; the
  // observer name rides with the site. All are one save.
  @override
  List<PanelSection> get sections => [
    storageSettingsProvider,
    filenamesSettingsProvider,
    opticsSettingsProvider,
    siteSettingsProvider,
  ];

  @override
  Future<void> panelSave() async {
    setState(() => _lastError = null);
    final error = await saveSections();
    if (mounted) setState(() => _lastError = error);
  }

  @override
  Widget build(BuildContext context) {
    final gate = sectionGate();
    if (gate != null) return gate;
    final fs = ref.watch(filenamesSettingsProvider).value!;
    final fn = ref.read(filenamesSettingsProvider.notifier);
    final sn = ref.read(storageSettingsProvider.notifier);
    final ss = ref.watch(storageSettingsProvider).value!;
    final site = ref.watch(siteSettingsProvider).value!;
    final optics = ref.watch(opticsSettingsProvider).value!;
    final model = FrameNamingModel.tryParse(ss.filenameTemplate);

    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        ?sectionConflict(),
        _PreviewCard(
          template: ss.filenameTemplate,
          extension: ss.fileFormat == StorageFileFormat.xisf
              ? '.xisf'
              : '.fits',
        ),
        const SizedBox(height: 20),
        if (model != null) ...[
          const SettingsSectionHeader('Organize into folders'),
          SettingsDropdownRow<FolderScheme>(
            label: 'Folders',
            helpKey: 'session.filenames.folders',
            value: model.folders,
            items: const {
              FolderScheme.nightAndType: 'By night, then frame type',
              FolderScheme.nightTargetType: 'By night, then target',
              FolderScheme.targetNightType: 'By target, then night',
              FolderScheme.none: 'No folders',
            },
            onChanged: (v) {
              if (v != null) {
                sn.setFilenameTemplate(model.copyWith(folders: v).compile());
              }
            },
          ),
          const SettingsSectionHeader('Include in each name'),
          const SettingsRow(
            label: 'Date & time',
            helpKey: 'session.filenames.datetime_token',
            value: 'Always',
            hint: 'What keeps every name unique and sorted',
          ),
          for (final part in NamePart.values)
            SettingsSwitchRow(
              label: switch (part) {
                NamePart.target => 'Target',
                NamePart.filter => 'Filter',
                NamePart.exposure => 'Exposure',
                NamePart.sensorTemp => 'Sensor temperature',
                NamePart.gain => 'Gain',
                NamePart.frameNumber => 'Frame number',
              },
              value: model.parts.contains(part),
              onChanged: (on) =>
                  sn.setFilenameTemplate(model.toggle(part, on).compile()),
            ),
        ] else ...[
          // A template the builder doesn't recognize — imported from NINA or
          // hand-written. Respect it: show it, offer the standard as an exit.
          const SettingsSectionHeader('Custom template'),
          Text(
            'This naming template was written by hand (or imported), so the '
            'usual choices are hidden to avoid rewriting it. Edit it below, '
            'or start over with the standard naming.',
            style: Theme.of(context).textTheme.bodySmall
                ?.copyWith(color: AraColors.textSecondary),
          ),
          const SizedBox(height: 8),
          EditableTextRow(
            label: 'Template',
            helpKey: 'session.storage.filename_template',
            currentValue: ss.filenameTemplate,
            getCanonical: () =>
                ref.read(storageSettingsProvider).value?.filenameTemplate ?? '',
            parse: sn.setFilenameTemplate,
            maxLines: 2,
          ),
          Align(
            alignment: Alignment.centerLeft,
            child: TextButton(
              onPressed: () =>
                  sn.setFilenameTemplate(const FrameNamingModel().compile()),
              child: const Text('Use standard naming'),
            ),
          ),
        ],
        SettingsSectionHeader(
          'Written into every frame',
          trailing: TextButton(
            onPressed: () => showFitsHeaderReference(context),
            child: const Text('See every header'),
          ),
        ),
        // §29.2 — the FITS header carries the whole story of the frame:
        // who, through what, from where, under what sky. Most of it is
        // automatic; the two identity lines are set here because they have
        // no hardware to live with.
        EditableTextRow(
          label: 'Observer',
          helpKey: 'session.filenames.observer',
          currentValue: site.observerName,
          getCanonical: () =>
              ref.read(siteSettingsProvider).value?.observerName ?? '',
          parse: (v) =>
              ref.read(siteSettingsProvider.notifier).setObserverName(v),
        ),
        EditableTextRow(
          label: 'Telescope',
          helpKey: 'session.filenames.telescope',
          currentValue: optics.telescopeName,
          getCanonical: () =>
              ref.read(opticsSettingsProvider).value?.telescopeName ?? '',
          parse: (v) =>
              ref.read(opticsSettingsProvider.notifier).setTelescopeName(v),
        ),
        // Each group is a switch, not just a fact: your frames, your call.
        SettingsSwitchRow(
          label: 'Who took it',
          helpKey: 'session.filenames.header_identity',
          value: fs.headerIdentity,
          onChanged: fn.setHeaderIdentity,
          hint: 'The observer and telescope names above',
        ),
        SettingsSwitchRow(
          label: 'Your site',
          helpKey: 'session.filenames.header_site',
          value: fs.headerSite,
          onChanged: fn.setHeaderSite,
          hint: site.latitudeDeg == 0 && site.longitudeDeg == 0
              ? 'Coordinates not set — see Where you observe'
              : '${site.latitudeDeg.toStringAsFixed(4)}°, '
                    '${site.longitudeDeg.toStringAsFixed(4)}°, '
                    '${site.elevationM.round()} m — turn off before sharing '
                    'frames if you don\'t want your location in them',
        ),
        SettingsSwitchRow(
          label: 'Optics',
          helpKey: 'session.filenames.header_optics',
          value: fs.headerOptics,
          onChanged: fn.setHeaderOptics,
          hint: optics.focalLengthMm <= 0
              ? 'Not set — see Imaging → Optics'
              : '${(optics.focalLengthMm * (optics.reducerFactor > 0 ? optics.reducerFactor : 1)).round()} mm'
                    '${optics.apertureMm > 0 ? ' · ${optics.apertureMm.round()} mm aperture' : ''}'
                    '${optics.pixelSizeUm > 0 ? ' · ${optics.pixelSizeUm} µm pixels' : ''}',
        ),
        SettingsSwitchRow(
          label: 'Sensor temperature',
          helpKey: 'session.filenames.header_temperature',
          value: fs.headerTemperature,
          onChanged: fn.setHeaderTemperature,
          hint: 'Cooler set point and actual sensor temperature',
        ),
        SettingsSwitchRow(
          label: 'Sky & weather',
          helpKey: 'session.filenames.header_weather',
          value: fs.headerWeather,
          onChanged: fn.setHeaderWeather,
          hint:
              'Sky quality, ambient, humidity, dew point, pressure, wind, '
              'cloud cover — when a weather station is connected',
        ),
        SettingsSwitchRow(
          label: 'Sun & moon',
          helpKey: 'session.filenames.header_ephemeris',
          value: fs.headerEphemeris,
          onChanged: fn.setHeaderEphemeris,
          hint:
              'Altitude of both, moon phase and illumination — computed '
              'for your site at the moment of capture. Even with Your site '
              'off, these can roughly reveal your latitude',
        ),
        const SettingsRow(
          label: 'Always written',
          helpKey: 'session.filenames.always_written',
          value: 'The essentials',
          hint:
              'Frame type, exposure, gain, filter, binning, capture time, '
              'camera model — what calibration and plate solving need',
        ),
        const SettingsSectionHeader('Format'),
        SettingsRow(
          label: 'File format',
          helpKey: 'session.storage.file_format',
          value: _formatLabel(ss.fileFormat),
          hint: 'Edit in Your night → Storage',
        ),
        SettingsSwitchRow(
          label: 'Compress bias/dark frames',
          helpKey: 'session.filenames.compress_darks_and_bias',
          value: fs.compressDarksAndBias,
          onChanged: fn.setCompressDarksAndBias,
          hint: 'Applies to XISF — lossless; FITS is never compressed',
        ),
        if (model != null)
          ExpansionTile(
            tilePadding: EdgeInsets.zero,
            title: Text(
              'Advanced',
              style: Theme.of(context).textTheme.titleSmall,
            ),
            children: [
              EditableTextRow(
                label: 'Template',
                helpKey: 'session.storage.filename_template',
                currentValue: ss.filenameTemplate,
                getCanonical: () =>
                    ref.read(storageSettingsProvider).value?.filenameTemplate ??
                    '',
                parse: sn.setFilenameTemplate,
                maxLines: 2,
              ),
              SettingsDropdownRow<DateSeparator>(
                label: 'Date separator',
                helpKey: 'session.filenames.date_separator',
                value: fs.dateSeparator,
                items: const {
                  DateSeparator.forwardSlash: '/  (real directories)',
                  DateSeparator.underscore: '_  (flat filenames)',
                  DateSeparator.dash: '-  (flat, Windows-safe)',
                },
                onChanged: (v) {
                  if (v != null) fn.setDateSeparator(v);
                },
              ),
            ],
          ),
        const SizedBox(height: 24),
        if (_lastError != null) ...[
          Semantics(
            liveRegion: true,
            child: Text(
              _lastError!,
              style: TextStyle(color: Theme.of(context).colorScheme.error),
            ),
          ),
          const SizedBox(height: 12),
        ],
      ],
    );
  }

  String _formatLabel(StorageFileFormat f) => switch (f) {
    StorageFileFormat.fits => 'FITS',
    StorageFileFormat.xisf => 'XISF',
  };
}

/// Tonight's frame, named. Folders render as a breadcrumb; the filename gets
/// the emphasis. This is the whole panel's feedback loop — every toggle below
/// changes it immediately.
class _PreviewCard extends StatelessWidget {
  const _PreviewCard({required this.template, required this.extension});

  final String template;
  final String extension;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final segments = previewSegments(
      template,
      NamingPreviewContext(captured: DateTime.now()),
      extension: extension,
    );
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.fromLTRB(18, 14, 18, 16),
      decoration: BoxDecoration(
        color: AraColors.bgPanelAlt,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: AraColors.border),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            'TONIGHT THIS FRAME WOULD BE SAVED AS',
            style: theme.textTheme.labelSmall?.copyWith(
              color: AraColors.textDisabled,
              fontSize: 10,
              letterSpacing: 0.8,
            ),
          ),
          const SizedBox(height: 10),
          if (segments.isEmpty)
            Text(
              'Nothing — the template produces no name. Frames fall back '
              'to their internal id.',
              style: theme.textTheme.bodySmall?.copyWith(
                color: AraColors.accentBusy,
              ),
            )
          else
            Wrap(
              crossAxisAlignment: WrapCrossAlignment.center,
              runSpacing: 4,
              children: [
                for (var i = 0; i < segments.length; i++) ...[
                  if (i > 0)
                    Padding(
                      padding: const EdgeInsets.symmetric(horizontal: 6),
                      child: Icon(
                        Icons.chevron_right,
                        size: 14,
                        color: AraColors.textDisabled,
                      ),
                    ),
                  if (i < segments.length - 1) ...[
                    Icon(
                      Icons.folder_outlined,
                      size: 14,
                      color: AraColors.textSecondary,
                    ),
                    const SizedBox(width: 4),
                    Text(
                      segments[i],
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: AraColors.textSecondary,
                      ),
                    ),
                  ] else
                    Text(
                      segments[i],
                      style: theme.textTheme.bodyMedium?.copyWith(
                        fontFamily: 'monospace',
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                ],
              ],
            ),
        ],
      ),
    );
  }
}
