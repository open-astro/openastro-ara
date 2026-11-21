/// The v2 sequence format's kind table and the rules that map a NINA export
/// onto it, as data (`docs/specs/sequence-v2.md`, #2026). The NINA importer
/// reads it today; the v2 editor's reader and writer (#2025 part C) read the
/// same rows. `test/util/nina_import_kind_table_test.dart` holds it to the
/// daemon: the checked-in schema and `SequenceKinds.cs` name the same kinds,
/// classes and fields, and a kind or field on one side only fails there.
library;

/// The list a node sits in, and so the engine class family that builds it.
enum SequenceV2Role { container, instruction, condition, trigger }

/// One `kind`: the NINA class it builds ([ninaClass], no namespace), its
/// [role], and the fields beyond the [role]'s common ones ([own]).
class SequenceV2Kind {
  const SequenceV2Kind(
    this.kind,
    this.ninaClass,
    this.role, [
    this.own = const [],
  ]);

  final String kind;
  final String ninaClass;
  final SequenceV2Role role;
  final List<String> own;

  /// Every field a node of this kind may carry, `kind` aside.
  Set<String> get fields => {...commonFields[role]!, ...own};
}

/// The fields every node of a role carries (spec "Common fields").
const commonFields = <SequenceV2Role, List<String>>{
  SequenceV2Role.container: [
    'attempts',
    'conditions',
    'errorBehavior',
    'expanded',
    'items',
    'name',
    'triggers',
  ],
  SequenceV2Role.instruction: ['attempts', 'errorBehavior'],
  SequenceV2Role.condition: [],
  SequenceV2Role.trigger: [],
};

const _c = SequenceV2Role.container;
const _i = SequenceV2Role.instruction;
const _k = SequenceV2Role.condition;
const _t = SequenceV2Role.trigger;

