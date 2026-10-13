/// Client models for §47 mosaic projects — the daemon's `MosaicDto`,
/// `MosaicPanelDto` and `MosaicProgressDto` (`/api/v1/mosaics`, #1146).
/// Snake_case wire; defensive parse — a malformed field degrades rather than
/// throws, except `id`, which keys the project and drops the row when missing.
library;

double _dbl(dynamic v) => v is num ? v.toDouble() : 0.0;
int _int(dynamic v) => v is num ? v.toInt() : 0;
String _str(dynamic v) => v is String ? v : '';

/// A mosaic project: the plan the planetarium drew, persisted on the daemon.
class Mosaic {
  final String id;
  final String name;
  final double centerRaDeg;
  final double centerDecDeg;
  final int panelCountX;
  final int panelCountY;
  final double overlapPercent;
  final double positionAngleDeg;
  final int totalPanels;
  final DateTime? createdUtc;

  const Mosaic({
    required this.id,
    required this.name,
    this.centerRaDeg = 0,
    this.centerDecDeg = 0,
    this.panelCountX = 1,
    this.panelCountY = 1,
    this.overlapPercent = 0,
    this.positionAngleDeg = 0,
    this.totalPanels = 1,
    this.createdUtc,
  });

  factory Mosaic.fromJson(Map<String, dynamic> json) => Mosaic(
    id: _str(json['id']),
    name: _str(json['name']),
    centerRaDeg: _dbl(json['center_ra_degrees']),
    centerDecDeg: _dbl(json['center_dec_degrees']),
    panelCountX: _int(json['panel_count_x']),
    panelCountY: _int(json['panel_count_y']),
    overlapPercent: _dbl(json['overlap_percent']),
    positionAngleDeg: _dbl(json['position_angle_degrees']),
    totalPanels: _int(json['total_panels']),
    createdUtc: json['created_utc'] is String
        ? DateTime.tryParse(json['created_utc'] as String)?.toUtc()
        : null,
  );

  /// "3×2" for the row chip.
  String get gridLabel => '$panelCountX×$panelCountY';
}

/// Where one panel stands: `pending` (no lights yet), `in_progress`, `done`.
enum MosaicPanelStatus {
  pending,
  inProgress,
  done;

  static MosaicPanelStatus parse(dynamic v) => switch (v) {
    'done' => MosaicPanelStatus.done,
    'in_progress' => MosaicPanelStatus.inProgress,
    _ => MosaicPanelStatus.pending,
  };
}

/// One panel of a mosaic with its derived completion.
class MosaicPanel {
  final int panelIndex;
  final int panelX;
  final int panelY;
  final double centerRaDeg;
  final double centerDecDeg;
  final bool crossesRaWrap;
  final MosaicPanelStatus status;
  final int framesCaptured;

  const MosaicPanel({
    required this.panelIndex,
    this.panelX = 0,
    this.panelY = 0,
    this.centerRaDeg = 0,
    this.centerDecDeg = 0,
    this.crossesRaWrap = false,
    this.status = MosaicPanelStatus.pending,
    this.framesCaptured = 0,
  });

  factory MosaicPanel.fromJson(Map<String, dynamic> json) => MosaicPanel(
    panelIndex: _int(json['panel_index']),
    panelX: _int(json['panel_x']),
    panelY: _int(json['panel_y']),
    centerRaDeg: _dbl(json['center_ra_degrees']),
    centerDecDeg: _dbl(json['center_dec_degrees']),
    crossesRaWrap: json['crosses_ra_wrap'] == true,
    status: MosaicPanelStatus.parse(json['status']),
    framesCaptured: _int(json['frames_captured']),
  );
}

/// The project's progress: panels done, frames owed per panel, frames so far.
class MosaicProgress {
  final int completedPanels;
  final int totalPanels;
  final int? currentPanelIndex;
  final int framesPerPanelTarget;
  final int framesCapturedTotal;

  const MosaicProgress({
    this.completedPanels = 0,
    this.totalPanels = 0,
    this.currentPanelIndex,
    this.framesPerPanelTarget = 0,
    this.framesCapturedTotal = 0,
  });

  factory MosaicProgress.fromJson(Map<String, dynamic> json) => MosaicProgress(
    completedPanels: _int(json['completed_panels']),
    totalPanels: _int(json['total_panels']),
    currentPanelIndex: json['current_panel_index'] is num
        ? (json['current_panel_index'] as num).toInt()
        : null,
    framesPerPanelTarget: _int(json['frames_per_panel_target']),
    framesCapturedTotal: _int(json['frames_captured_total']),
  );

  bool get complete => totalPanels > 0 && completedPanels >= totalPanels;
}

/// A project with its progress and panels, as the Library shows it.
class MosaicProject {
  final Mosaic mosaic;
  final MosaicProgress progress;
  final List<MosaicPanel> panels;

  const MosaicProject({
    required this.mosaic,
    required this.progress,
    this.panels = const [],
  });
}
