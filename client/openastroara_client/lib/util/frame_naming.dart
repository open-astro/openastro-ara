/// §29.2 — the structured face of the filename template.
///
/// The server speaks the inherited `$$TOKEN$$` language (and now actually
/// names files with it). Nobody should have to *write* that language, so the
/// UI edits this model instead: a folder scheme plus a set of name parts.
/// The model compiles to a template string for the server, and recognizes its
/// own output when it reads one back. A template it can't recognize — a
/// hand-written NINA import, say — flips the panel into custom mode where the
/// raw string is edited directly. Nothing is ever lost in translation:
/// unrecognized means untouched.
library;

/// How frames are grouped into folders on the disk.
enum FolderScheme {
  /// `2026-08-03 / Light / …` — the default: one folder per night, split by
  /// frame type so calibration never mixes into the lights.
  nightAndType,

  /// `2026-08-03 / M 31 / Light / …` — nights first, then what you shot.
  nightTargetType,

  /// `M 31 / 2026-08-03 / Light / …` — a folder per project, nights inside.
  targetNightType,

  /// Everything flat in the save folder.
  none,
}

/// One optional ingredient of the filename. Order here is the order in the
/// name — fixed on purpose: fewer decisions, and every Ara library reads the
/// same way.
enum NamePart { target, filter, exposure, sensorTemp, gain, frameNumber }

/// The structured template. [dateTime] is not modeled — every name starts
/// with `$$DATETIME$$`; it is what makes names unique and sortable, so it is
/// not optional.
class FrameNamingModel {
  const FrameNamingModel({
    this.folders = FolderScheme.nightAndType,
    this.parts = const {NamePart.filter, NamePart.exposure},
  });

  final FolderScheme folders;
  final Set<NamePart> parts;

  FrameNamingModel copyWith({FolderScheme? folders, Set<NamePart>? parts}) =>
      FrameNamingModel(
          folders: folders ?? this.folders, parts: parts ?? this.parts);

  FrameNamingModel toggle(NamePart part, bool on) {
    final next = Set<NamePart>.from(parts);
    on ? next.add(part) : next.remove(part);
    return copyWith(parts: next);
  }

  /// The exact string the server stores and expands.
  String compile() {
    final folderTokens = switch (folders) {
      FolderScheme.nightAndType => [r'$$DATEMINUS12$$', r'$$IMAGETYPE$$'],
      FolderScheme.nightTargetType => [
          r'$$DATEMINUS12$$', r'$$TARGETNAME$$', r'$$IMAGETYPE$$'
        ],
      FolderScheme.targetNightType => [
          r'$$TARGETNAME$$', r'$$DATEMINUS12$$', r'$$IMAGETYPE$$'
        ],
      FolderScheme.none => <String>[],
    };
    final nameTokens = [
      r'$$DATETIME$$',
      for (final part in NamePart.values)
        if (parts.contains(part))
          switch (part) {
            NamePart.target => r'$$TARGETNAME$$',
            NamePart.filter => r'$$FILTER$$',
            NamePart.exposure => r'$$EXPOSURETIME$$s',
            NamePart.sensorTemp => r'$$SENSORTEMP$$',
            NamePart.gain => r'g$$GAIN$$',
            NamePart.frameNumber => r'$$FRAMENR$$',
          },
    ];
    return [...folderTokens, nameTokens.join('_')].join(r'\\');
  }

  /// Recognize a template this model could have written (also accepts the
  /// inherited NINA default). Null = custom, edit raw.
  static FrameNamingModel? tryParse(String template) {
    // The NINA-inherited default is the standard scheme in older clothes.
    const ninaDefault =
        r'$$DATEMINUS12$$\\$$IMAGETYPE$$\\$$DATETIME$$_$$FILTER$$_$$EXPOSURETIME$$s';
    if (template.trim() == ninaDefault) {
      return const FrameNamingModel();
    }
    for (final folders in FolderScheme.values) {
      // parts is small: try every subset via bitmask (2^6).
      for (var mask = 0; mask < (1 << NamePart.values.length); mask++) {
        final parts = <NamePart>{
          for (var i = 0; i < NamePart.values.length; i++)
            if (mask & (1 << i) != 0) NamePart.values[i],
        };
        final candidate = FrameNamingModel(folders: folders, parts: parts);
        if (candidate.compile() == template.trim()) return candidate;
      }
    }
    return null;
  }
}

/// Example values for the live preview — one plausible frame from tonight.
class NamingPreviewContext {
  const NamingPreviewContext({
    required this.captured,
    this.target = 'M 31',
    this.filter = 'L',
    this.exposureSec = 180,
    this.sensorTemp = -10,
    this.gain = 100,
    this.frameNumber = 42,
  });

  final DateTime captured;
  final String target;
  final String filter;
  final double exposureSec;
  final int sensorTemp;
  final int gain;
  final int frameNumber;
}

/// Mirror of the server's expander, for the preview only — the server's
/// expansion is the one that names real files.
List<String> previewSegments(String template, NamingPreviewContext ctx) {
  String two(int n) => n.toString().padLeft(2, '0');
  String date(DateTime t) => '${t.year}-${two(t.month)}-${two(t.day)}';
  final t = ctx.captured;
  final night = t.subtract(const Duration(hours: 12));
  var expanded = template
      .replaceAll(r'$$DATEMINUS12$$', date(night))
      .replaceAll(r'$$DATETIME$$',
          '${date(t)}_${two(t.hour)}-${two(t.minute)}-${two(t.second)}')
      .replaceAll(r'$$DATE$$', date(t))
      .replaceAll(r'$$TIME$$', '${two(t.hour)}-${two(t.minute)}-${two(t.second)}')
      .replaceAll(r'$$IMAGETYPE$$', 'Light')
      .replaceAll(r'$$TARGETNAME$$', ctx.target)
      .replaceAll(r'$$FILTER$$', ctx.filter)
      .replaceAll(
          r'$$EXPOSURETIME$$',
          ctx.exposureSec == ctx.exposureSec.roundToDouble()
              ? ctx.exposureSec.round().toString()
              : ctx.exposureSec.toString())
      .replaceAll(r'$$SENSORTEMP$$', '${ctx.sensorTemp}C')
      .replaceAll(r'$$GAIN$$', '${ctx.gain}')
      .replaceAll(r'$$OFFSET$$', '30')
      .replaceAll(r'$$BINNING$$', '1x1')
      .replaceAll(r'$$CAMERA$$', 'ASI2600MM')
      .replaceAll(r'$$FRAMENR$$', ctx.frameNumber.toString().padLeft(4, '0'));
  // Any token we didn't recognize vanishes, same as the server.
  expanded = expanded.replaceAll(RegExp(r'\$\$[A-Za-z0-9]*\$\$'), '');
  final segments = expanded
      .replaceAll(r'\\', '/')
      .split('/')
      .map((s) {
        var v = s;
        while (v.contains('__')) {
          v = v.replaceAll('__', '_');
        }
        return v.replaceAll(RegExp(r'^[_\-. ]+|[_\-. ]+$'), '');
      })
      .where((s) => s.isNotEmpty)
      .toList();
  if (segments.isNotEmpty) {
    segments[segments.length - 1] = '${segments.last}.fits';
  }
  return segments;
}