/// Every kind but the placeholder (`unknown`) and `glover-exposure`, in the
/// order of `SequenceKinds.All`.
const sequenceV2Kinds = <SequenceV2Kind>[
  SequenceV2Kind('root', 'SequenceRootContainer', _c),
  SequenceV2Kind('sequential', 'SequentialContainer', _c),
  SequenceV2Kind('parallel', 'ParallelContainer', _c),
  SequenceV2Kind('target', 'DeepSkyObjectContainer', _c, [
    'exposureInfoList',
    'exposureInfoListExpanded',
    'targetId',
  ]),
  SequenceV2Kind('smart-exposure', 'SmartExposure', _c),
  SequenceV2Kind('annotation', 'Annotation', _i, ['text']),
  SequenceV2Kind('wait-for-user', 'WaitForUser', _i, ['text']),
  SequenceV2Kind('wait-for-duration', 'WaitForTimeSpan', _i, ['time']),
  SequenceV2Kind('wait-for-time', 'WaitForTime', _i, [
    'hours',
    'minutes',
    'minutesOffset',
    'provider',
    'seconds',
  ]),
  SequenceV2Kind('wait-for-sun-altitude', 'WaitForSunAltitude', _i, ['data']),
  SequenceV2Kind('wait-until-safe', 'WaitUntilSafe', _i),
  SequenceV2Kind('set-tracking', 'SetTracking', _i, ['trackingMode']),
  SequenceV2Kind('unpark-mount', 'UnparkScope', _i),
  SequenceV2Kind('park-mount', 'ParkScope', _i),
  SequenceV2Kind('find-home-mount', 'FindHome', _i),
  SequenceV2Kind('slew-radec', 'SlewScopeToRaDec', _i, [
    'center',
    'coordinates',
    'inherited',
  ]),
  SequenceV2Kind('slew-altaz', 'SlewScopeToAltAz', _i, ['coordinates']),
  SequenceV2Kind('move-focuser-absolute', 'MoveFocuserAbsolute', _i, [
    'position',
  ]),
  SequenceV2Kind('move-focuser-relative', 'MoveFocuserRelative', _i, [
    'relativePosition',
  ]),
  SequenceV2Kind(
    'move-focuser-by-temperature',
    'MoveFocuserByTemperature',
    _i,
    ['absolute', 'intercept', 'slope'],
  ),
  SequenceV2Kind('capture', 'TakeExposure', _i, [
    'binning',
    'calibrationNight',
    'calibrationTarget',
    'exposureCount',
    'exposureTime',
    'gain',
    'imageType',
    'offset',
  ]),
  SequenceV2Kind('cool-camera', 'CoolCamera', _i, [
    'duration',
    'temperature',
    'waitForTemperature',
  ]),
  SequenceV2Kind('warm-camera', 'WarmCamera', _i, ['duration']),
  SequenceV2Kind('set-usb-limit', 'SetUSBLimit', _i, ['usbLimit']),
  SequenceV2Kind('set-readout-mode', 'SetReadoutMode', _i, ['mode']),
  SequenceV2Kind('dew-heater', 'DewHeater', _i, ['onOff']),
  SequenceV2Kind('start-guiding', 'StartGuiding', _i, [
    'calibrateEachNight',
    'forceCalibration',
  ]),
  SequenceV2Kind('stop-guiding', 'StopGuiding', _i),
  SequenceV2Kind('dither', 'Dither', _i),
  SequenceV2Kind('move-rotator-mechanical', 'MoveRotatorMechanical', _i, [
    'mechanicalPosition',
  ]),
  SequenceV2Kind('set-switch-value', 'SetSwitchValue', _i, [
    'alpacaDeviceNumber',
    'switchIndex',
    'value',
  ]),
  SequenceV2Kind('open-dome-shutter', 'OpenDomeShutter', _i),
  SequenceV2Kind('close-dome-shutter', 'CloseDomeShutter', _i),
  SequenceV2Kind('park-dome', 'ParkDome', _i),
  SequenceV2Kind('find-home-dome', 'FindHomeDome', _i),
  SequenceV2Kind('slew-dome-azimuth', 'SlewDomeAzimuth', _i, [
    'azimuthDegrees',
  ]),
  SequenceV2Kind('enable-dome-sync', 'EnableDomeSynchronization', _i),
  SequenceV2Kind('disable-dome-sync', 'DisableDomeSynchronization', _i),
  SequenceV2Kind('sync-dome', 'SynchronizeDome', _i),
  SequenceV2Kind('switch-filter', 'SwitchFilter', _i, ['filter']),
  SequenceV2Kind('run-autofocus', 'RunAutofocus', _i),
  SequenceV2Kind('center-and-rotate', 'CenterAndRotate', _i, [
    'coordinates',
    'inherited',
    'positionAngle',
  ]),
  SequenceV2Kind('tracking-probe', 'TrackingProbe', _i, [
    'ceilingSeconds',
    'eccentricityTolerance',
    'maxExposure',
    'priorSeconds',
    'rungs',
  ]),
  SequenceV2Kind('flat-panel-flats', 'FlatPanelFlats', _i, [
    'brightness',
    'calibrationNight',
    'calibrationTarget',
    'frameCount',
    'gain',
    'maxExposureSec',
    'minExposureSec',
    'offset',
    'targetAdu',
    'targetAduTolerancePct',
    'targetPercent',
  ]),
  SequenceV2Kind('sky-flats', 'SkyFlats', _i, [
    'calibrationNight',
    'calibrationTarget',
    'frameCount',
    'gain',
    'maxExposureSec',
    'minExposureSec',
    'offset',
    'stopAtMaxAdu',
    'stopAtMinAdu',
    'targetAdu',
    'targetAduTolerancePct',
  ]),
  SequenceV2Kind('open-flat-panel-cover', 'OpenFlatPanelCover', _i),
  SequenceV2Kind('close-flat-panel-cover', 'CloseFlatPanelCover', _i),
  SequenceV2Kind('connect-all-equipment', 'ConnectAllEquipment', _i),
  SequenceV2Kind('connect-equipment', 'ConnectEquipment', _i, [
    'selectedDevice',
  ]),
  SequenceV2Kind('disconnect-all-equipment', 'DisconnectAllEquipment', _i),
  SequenceV2Kind('disconnect-equipment', 'DisconnectEquipment', _i, [
    'selectedDevice',
  ]),
  SequenceV2Kind('switch-profile', 'SwitchProfile', _i, [
    'reconnect',
    'selectedProfileId',
  ]),
  SequenceV2Kind('loop-iterations', 'LoopCondition', _k, [
    'completedIterations',
    'iterations',
  ]),
  SequenceV2Kind('loop-for-duration', 'TimeSpanCondition', _k, [
    'hours',
    'minutes',
    'seconds',
  ]),
  SequenceV2Kind('loop-while-above-horizon', 'AboveHorizonCondition', _k, [
    'data',
    'hasDsoParent',
  ]),
  SequenceV2Kind('loop-while-altitude', 'AltitudeCondition', _k, [
    'data',
    'hasDsoParent',
  ]),
  SequenceV2Kind('loop-until-time', 'TimeCondition', _k, [
    'hours',
    'minutes',
    'minutesOffset',
    'provider',
    'seconds',
  ]),
  SequenceV2Kind('meridian-flip', 'MeridianFlipTrigger', _t, [
    'recalibrateGuider',
    'run',
  ]),
  SequenceV2Kind('dither-after-exposures', 'DitherAfterExposures', _t, [
    'afterExposures',
    'run',
  ]),
  SequenceV2Kind('autofocus-after-exposures', 'AutofocusAfterExposures', _t, [
    'afterExposures',
    'run',
  ]),
  SequenceV2Kind('unguided-exposure-guard', 'UnguidedExposureGuard', _t, [
    'eccentricityTolerance',
    'goodEccentricity',
    'maxExposure',
    'run',
    'rungs',
    'stepDownAfter',
    'stepUpAfter',
  ]),
  SequenceV2Kind('center-after-drift', 'CenterAfterDriftTrigger', _t, [
    'afterExposures',
    'coordinates',
    'distanceArcMinutes',
    'run',
  ]),
  SequenceV2Kind('autofocus-after-time', 'AutofocusAfterTimeTrigger', _t, [
    'amount',
    'run',
  ]),
  SequenceV2Kind(
    'autofocus-after-temperature-change',
    'AutofocusAfterTemperatureChangeTrigger',
    _t,
    ['amount', 'run'],
  ),
  SequenceV2Kind(
    'autofocus-after-hfr-increase',
    'AutofocusAfterHFRIncreaseTrigger',
    _t,
    ['amount', 'run', 'sampleSize'],
  ),
  SequenceV2Kind(
    'autofocus-after-filter-change',
    'AutofocusAfterFilterChange',
    _t,
    ['run'],
  ),
  SequenceV2Kind('reconnect', 'ReconnectTrigger', _t, [
    'run',
    'selectedDevice',
  ]),
];

/// The kind of each NINA class name.
final Map<String, SequenceV2Kind> sequenceV2KindByNinaClass = {
  for (final k in sequenceV2Kinds) k.ninaClass: k,
};

/// NINA classes that are not registered in Ara but are `SequentialContainer`s
/// there, so they import as a `sequential` with the same `Name` (children,
/// conditions and triggers kept).
const ninaAreaContainers = <String>{
  'StartAreaContainer',
  'TargetAreaContainer',
  'EndAreaContainer',
};

/// The namespaces whose classes the kind table names (the NINA and the
/// OpenAstroAra sequencer; the assembly part of a `$type` is not read).
const ninaSequencerNamespaces = <String>[
  'NINA.Sequencer.',
  'OpenAstroAra.Sequencer.',
];

/// The one plugin namespace the importer maps, and the six classes in it that
/// it maps: the same-named Ara kinds, `ReconnectTrigger` to `reconnect`.
const ninaConnectorNamespace = 'NINA.Plugins.Connector.';
const ninaConnectorClasses = <String>{
  'ConnectAllEquipment',
  'ConnectEquipment',
  'DisconnectEquipment',
  'DisconnectAllEquipment',
  'SwitchProfile',
  'ReconnectTrigger',
};

/// A NINA property whose v2 field is not its plain lowerCamelCase (spec
/// "Renamed fields"). `Target` is hoisted into `targets` instead; `Binning`
/// and `Filter` follow [ninaNestedKeys].
const ninaFieldRenames = <String, String>{
  'IsExpanded': 'expanded',
  'TriggerRunner': 'run',
  'SelectedProvider': 'provider',
};

/// The NINA property that holds a target container's target.
const ninaTargetProperty = 'Target';

/// The keys a nested value keeps, by its v2 field: the v2 key by the NINA key
/// with its leading underscore removed. Any other key is dropped.
const ninaNestedKeys = <String, Set<String>>{
  'binning': {'x', 'y'},
  'filter': {
    'name',
    'position',
    'focusOffset',
    'autoFocusExposureTime',
    'autoFocusFilter',
  },
};

/// The keys of a coordinates object in `targets`.
const targetCoordinateKeys = <String, Object>{
  'raHours': 0,
  'raMinutes': 0,
  'raSeconds': 0.0,
  'negativeDec': false,
  'decDegrees': 0,
  'decMinutes': 0,
  'decSeconds': 0.0,
};

/// NINA class of a time provider to its `provider` name (spec "Time sources").
const sequenceV2Providers = <String, String>{
  'TimeProvider': 'clock',
  'SunsetProvider': 'sunset',
  'SunriseProvider': 'sunrise',
  'CivilDuskProvider': 'civil-dusk',
  'CivilDawnProvider': 'civil-dawn',
  'NauticalDuskProvider': 'nautical-dusk',
  'NauticalDawnProvider': 'nautical-dawn',
  'DuskProvider': 'dusk',
  'DawnProvider': 'dawn',
  'MeridianProvider': 'meridian',
};

/// A NINA enum written as a number, by its value: `InstructionErrorBehavior`
/// (spec "Common fields"; the fourth token is `SkipToSequenceEndInstructions`).
const errorBehaviorTokens = <String>[
  'continue-on-error',
  'skip-instruction-set-on-error',
  'abort-on-error',
  'skip-to-sequence-end-instructions',
];

/// `TrackingMode`, by value, as the engine names it.
const trackingModeNames = <String>[
  'Sidereal',
  'Lunar',
  'Solar',
  'King',
  'Custom',
  'Stopped',
];

/// `ComparisonOperator`, by value, as the engine names it.
const comparatorNames = <String>[
  'EQUALS',
  'LessThan',
  'LessThanOrEqual',
  'GreaterThan',
  'GreaterThanOrEqual',
  'NotEqual',
];

/// The engine's deprecation shim: the two inclusive operators fold into the
/// strict ones (`WaitForAltitudeBase.DeprecatedComparator`).
const comparatorFolds = <String, String>{
  'GreaterThanOrEqual': 'GreaterThan',
  'LessThanOrEqual': 'LessThan',
};

/// The NINA properties of the altitude kinds that were once on the node and
/// now live in its `data` (`WaitForAltitudeBase`, `LoopForAltitudeBase`), and
/// the `data` key each one sets. They win over `Data` when a file has both.
const altitudeShims = <String, String>{
  'Altitude': 'offset',
  'AltitudeOffset': 'offset',
  'UserMoonAltitude': 'offset',
  'UserSunAltitude': 'offset',
  'Comparator': 'comparator',
  'Coordinates': 'coordinates',
};

/// The role keys a `selectedDevice` names (`ConnectEquipment.GetMediator`);
/// anything else is a device id of NINA's own.
const rigRoleKeys = <String>{
  'Camera',
  'Filter Wheel',
  'Focuser',
  'Rotator',
  'Telescope',
  'Mount',
  'Guider',
  'Switch',
  'Flat Panel',
  'Weather',
  'Dome',
  'Safety Monitor',
};

/// The v2 name of an engine property: lowerCamelCase, a leading acronym
/// lowercased whole (`RAHours` is `raHours`, `ROI` is `roi`, `USBLimit` is
/// `usbLimit`).
String lowerCamel(String key) {
  bool upper(int c) => c >= 0x41 && c <= 0x5a;
  var run = 0;
  while (run < key.length && upper(key.codeUnitAt(run))) {
    run++;
  }
  final lower = run == key.length || run <= 1 ? run : run - 1;
  return key.substring(0, lower).toLowerCase() + key.substring(lower);
}
