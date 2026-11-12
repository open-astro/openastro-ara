// ignore: unused_import
import 'package:intl/intl.dart' as intl;

import 'app_localizations.dart';

// ignore_for_file: type=lint

/// The translations for English (`en`).
class AppLocalizationsEn extends AppLocalizations {
  AppLocalizationsEn([String locale = 'en']) : super(locale);

  @override
  String pillsMore(int count) {
    return '+$count more';
  }

  @override
  String get pillsShowFewer => 'Show fewer';

  @override
  String pillsMoreShort(int count) {
    return '+$count';
  }

  @override
  String pillsMoreSemantics(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count more messages',
      one: '1 more message',
    );
    return '$_temp0';
  }

  @override
  String get pillsDismiss => 'Dismiss';

  @override
  String get safetyAlertSemantics => 'Safety alert';

  @override
  String get safetyAlertTitle => 'SAFETY ALERT';

  @override
  String get safetyUnsafe => 'Conditions are UNSAFE.';

  @override
  String get safetyRinging => 'Alarm ringing';

  @override
  String get safetySilenceBefore => 'Silence it before it sounds';

  @override
  String get safetySilence => 'Silence alarm';

  @override
  String get deviceCamera => 'Camera (Main)';

  @override
  String get deviceMount => 'Mount';

  @override
  String get deviceFocuser => 'Focuser';

  @override
  String get deviceGuider => 'Guider';

  @override
  String get deviceConnecting => 'Connecting…';

  @override
  String get deviceNotConnected => 'Not connected';

  @override
  String get deviceCurrentTemperature => 'Sensor temperature';

  @override
  String get deviceCooler => 'Cooler';

  @override
  String get deviceNoCooler => 'No cooler';

  @override
  String get deviceTarget => 'Target temperature';

  @override
  String get deviceCouldntSaveTarget => 'Couldn\'t save the target';

  @override
  String get deviceDewHeater => 'Dew heater';

  @override
  String get deviceCouldntChangeCooler => 'Couldn\'t change the cooler';

  @override
  String get deviceSdk => 'SDK';

  @override
  String get deviceDriver => 'Driver';

  @override
  String get deviceFirmware => 'Firmware';

  @override
  String get deviceSerialNumber => 'Serial number';

  @override
  String get deviceLess => 'Less';

  @override
  String get deviceMore => 'More';

  @override
  String get deviceOn => 'On';

  @override
  String get deviceOff => 'Off';

  @override
  String get deviceVersions => 'Versions';

  @override
  String get deviceTracking => 'Tracking';

  @override
  String get devicePark => 'Park';

  @override
  String get deviceParking => 'Parking…';

  @override
  String get deviceUnparking => 'Unparking…';

  @override
  String get deviceParkUnfinished => 'The mount didn\'t finish parking';

  @override
  String get deviceUnparkUnfinished => 'The mount didn\'t finish unparking';

  @override
  String get deviceParked => 'Parked';

  @override
  String get deviceUnparked => 'Unparked';

  @override
  String get devicePlanRunningParkAnyway =>
      'A plan is running. Park the mount anyway?';

  @override
  String get deviceCancel => 'Cancel';

  @override
  String get deviceParkAnyway => 'Park anyway';

  @override
  String get deviceMeridianFlip => 'Meridian flip';

  @override
  String get deviceAuto => 'Auto';

  @override
  String get deviceCouldntSaveMeridianFlip =>
      'Couldn\'t save the meridian flip';

  @override
  String get deviceCouldntMoveMount => 'Couldn\'t move the mount';

  @override
  String get deviceGain => 'Gain';

  @override
  String get deviceBusy => 'Still busy';

  @override
  String get deviceBusyDetail =>
      'The last command hasn\'t finished. Try again in a moment.';

  @override
  String get deviceCouldntMoveFocuser => 'Couldn\'t move the focuser';

  @override
  String get deviceFilter => 'Filter';

  @override
  String deviceSlot(int position) {
    return 'Slot $position';
  }

  @override
  String deviceSlotFilter(int position, String filter) {
    return '$position · $filter';
  }

  @override
  String get deviceNoFilters => 'This wheel reports no filters.';

  @override
  String get deviceCouldntChangeFilter => 'Couldn\'t change the filter';

  @override
  String deviceSwitchPorts(String device, int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count ports',
      one: '1 port',
    );
    return '$device · $_temp0';
  }

  @override
  String get deviceSensors => 'Sensors';

  @override
  String get deviceNothingToSwitch => 'Nothing to switch';

  @override
  String get deviceModeManual => 'Manual';

  @override
  String get deviceModeSwitch => 'Switch';

  @override
  String get deviceSetAutomatically => 'Set automatically';

  @override
  String deviceSwitchPortOnDevice(String port, String device) {
    return '$port ($device)';
  }

  @override
  String deviceCouldntSetPort(String port) {
    return 'Couldn\'t set $port';
  }

  @override
  String deviceSettingPort(String port) {
    return 'Setting $port…';
  }

  @override
  String get devicePlanRunningChangeFilter =>
      'A plan is running. Change the filter anyway?';

  @override
  String get deviceChangeAnyway => 'Change anyway';

  @override
  String deviceSwapIn(String filter) {
    return 'Swap in $filter, then confirm.';
  }

  @override
  String deviceFilterIsIn(String filter) {
    return '$filter is in';
  }

  @override
  String get deviceCancelSwap => 'Cancel swap';

  @override
  String get deviceCover => 'Cover';

  @override
  String get deviceOpen => 'Open';

  @override
  String get deviceClosed => 'Closed';

  @override
  String get deviceLight => 'Light';

  @override
  String get deviceBrightness => 'Brightness';

  @override
  String get deviceWarmingUp => 'Warming up…';

  @override
  String get deviceCoverMoving => 'Cover moving…';

  @override
  String get deviceLightWarmingUp => 'Light warming up…';

  @override
  String get devicePlanRunningChangeFlatPanel =>
      'A plan is running. Change the flat panel anyway?';

  @override
  String get deviceCouldntChangeFlatPanel => 'Couldn\'t change the flat panel';

  @override
  String get shellSafetyMonitor => 'Safety monitor';

  @override
  String get deviceShutter => 'Shutter';

  @override
  String get deviceShutterMoving => 'Shutter moving…';

  @override
  String get deviceAzimuth => 'Azimuth';

  @override
  String get deviceAtHome => 'At home';

  @override
  String get deviceHome => 'Home';

  @override
  String get deviceStop => 'Stop';

  @override
  String get deviceStopDome => 'Stop the dome';

  @override
  String get devicePlanRunningChangeDome =>
      'A plan is running. Change the dome anyway?';

  @override
  String get deviceCouldntMoveDome => 'Couldn\'t move the dome';

  @override
  String get deviceCouldntStopDome => 'Couldn\'t stop the dome';

  @override
  String get deviceFollowsMount => 'Follows the mount';

  @override
  String get deviceFollowingMount => 'Following the mount…';

  @override
  String get deviceStoppedFollowingMount =>
      'The dome no longer follows the mount';

  @override
  String get deviceCouldntFollowMount =>
      'Couldn\'t change whether the dome follows the mount';

  @override
  String get domePanelFollowingHeader => 'Following the mount';

  @override
  String get domePanelReadingSettings => 'Reading…';

  @override
  String get domePanelCouldntSave => 'Couldn\'t save the dome';

  @override
  String get domePanelFollowNote =>
      'With this on, Ara turns the dome so its slit stays in front of the telescope: after a mount slew, after a meridian flip, and while tracking whenever the slit falls behind by more than the tolerance. Turning the dome by hand, parking it, sending it home, stopping it or disconnecting it turns this off, and so do an emergency stop, an unsafe-weather reaction and a geometry that puts the telescope outside the dome. The dome must be able to turn to an azimuth.';

  @override
  String get domePanelFollowMount => 'Follow the mount';

  @override
  String get domePanelFollowNeedsAzimuth =>
      'Connect a dome that can turn to an azimuth first';

  @override
  String get domePanelGeometryHeader => 'Geometry';

  @override
  String get domePanelMountType => 'Mount type';

  @override
  String get domePanelMountGermanEquatorial => 'German equatorial';

  @override
  String get domePanelMountForkOnWedge => 'Fork or alt-az (on a wedge)';

  @override
  String get domePanelDomeRadius => 'Dome radius (mm)';

  @override
  String get domePanelScopeNorth => 'Scope north of dome center (mm)';

  @override
  String get domePanelScopeEast => 'Scope east of dome center (mm)';

  @override
  String get domePanelScopeAbove => 'Scope above dome center (mm)';

  @override
  String get domePanelMountAxisLength => 'Mount axis length (mm)';

  @override
  String get domePanelMountLateralOffset => 'Mount lateral offset (mm)';

  @override
  String get domePanelDecAxisOffset => 'Dec axis horizontal offset (mm)';

  @override
  String get domePanelAzimuthTolerance => 'Azimuth tolerance (°)';

  @override
  String get domePanelTurnDuringSlews => 'Turn during mount slews';

  @override
  String get domePanelTurnDuringSlewsHint =>
      'Off: the dome turns when the mount has finished slewing';

  @override
  String get domePanelSlewing => 'Slewing…';

  @override
  String get deviceDomeReadFailed => 'Dome read failed — check the device.';

  @override
  String get deviceSafe => 'Safe';

  @override
  String get deviceUnsafe => 'Unsafe';

  @override
  String deviceSafetySince(String time, String duration) {
    return 'since $time · $duration';
  }

  @override
  String deviceUnsafeSince(String time) {
    return 'Unsafe since $time';
  }

  @override
  String deviceResumesAfterSafe(int minutes) {
    return 'Resumes $minutes min after Safe';
  }

  @override
  String get deviceStaysPaused => 'Stays paused until you resume';

  @override
  String get deviceOpening => 'Opening…';

  @override
  String get deviceClosing => 'Closing…';

  @override
  String get deviceTurn => 'Turn';

  @override
  String deviceTurnTo(String angle) {
    return 'Turn to $angle';
  }

  @override
  String get deviceShutterFault => 'Shutter fault: open or close again.';

  @override
  String get deviceFindingHome => 'Finding home…';

  @override
  String deviceTurningTo(String angle) {
    return 'Turning to $angle…';
  }

  @override
  String deviceReasonSentence(String reason) {
    return '$reason.';
  }

  @override
  String get deviceWhy => 'Why';

  @override
  String get deviceWhatAraDoes => 'What Ara does';

  @override
  String deviceMonitorNoReason(String name) {
    return '$name says unsafe. It doesn\'t say why: check the device.';
  }

  @override
  String deviceWindOverLimit(String value, int limit) {
    return 'Wind $value km/h, over the $limit km/h limit';
  }

  @override
  String deviceHumidityOverLimit(String value, int limit) {
    return 'Humidity $value %, over the $limit % limit';
  }

  @override
  String deviceDewUnderMargin(String value, String limit) {
    return 'Dew point $value °C below the air, under the $limit °C margin';
  }

  @override
  String deviceMonitorReadsSafe(String name) {
    return '$name reads Safe';
  }

  @override
  String get deviceOnUnsafePause =>
      'On unsafe, Ara pauses the plan, parks and closes the dome.';

  @override
  String get deviceOnUnsafePark => 'On unsafe, Ara parks; the plan carries on.';

  @override
  String get deviceOnUnsafeAbort => 'On unsafe, Ara stops the plan and parks.';

  @override
  String get deviceOnUnsafeIgnore =>
      'On unsafe, Ara does nothing; imaging carries on.';

  @override
  String get deviceFlatPanelUnconfirmed =>
      'The flat panel didn\'t make that change';

  @override
  String get deviceFlatPanelUnconfirmedDetail =>
      'Check the cover isn\'t jammed and the panel isn\'t busy.';

  @override
  String get deviceSensorReadFailed => 'Sensor read failed — check the device.';

  @override
  String get deviceCloudCover => 'Cloud cover';

  @override
  String get deviceSkyQuality => 'Sky quality';

  @override
  String get deviceDewPoint => 'Dew point';

  @override
  String get deviceHumidity => 'Humidity';

  @override
  String get deviceRain => 'Rain';

  @override
  String get deviceWind => 'Wind';

  @override
  String deviceGusts(String speed) {
    return 'gusts $speed';
  }

  @override
  String deviceCompass(String point) {
    String _temp0 = intl.Intl.selectLogic(point, {
      'n': 'N',
      'ne': 'NE',
      'e': 'E',
      'se': 'SE',
      's': 'S',
      'sw': 'SW',
      'w': 'W',
      'other': 'NW',
    });
    return '$_temp0';
  }

  @override
  String deviceLimit(String value) {
    return 'limit $value';
  }

  @override
  String deviceMargin(String value) {
    return 'margin $value';
  }

  @override
  String get devicePastLimit => 'past the safety limit';

  @override
  String deviceUpdatedAgo(String age) {
    return 'Updated $age ago';
  }

  @override
  String deviceLastReadingAgo(String age) {
    return 'Last reading $age ago';
  }

  @override
  String get deviceMoon => 'Moon';

  @override
  String deviceMoonPhase(String phase) {
    String _temp0 = intl.Intl.selectLogic(phase, {
      'newMoon': 'New moon',
      'waxingCrescent': 'Waxing crescent',
      'firstQuarter': 'First quarter',
      'waxingGibbous': 'Waxing gibbous',
      'fullMoon': 'Full moon',
      'waningGibbous': 'Waning gibbous',
      'lastQuarter': 'Last quarter',
      'other': 'Waning crescent',
    });
    return '$_temp0';
  }

  @override
  String get deviceSafetyLimitsNotLoaded =>
      'Safety limits not loaded: nothing is marked.';

  @override
  String get deviceCouldntSaveThat => 'Couldn\'t save that';

  @override
  String get deviceNever => 'Never';

  @override
  String get deviceRefocusTemp => 'Refocus every';

  @override
  String get devicePosition => 'Position';

  @override
  String get deviceTemperature => 'Temperature';

  @override
  String get deviceMoving => 'Moving…';

  @override
  String get deviceExposing => 'Exposing';

  @override
  String get deviceDownloading => 'Downloading';

  @override
  String get deviceAutofocus => 'Autofocus';

  @override
  String get deviceCancelAutofocus => 'Cancel autofocus';

  @override
  String get deviceIn => 'In';

  @override
  String get deviceOut => 'Out';

  @override
  String get deviceCoarse => 'Coarse';

  @override
  String get deviceFine => 'Fine';

  @override
  String get deviceCoarseFocus => 'Coarse focus';

  @override
  String get deviceFineFocus => 'Fine focus';

  @override
  String get deviceRotate => 'Rotate';

  @override
  String deviceRotateTo(String angle) {
    return 'Rotate to $angle';
  }

  @override
  String get deviceSync => 'Sync';

  @override
  String deviceSyncTo(String angle) {
    return 'Sync to $angle';
  }

  @override
  String get deviceCouldntSyncRotator => 'Couldn\'t sync the rotator';

  @override
  String get devicePlanRunningSync => 'A plan is running. Sync anyway?';

  @override
  String get deviceSyncAnyway => 'Sync anyway';

  @override
  String get deviceSkyAngle => 'Sky angle';

  @override
  String get deviceMechanicalAngle => 'Mechanical angle';

  @override
  String get deviceCouldntMoveRotator => 'Couldn\'t move the rotator';

  @override
  String get deviceCouldntStopRotator => 'Couldn\'t stop the rotator';

  @override
  String get deviceStopRotator => 'Stop the rotator';

  @override
  String get devicePlanRunningRotate => 'A plan is running. Rotate anyway?';

  @override
  String get deviceRotateAnyway => 'Rotate anyway';

  @override
  String get deviceKnobHint => 'Increase moves out, decrease moves in';

  @override
  String get deviceState => 'State';

  @override
  String get deviceRms => 'RMS';

  @override
  String get deviceDither => 'Dither';

  @override
  String get deviceDitherBy => 'Dither by';

  @override
  String devicePowerPercent(int percent) {
    return '$percent % power';
  }

  @override
  String deviceLetGoToMoveTo(String position) {
    return 'Let go to move to $position';
  }

  @override
  String deviceMovingTo(String position) {
    return 'Moving to $position…';
  }

  @override
  String deviceRmsRaDec(String ra, String dec) {
    return 'RA $ra · Dec $dec';
  }

  @override
  String deviceDitherEveryFrames(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count frames',
      one: 'Every frame',
    );
    return '$_temp0';
  }

  @override
  String previewFree(String size) {
    return '$size GB free';
  }

  @override
  String get previewUnplugged => 'Unplugged';

  @override
  String get previewStorage => 'Storage';

  @override
  String get previewCpu => 'CPU';

  @override
  String get previewMemory => 'Memory';

  @override
  String get previewUp => 'Up';

  @override
  String get previewHfr => 'HFR';

  @override
  String get previewStars => 'Stars';

  @override
  String get previewStopGuideFrames => 'Stop the guide camera\'s frames first.';

  @override
  String get previewGuideEmpty =>
      'Tap the shutter for the guide camera\'s frames';

  @override
  String get previewCamera => 'Camera';

  @override
  String get previewExposure => 'Exposure';

  @override
  String get previewGainOffset => 'Gain · offset';

  @override
  String get previewClipped => 'Clipped';

  @override
  String get previewLoop => 'Loop';

  @override
  String get previewSingle => 'Single';

  @override
  String get previewDone => 'Done';

  @override
  String get previewStop => 'Stop';

  @override
  String get previewTakeFrame => 'Take a frame';

  @override
  String previewStartsAt(String time) {
    return 'Starts at $time';
  }

  @override
  String get previewStopping => 'Stopping…';

  @override
  String get previewTakingFrame => 'Taking a frame';

  @override
  String get previewStarting => 'Starting…';

  @override
  String previewSecondsLeft(int seconds) {
    return '$seconds s left';
  }

  @override
  String get previewDownloading => 'Downloading…';

  @override
  String get previewFrameReady => 'Frame ready';

  @override
  String get previewCaptureFailed => 'Capture failed';

  @override
  String get previewLooping => 'Looping';

  @override
  String get previewPlanPaused => 'Plan paused';

  @override
  String get previewPlan => 'Plan';

  @override
  String get previewPlanSkipped => 'Skipped: conditions not met';

  @override
  String previewWaitingForFlip(String countdown) {
    return 'Waiting for the meridian flip · $countdown';
  }

  @override
  String get previewParking => 'Parking';

  @override
  String get previewUnparking => 'Unparking';

  @override
  String get previewCooling => 'Cooling';

  @override
  String previewStatusSpoken(String label, String detail) {
    return '$label, $detail';
  }

  @override
  String get focusAuto => 'Auto';

  @override
  String get focusManual => 'Manual';

  @override
  String get focusAutofocus => 'Autofocus';

  @override
  String get focusCancelAutofocus => 'Cancel autofocus';

  @override
  String focusHfr(String value) {
    return 'HFR $value';
  }

  @override
  String get focusNoFocuser => 'No focuser connected';

  @override
  String get polarLoop => 'Loop';

  @override
  String get polarSingle => 'Single';

  @override
  String get polarStart => 'Start polar alignment';

  @override
  String get polarStop => 'Stop';

  @override
  String get polarActionStart => 'start polar alignment';

  @override
  String get polarActionStop => 'stop polar alignment';

  @override
  String get polarActionSave => 'save the polar-align settings';

  @override
  String get polarActionCapture => 'take a frame';

  @override
  String get polarActionComplete => 'complete polar alignment';

  @override
  String get polarPhaseSeeding => 'Finding the pole…';

  @override
  String get polarPhaseAdjusting => 'Adjusting';

  @override
  String get polarPhasePaused => 'Paused';

  @override
  String get polarPhaseFailed => 'Polar alignment failed';

  @override
  String get polarPhaseReady => 'Ready';

  @override
  String get polarPhaseHoming => 'Going Home…';

  @override
  String get polarPhaseNeedsHome => 'Put the mount at Home';

  @override
  String get polarPhaseRough => 'Rough step';

  @override
  String polarPhaseChecking(int done, int total) {
    return 'Checking $done of $total';
  }

  @override
  String get polarVerdictExcellent => 'Excellent';

  @override
  String get polarVerdictVeryGood => 'Very good';

  @override
  String get polarVerdictGood => 'Good';

  @override
  String get polarVerdictRough => 'Rough';

  @override
  String get polarVerdictFarOff => 'Far off';

  @override
  String get polarPhaseChecked => 'Checked';

  @override
  String get polarRoughWaiting =>
      'The main camera measures the mount axis with a turn in RA, then follows your knob moves. Waiting for the first solve…';

  @override
  String get polarSideOnPole => 'The mount axis is on the pole.';

  @override
  String polarSideSits(String parts) {
    return 'The mount axis sits $parts of the pole.';
  }

  @override
  String polarSideSitsVertical(String parts) {
    return 'The mount axis sits $parts the pole.';
  }

  @override
  String get polarSideAbove => 'above';

  @override
  String get polarSideBelow => 'below';

  @override
  String get polarSideEast => 'east';

  @override
  String get polarSideWest => 'west';

  @override
  String get polarSideJoin => ' and ';

  @override
  String get polarNeedsHomeHint =>
      'This mount cannot find Home by itself. Counterweights down, tube pointing at the pole, then Continue.';

  @override
  String polarCheckedLine(String verdict) {
    return 'Three-point check: $verdict';
  }

  @override
  String polarCheckedVsFine(String verdict, String delta) {
    return 'Three-point check: $verdict, $delta′ from the fine readout';
  }

  @override
  String get polarContinue => 'Continue';

  @override
  String get polarActionContinue => 'continue polar alignment';

  @override
  String get polarNextFine => 'Next: fine';

  @override
  String get polarActionNextFine => 'move on to the fine step';

  @override
  String get polarCheck => 'Check it';

  @override
  String get polarActionCheck => 'start the check';

  @override
  String get polarAltitude => 'Altitude';

  @override
  String get polarAzimuth => 'Azimuth';

  @override
  String polarBoltWorse(String axis) {
    return '$axis got worse. Turn that bolt back past where you started.';
  }

  @override
  String polarNoSolve(int count) {
    return 'No solve ($count in a row). Check the sky.';
  }

  @override
  String get polarExposure => 'Exposure';

  @override
  String get polarTakeFrame => 'Take frame';

  @override
  String get polarDone => 'Done';

  @override
  String get searchHint => 'Objects, settings, modes, images';

  @override
  String get searchGroupSky => 'Sky';

  @override
  String get searchGroupSettings => 'Settings';

  @override
  String get searchGroupModes => 'Modes';

  @override
  String get searchGroupImages => 'Images';

  @override
  String get searchImageLibrary => 'Image Library';

  @override
  String searchNoMatch(String query) {
    return 'Nothing in Simple Mode matches \"$query\".';
  }

  @override
  String searchMatches(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count matches',
      one: '1 match',
    );
    return '$_temp0';
  }

  @override
  String get searchSectionRig => 'Rig';

  @override
  String get searchSectionDevices => 'Devices';

  @override
  String get searchSectionCamera => 'Camera (Main)';

  @override
  String get searchSectionMount => 'Mount';

  @override
  String get searchSectionFocuser => 'Focuser';

  @override
  String get searchSectionGuider => 'Camera (Guide)';

  @override
  String get searchSettingAperture => 'Main scope aperture';

  @override
  String get searchSettingFocalLength => 'Main scope focal length';

  @override
  String get searchSettingLatitude => 'Latitude';

  @override
  String get searchSettingLongitude => 'Longitude';

  @override
  String get searchSettingElevation => 'Elevation';

  @override
  String get searchSettingLocationTime => 'Location and time';

  @override
  String get searchSettingPermanentPier => 'Permanent pier';

  @override
  String get searchSettingSimpleMode => 'Simple Mode';

  @override
  String get searchSettingChangeProfile => 'Change profile';

  @override
  String get searchSettingChangeRig => 'Change rig';

  @override
  String get searchSettingVersions => 'Versions';

  @override
  String get searchSettingLogs => 'Logs & bug report';

  @override
  String get searchSettingConnect => 'Connect';

  @override
  String get searchSettingDisconnect => 'Disconnect';

  @override
  String get searchSettingReconnect => 'Reconnect';

  @override
  String get searchSettingCooler => 'Cooler';

  @override
  String get searchSettingCoolerTarget => 'Cooler target';

  @override
  String get searchSettingGain => 'Gain';

  @override
  String get searchSettingTracking => 'Tracking';

  @override
  String get searchSettingPark => 'Park';

  @override
  String get searchSettingMeridianFlip => 'Meridian flip';

  @override
  String get searchSettingFocuserStep => 'Focuser step';

  @override
  String get searchSettingDither => 'Dither';

  @override
  String get searchWordsRig => 'system profile';

  @override
  String get searchWordsAperture => 'telescope diameter';

  @override
  String get searchWordsFocalLength => 'telescope';

  @override
  String get searchWordsSite => 'site location coordinates';

  @override
  String get searchWordsElevation => 'site altitude height';

  @override
  String get searchWordsLocationTime => 'gps clock time zone site';

  @override
  String get searchWordsPermanentPier => 'polar alignment observatory';

  @override
  String get searchWordsSimpleMode => 'full ui advanced';

  @override
  String get searchWordsChangeProfile => 'profile';

  @override
  String get searchWordsChangeRig => 'server';

  @override
  String get searchWordsVersions => 'version about firmware driver';

  @override
  String get searchWordsLogs => 'support logs';

  @override
  String get searchWordsDevices => 'equipment gear';

  @override
  String get searchWordsCooler => 'camera temperature cooling';

  @override
  String get searchWordsGain => 'camera iso hcg';

  @override
  String get searchWordsTracking => 'mount sidereal';

  @override
  String get searchWordsPark => 'mount unpark';

  @override
  String get searchWordsMeridianFlip => 'mount pier side';

  @override
  String get searchWordsFocuser => 'focus position knob move';

  @override
  String get searchWordsFocuserStep => 'focus move';

  @override
  String get searchWordsGuider => 'guiding rms phd2';

  @override
  String get searchWordsDither => 'guiding dithering';

  @override
  String get simplePreflightStepFocus => 'Focus';

  @override
  String get simplePreflightStepPolarAlign => 'Polar align';

  @override
  String get simplePreflightStepPlan => 'Plan';

  @override
  String get simplePreflightStepGuide => 'Guide';

  @override
  String simplePreflightStepDone(String step) {
    return '$step, done';
  }

  @override
  String simplePreflightStepSkipped(String step) {
    return '$step, skipped';
  }

  @override
  String simplePreflightStepCurrent(String step) {
    return '$step, current step';
  }

  @override
  String simplePreflightStepNext(String step) {
    return '$step, next';
  }

  @override
  String get simplePreflightBack => 'Back';

  @override
  String get simplePreflightSkip => 'Skip';

  @override
  String get shellModePreflight => 'Preflight';

  @override
  String get shellModePreview => 'Preview';

  @override
  String get shellModePolarAlign => 'Polar align';

  @override
  String get shellModeFocus => 'Focus';

  @override
  String get shellModePlan => 'Plan';

  @override
  String get shellRailPolarAlign => 'P.A.';

  @override
  String get shellRailPreflight => 'Preflight';

  @override
  String shellStopAgain(String shortcut) {
    return 'Press $shortcut again to stop the run';
  }

  @override
  String shellDiscardEditQuestion(String name) {
    return 'Discard changes to $name?';
  }

  @override
  String get shellKeepEditing => 'Keep editing';

  @override
  String get shellDiscard => 'Discard';

  @override
  String get shellShowControls => 'Show controls';

  @override
  String get shellHideControls => 'Hide controls';

  @override
  String get shellTapShutter => 'Tap the shutter to take a frame';

  @override
  String get shellImageThis => 'Image this';

  @override
  String get shellSearch => 'Search';

  @override
  String get shellNightModeOn => 'Night mode on';

  @override
  String get shellNightMode => 'Night mode';

  @override
  String get shellSettings => 'Settings';

  @override
  String get shellSky => 'Sky Map';

  @override
  String get shellStats => 'Stats';

  @override
  String get shellInfo => 'Info';

  @override
  String get shellLibrary => 'Library';

  @override
  String shellModeSemantics(String mode) {
    return 'Mode: $mode';
  }

  @override
  String get shellGuiding => 'Guiding';

  @override
  String shellGuidingRms(String rms) {
    return 'RMS $rms';
  }

  @override
  String get shellRa => 'RA';

  @override
  String get shellDec => 'Dec';

  @override
  String get shellHistogram => 'Histogram';

  @override
  String shellHistogramMedian(int median) {
    return 'Median $median';
  }

  @override
  String get shellSectionRig => 'Rig';

  @override
  String get shellSectionDevices => 'Devices';

  @override
  String get shellSectionCamera => 'Camera (Main)';

  @override
  String get shellSectionMount => 'Mount';

  @override
  String get shellSectionFocuser => 'Focuser';

  @override
  String get shellSectionGuider => 'Camera (Guide)';

  @override
  String get shellRigNotConnected => 'Rig not connected';

  @override
  String get shellFilterWheel => 'Filter wheel';

  @override
  String get shellSwitches => 'Switches';

  @override
  String get shellRotator => 'Rotator';

  @override
  String get shellFlatPanel => 'Flat panel';

  @override
  String get shellDome => 'Dome';

  @override
  String get shellWeather => 'Weather';

  @override
  String shellGearTitle(String type, String name) {
    return '$type · $name';
  }

  @override
  String shellGearActionFailed(String action, String device) {
    return '$action failed · $device';
  }

  @override
  String get shellOk => 'OK';

  @override
  String get shellBedtimeTitle => 'Before you sleep';

  @override
  String get shellBedtimeAllGood => 'Everything is ready for the night.';

  @override
  String get shellBedtimeAnswer => 'Got it — let me go to bed';

  @override
  String get shellConnected => 'Connected';

  @override
  String get shellConnecting => 'Connecting…';

  @override
  String get shellFailed => 'Failed';

  @override
  String get shellNotConnected => 'Not connected';

  @override
  String get shellDisconnect => 'Disconnect';

  @override
  String get shellReconnect => 'Reconnect';

  @override
  String get shellAddDevice => 'Add a device';

  @override
  String get shellChooseDevice => 'Choose…';

  @override
  String get shellConnect => 'Connect';

  @override
  String get shellCouldntSave => 'Couldn\'t save that';

  @override
  String get shellCouldntSetClock => 'Couldn\'t set the rig\'s clock';

  @override
  String get shellCouldntGetLocation => 'Couldn\'t get a location';

  @override
  String get shellCouldntSaveSite => 'Couldn\'t save the site';

  @override
  String shellSiteFrom(String source) {
    return 'Site from $source';
  }

  @override
  String get shellChange => 'Change';

  @override
  String get shellName => 'Name';

  @override
  String get shellChangeRig => 'Change rig';

  @override
  String get shellProfile => 'Profile';

  @override
  String get shellChangeProfile => 'Change profile';

  @override
  String get shellPlanRunning =>
      'A plan is running. It keeps running on the rig; pick this profile again to come back to it.';

  @override
  String get shellAperture => 'Main scope aperture';

  @override
  String get shellFocalLength => 'Main scope focal length';

  @override
  String get shellLatitude => 'Latitude';

  @override
  String get shellLongitude => 'Longitude';

  @override
  String get shellElevation => 'Elevation';

  @override
  String shellUseLocation(String device) {
    return 'Use $device\'s location and time';
  }

  @override
  String get shellPermanentPier => 'Permanent pier';

  @override
  String get shellPermanentPierHint => 'Skips polar alignment';

  @override
  String previewPreflightWaiting(Object step) {
    return '$step: waiting for you';
  }

  @override
  String get shellFullUi => 'Full UI';

  @override
  String get shellOpenSourceLicenses => 'Open-source licenses';

  @override
  String get shellLicenses => 'Licenses';

  @override
  String get shellSimpleMode => 'Simple Mode';

  @override
  String get shellThisApp => 'This app';

  @override
  String get shellAraServer => 'Ara server';

  @override
  String get shellStarDatabase => 'Star database';

  @override
  String get shellSystem => 'System';

  @override
  String get shellLogs => 'Logs & bug report';

  @override
  String get shellNotSet => 'Not set';

  @override
  String get shellHemiNorth => 'N';

  @override
  String get shellHemiSouth => 'S';

  @override
  String get shellHemiEast => 'E';

  @override
  String get shellHemiWest => 'W';

  @override
  String shellWheelSemantics(String label, int index, int count) {
    return '$label, wheel $index of $count';
  }

  @override
  String get shellRaise => 'Raise';

  @override
  String get shellLower => 'Lower';

  @override
  String get planNeverRises => 'Never rises here';

  @override
  String get planBelowHorizon => 'Below your horizon';

  @override
  String planBelowHorizonRises(String time) {
    return 'Below your horizon · rises $time';
  }

  @override
  String planAltitudeUpMax(int alt, int max) {
    return '$alt° up · max $max°';
  }

  @override
  String planAltitudeUp(int alt) {
    return '$alt° up';
  }

  @override
  String get planBelowTheHorizon => 'below the horizon';

  @override
  String planRisesAt(String time) {
    return 'rises $time';
  }

  @override
  String planRisesAtCap(String time) {
    return 'Rises $time';
  }

  @override
  String get planPausing => 'Pausing…';

  @override
  String get planPaused => 'Paused';

  @override
  String get planWaitingForFlip => 'Waiting for flip';

  @override
  String get planDone => 'Done';

  @override
  String planMoreInQueue(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count more in the queue',
    );
    return '$_temp0';
  }

  @override
  String get planQueued => 'queued';

  @override
  String planSquareLabel(String name) {
    return 'Plan: $name';
  }

  @override
  String get planPickTargetFirst => 'Pick a target first';

  @override
  String get planStartImaging => 'Start imaging';

  @override
  String get planPauseAfterFrame => 'Pause after this frame';

  @override
  String get planPausingLabel => 'Pausing';

  @override
  String get planPausingAfterFrame => 'Pausing after this frame';

  @override
  String get planPauseAfterFlip => 'Pause after the flip';

  @override
  String get planResume => 'Resume';

  @override
  String get planStopTheRun => 'Stop the run';

  @override
  String get planHoldToStop => 'Hold to stop';

  @override
  String get planTonightsBest => 'TONIGHT\'S BEST';

  @override
  String planEditTarget(String name) {
    return 'Edit $name';
  }

  @override
  String get planSearchObjects => 'Search objects';

  @override
  String get planSearch => 'Search';

  @override
  String get planSearchOrPick => 'Search, or pick in the sky.';

  @override
  String get planPickInSky => 'Pick in Sky';

  @override
  String get planExposure => 'Exposure';

  @override
  String planExposureSuggested(String exposure) {
    return '$exposure · suggested';
  }

  @override
  String get planFrames => 'Frames';

  @override
  String planSuggested(String hint) {
    return 'Suggested: $hint';
  }

  @override
  String get planUseAll => 'Use all';

  @override
  String planFrameCount(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count frames',
    );
    return '$_temp0';
  }

  @override
  String get planNotPolarAligned => 'Not polar aligned tonight';

  @override
  String get planStartsNow => 'Starts now';

  @override
  String planStartsAtDark(String time) {
    return 'Starts at dark ($time)';
  }

  @override
  String planStopsAtDawn(String time) {
    return 'Stops at dawn · $time';
  }

  @override
  String get planSquareParks => 'Parks';

  @override
  String planStartsAt(String time) {
    return 'Starts at $time';
  }

  @override
  String get planStartNowInstead => 'Start now instead';

  @override
  String planWaitForDark(String time) {
    return 'Wait for dark ($time)';
  }

  @override
  String get planPickATime => 'Pick a time';

  @override
  String get planCancel => 'Cancel';

  @override
  String get planSaving => 'Saving…';

  @override
  String get planSave => 'Save';

  @override
  String planTargetAdded(String name, int count) {
    return '$name added · $count in the queue';
  }

  @override
  String get planAddToQueue => 'Add to queue';

  @override
  String get planStarting => 'Starting…';

  @override
  String planStartAt(String time) {
    return 'Start at $time';
  }

  @override
  String get planStartNow => 'Start now';

  @override
  String get planCreateAndStart => 'Create and start';

  @override
  String planTargetRemoved(String name) {
    return '$name removed';
  }

  @override
  String get planUndo => 'Undo';

  @override
  String planMoveTarget(String name) {
    return 'Move $name';
  }

  @override
  String planRemoveFromQueue(String name) {
    return 'Remove $name from the queue';
  }

  @override
  String get planRemove => 'Remove';

  @override
  String planQueueHeader(int count, String duration) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: 'targets',
      one: 'target',
    );
    return 'QUEUE · $count $_temp0 · $duration';
  }

  @override
  String get planOpenStorage => 'Open Storage';

  @override
  String get planNightDone => 'Night done';

  @override
  String get planStoppedAtDawn => 'stopped at dawn';

  @override
  String get planStopped => 'stopped';

  @override
  String get planFailed => 'failed';

  @override
  String planStoppedAt(String step) {
    return 'Stopped at: $step';
  }

  @override
  String get planNewPlan => 'New plan';

  @override
  String planImagingTarget(String name) {
    return 'IMAGING $name';
  }

  @override
  String get planMountStopped => 'Mount stopped';

  @override
  String planEndsLeft(String time, String duration) {
    return 'Ends $time · $duration left';
  }

  @override
  String get planHintResume =>
      'Tap the shutter to resume and re-center; hold it to stop.';

  @override
  String get planResumeWithoutRecenter => 'Resume without re-centering';

  @override
  String get planRecenterAndRefocus => 'Re-center and refocus';

  @override
  String get planHintPause =>
      'Tap the shutter to pause after this frame; hold it to stop.';

  @override
  String get planAddTarget => 'Add target';

  @override
  String planMagnitude(String value) {
    return 'mag $value';
  }

  @override
  String planAlreadyQueued(String name) {
    return '$name is already in the queue.';
  }

  @override
  String get planRigUnreachable =>
      'Couldn\'t reach the rig. Check the connection and try again.';

  @override
  String get planActionCreate => 'create the plan';

  @override
  String get planActionSaveEdit => 'save the edit';

  @override
  String get planActionPutBack => 'put it back';

  @override
  String get planActionReorder => 'reorder the queue';

  @override
  String get planActionRemove => 'remove it from the queue';

  @override
  String get planActionSchedule => 'schedule the start';

  @override
  String get planActionStart => 'start imaging';

  @override
  String get planActionPause => 'pause';

  @override
  String get planActionResume => 'resume';

  @override
  String get planActionStop => 'stop the run';

  @override
  String get planRunEnding => 'The run is ending; this target can\'t join it.';

  @override
  String get planQueueBusy =>
      'The queue is still updating on the rig. Try again in a moment.';

  @override
  String get shellLanguage => 'Language';

  @override
  String get shellLanguageSystem => 'System';

  @override
  String get estopCompactStop => 'STOP';

  @override
  String get estopCompactHold => 'HOLD';

  @override
  String get estopCompactPressAgain => 'PRESS\nAGAIN';

  @override
  String get estopStopping => 'Stopping…';

  @override
  String get estopHoldToConfirm => 'Hold to confirm';

  @override
  String get estopPressAgain => 'Press again';

  @override
  String get estopLabel => 'Emergency Stop';

  @override
  String get estopPressAgainAnnouncement =>
      'Press again within 3 seconds to stop everything';

  @override
  String get estopConfirmTitle => 'Stop everything now?';

  @override
  String get estopConfirmBody =>
      'This aborts the running sequence, stops the mount where it is with tracking off, halts the rotator where it is, aborts the in-flight exposure, stops the guide-camera focus loop, the Bahtinov readout, autofocus and polar alignment, stops guiding, and switches the flat panel light and the camera cooler off. It cannot be undone — the sequence does not resume.';

  @override
  String get estopCancel => 'Cancel';

  @override
  String get estopConfirm => 'EMERGENCY STOP';

  @override
  String get estopNoServer => 'No server connected — nothing to stop.';

  @override
  String estopFailed(String error) {
    return 'EMERGENCY STOP FAILED — $error Check your rig manually.';
  }

  @override
  String get estopActionReach => 'reach the rig';

  @override
  String get estopAlreadyRunning =>
      'An emergency stop is already running — no second volley sent.';

  @override
  String estopRunsAborted(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count sequence run(s) aborted',
    );
    return '$_temp0';
  }

  @override
  String get estopNoSequence => 'no sequence was running';

  @override
  String get estopExposureAborted => 'exposure aborted';

  @override
  String get estopGuidingStopped => 'guiding stopped';

  @override
  String get estopMountStopped => 'mount stopped';

  @override
  String get estopFlatPanelOff => 'flat panel light off';

  @override
  String get estopCoolerOff => 'camera cooler off';

  @override
  String estopSummary(String parts) {
    return 'Emergency stop executed: $parts.';
  }

  @override
  String estopCheckRig(String failures) {
    return 'CHECK THE RIG: $failures.';
  }

  @override
  String get estopFailAbortRuns =>
      'SEQUENCE ABORT FAILED (the run may still be executing)';

  @override
  String get estopFailAbortExposure => 'EXPOSURE ABORT FAILED';

  @override
  String get estopFailStopGuiding =>
      'GUIDING MAY STILL BE ACTIVE (stop failed)';

  @override
  String get estopFailStopMount => 'MOUNT STOP FAILED — verify it manually';

  @override
  String get estopFailRotator =>
      'ROTATOR HALT FAILED — it may still be turning';

  @override
  String get estopFailFlatPanel => 'FLAT PANEL LIGHT-OFF FAILED';

  @override
  String get estopFailCooler =>
      'CAMERA COOLER STILL ON (switching it off failed)';

  @override
  String get estopFailGuideFocus =>
      'GUIDE-CAMERA FOCUS LOOP MAY STILL BE RUNNING (stop failed)';

  @override
  String get estopFailBahtinov =>
      'BAHTINOV READOUT MAY STILL BE RUNNING (stop failed)';

  @override
  String get estopFailAutofocus =>
      'AUTOFOCUS MAY STILL BE RUNNING (cancel failed)';

  @override
  String get estopFailPolarAlign =>
      'POLAR ALIGNMENT MAY STILL BE RUNNING (stop failed)';

  @override
  String estopFailOther(String step) {
    return '$step FAILED';
  }

  @override
  String get focusCardInFocus => 'In focus';

  @override
  String focusCardInFocusHfr(String hfr) {
    return 'In focus · HFR $hfr';
  }

  @override
  String get focusCardFailed => 'Autofocus failed';

  @override
  String get focusCardCancelled => 'Canceled';

  @override
  String get focusCardNotFocused => 'Not focused yet';

  @override
  String get focusCardFailedSeeLogs => 'Autofocus failed — see Support → Logs';

  @override
  String focusCardBackAt(String position) {
    return 'The focuser is back at $position.';
  }

  @override
  String focusCardSmart(String shot, String total) {
    return 'Smart Focus — shot $shot of $total';
  }

  @override
  String get focusCardCoarse => 'Finding rough focus…';

  @override
  String focusCardSweeping(String done, String total) {
    return 'Sweeping — probe $done of $total';
  }

  @override
  String focusCardPass(String pass) {
    return '(pass $pass)';
  }

  @override
  String get focusCardFitting => 'Fitting the curve…';

  @override
  String get focusCardMoving => 'Moving to best focus…';

  @override
  String get focusCardConfirming => 'Confirming focus…';

  @override
  String get focusCardBacklash => 'Measuring focuser backlash…';

  @override
  String get focusCardRunning => 'Autofocus running…';

  @override
  String focusCardInFocusAt(String position) {
    return 'In focus at $position';
  }

  @override
  String focusCardTook(String duration) {
    return 'in $duration';
  }

  @override
  String focusCardFailedReason(String reason) {
    return 'Failed: $reason';
  }

  @override
  String get focusCardSeeLogs => 'see Support → Logs';

  @override
  String focusCardRestoredPosition(String position) {
    return 'focuser restored to $position';
  }

  @override
  String get focusCardNoRunYet => 'No autofocus yet this session.';

  @override
  String get filterDropdownLabel => 'Filter';

  @override
  String get filterDropdownDidntMove =>
      'Couldn\'t change the filter - the wheel didn\'t start moving.';

  @override
  String filterDropdownNotASlot(String name) {
    return 'Filter \'$name\' isn\'t a slot on the connected wheel.';
  }

  @override
  String get filterDropdownBusy => 'Another action is still in progress.';

  @override
  String get filterDropdownActionChange => 'change the filter';

  @override
  String get polarHintOnTarget => 'On target';

  @override
  String get polarHintLower => 'Lower';

  @override
  String get polarHintRaise => 'Raise';

  @override
  String get polarHintMoveWest => 'Move west';

  @override
  String get polarHintMoveEast => 'Move east';

  @override
  String get polarHintAltitudeWorse =>
      'Altitude got worse after that turn — the altitude bolt moves the axis the other way on this mount. Turn it back past where you started.';

  @override
  String get polarHintAzimuthWorse =>
      'Azimuth got worse after that turn — the azimuth bolt moves the axis the other way on this mount. Turn it back past where you started.';

  @override
  String get guiderStateNotGuiding => 'Not guiding';

  @override
  String get guiderStateLooping => 'Looping';

  @override
  String get guiderStateStarSelected => 'Star selected';

  @override
  String get guiderStateCalibrating => 'Calibrating';

  @override
  String get guiderStateGuiding => 'Guiding';

  @override
  String get guiderStateSettling => 'Settling';

  @override
  String get guiderStatePaused => 'Paused';

  @override
  String get guiderStateStarLost => 'Star lost';

  @override
  String get guiderStateDithering => 'Dithering';

  @override
  String get guiderStateAssistant => 'Guiding Assistant';

  @override
  String get guiderStateUnknown => 'Unknown';

  @override
  String get guiderStateNotConnected => 'Not connected';

  @override
  String guiderStateCalibrationStep(String direction, String step) {
    return '$direction step $step';
  }

  @override
  String guideFindStarFailed(String reason) {
    return 'Couldn\'t find a star: $reason';
  }

  @override
  String errorCouldnt(String action) {
    return 'Couldn\'t $action';
  }

  @override
  String get errorActionDefault => 'do that';

  @override
  String errorNoAnswer(String device) {
    return 'your rig didn\'t answer. Check it\'s powered on and on the same network as $device.';
  }

  @override
  String get errorTooSlow =>
      'your rig took too long to answer. It may be busy; try again in a moment.';

  @override
  String get errorUntrusted => 'the connection wasn\'t trusted.';

  @override
  String get errorCancelled => 'the request was canceled.';

  @override
  String get errorNotFound =>
      'your rig doesn\'t have that. It may be running an older version of Ara.';

  @override
  String get errorBusy => 'something else is using it right now.';

  @override
  String get errorRefusedValues => 'your rig refused those values.';

  @override
  String get errorServer =>
      'your rig hit an error. Support → Logs has the details.';

  @override
  String get errorRefused => 'your rig refused the request.';

  @override
  String get errorConnectionFailed =>
      'the connection failed. Check your rig is reachable.';

  @override
  String get errorFile => 'a file problem got in the way';

  @override
  String get errorUnreadable =>
      'your rig sent something Ara didn\'t understand. It may be running a different version.';

  @override
  String focusCardWeakFit(String r2) {
    return 'The curve fit was weak (R² $r2) — passing cloud, a slew during the sweep, or probes that straddle the V unevenly. The focus here comes from the measured frame, not the fit; run again if the sky has settled.';
  }

  @override
  String focusCardWeakFitPlateau(String r2) {
    return 'The curve fit was weak (R² $r2): the outer probes sit on a plateau, so the sweep is wider than the V. A smaller step size would put more probes on the slope. The focus here comes from the measured frame, not the fit.';
  }

  @override
  String focusCardWeakFitPlateauAuto(String r2) {
    return 'The curve fit was weak (R² $r2): the outer probes sit on a plateau, so this sweep was wider than the V. With automatic step size on, Ara sizes the next sweep from the V\'s width where a run lets it be measured, so running again should put more probes on the slope. The focus here comes from the measured frame, not the fit.';
  }

  @override
  String get focusBestPosition => 'Best position';

  @override
  String get focusStars => 'Stars';

  @override
  String get focusFitR2 => 'Fit R²';

  @override
  String focusFocuserAt(String position) {
    return 'Focuser at $position';
  }

  @override
  String get focusMeasuredStars => 'Measured stars';

  @override
  String get bahtinovInFocusMaskOff => 'In focus · mask off';

  @override
  String get bahtinovMaskOff => 'Mask off';

  @override
  String get bahtinovCameraError => 'Stopped by a camera error';

  @override
  String get bahtinovWaitingFirst => 'Waiting for the first frame';

  @override
  String bahtinovInFocusPx(String px) {
    return 'In focus · $px px';
  }

  @override
  String bahtinovHintPx(String hint, String px) {
    return '$hint · $px px from focus';
  }

  @override
  String bahtinovStoppedLast(String px) {
    return 'Stopped · last $px px from focus';
  }

  @override
  String get bahtinovFitMask => 'Fit the mask, then Start';

  @override
  String get bahtinovFitMaskDetail =>
      'Put the Bahtinov mask over the front of the telescope and point at a bright star. Ara uses the brightest star in the frame.';

  @override
  String get bahtinovWaiting => 'Waiting';

  @override
  String get bahtinovWaitingDetail => 'The first frame is on its way.';

  @override
  String get bahtinovNoStar => 'No star';

  @override
  String get bahtinovNoStarDetail =>
      'Point at a bright star, or lengthen the exposure.';

  @override
  String get bahtinovNearEdge => 'Star at the edge';

  @override
  String get bahtinovNearEdgeDetail =>
      'Center the star so its spikes fit in the frame.';

  @override
  String get bahtinovNoSpikes => 'No spikes';

  @override
  String get bahtinovNoSpikesDetail =>
      'Fit the mask over the front of the telescope. Far out of focus? Turn until the star shrinks and spikes appear.';

  @override
  String get bahtinovInFocus => 'In focus';

  @override
  String bahtinovInFocusZoneDetail(String um) {
    return 'The middle spike is centered, inside the ±$um µm focus zone. Lock the focuser, then Finish.';
  }

  @override
  String get bahtinovInFocusDetail =>
      'The middle spike is centered. Lock the focuser, then Finish.';

  @override
  String get bahtinovMakeMove => 'Make a move';

  @override
  String get bahtinovMakeMoveDetail =>
      'Turn the focuser a little either way, then watch the number.';

  @override
  String get bahtinovGoBack => 'Go back';

  @override
  String bahtinovPassedDetail(String size) {
    return 'You passed focus. Turn back about $size your last move.';
  }

  @override
  String get bahtinovKeepGoing => 'Keep going';

  @override
  String bahtinovKeepGoingDetail(String size) {
    return 'Same way — about $size your last move again.';
  }

  @override
  String get bahtinovWrongWayDetail =>
      'Wrong way — turn back past where you started.';

  @override
  String get bahtinovMoveQuarter => 'a quarter of';

  @override
  String get bahtinovMoveThird => 'a third of';

  @override
  String get bahtinovMoveHalf => 'half';

  @override
  String get bahtinovMoveThreeQuarters => 'three quarters of';

  @override
  String get bahtinovMoveSame => 'the same as';

  @override
  String get bahtinovMoveOneAndHalf => '1½×';

  @override
  String get bahtinovMoveTwice => 'twice';

  @override
  String get bahtinovMoveSeveral => 'several times';

  @override
  String get bahtinovPxFromFocus => 'px from focus';

  @override
  String get bahtinovFinish => 'Finish';

  @override
  String get bahtinovMaskQuestion => 'Take the Bahtinov mask off';

  @override
  String get bahtinovMaskQuestionDetail =>
      'Take the mask off the front of the telescope before you image. Left on, it puts spikes on every star and costs light.';

  @override
  String get bahtinovNotYet => 'Not yet';

  @override
  String get bahtinovMaskRemoved => 'Mask removed';

  @override
  String get bahtinovExposure => 'Exposure';

  @override
  String get focusStartBahtinov => 'Start the Bahtinov readout';

  @override
  String get focusStopBahtinov => 'Stop the Bahtinov readout';

  @override
  String focusWeakFitShort(String r2) {
    return 'Weak curve fit (R² $r2): the focus comes from the measured frame. Run again if the sky has settled.';
  }

  @override
  String get guideFocusStarting => 'Starting…';

  @override
  String get guideFocusNoStarsMeasured => 'Live · no stars measured';

  @override
  String guideFocusLive(String hfr) {
    return 'Live · HFR $hfr';
  }

  @override
  String guideFocusLiveInFocus(String hfr) {
    return 'Live · HFR $hfr — in focus';
  }

  @override
  String guideFocusLiveBest(String hfr) {
    return 'Live · HFR $hfr — best so far';
  }

  @override
  String get guideFocusStoppedError => 'Stopped on an error';

  @override
  String get guideFocusHeld => 'In focus — held, stopped';

  @override
  String guideFocusHeldBest(String hfr) {
    return 'In focus — held, stopped · best HFR $hfr';
  }

  @override
  String guideFocusInFocusBest(String hfr) {
    return 'In focus · best HFR $hfr';
  }

  @override
  String get guideFocusStopped => 'Stopped';

  @override
  String guideFocusStoppedBest(String hfr) {
    return 'Stopped · best HFR $hfr';
  }

  @override
  String get guideFocusWaitingMain => 'Waiting for the main telescope';

  @override
  String get guideFocusNotReady => 'Not ready';

  @override
  String get guideFocusReady => 'Ready to focus';

  @override
  String get guideFocusConnectGuider =>
      'Connect the guider first — the frames come through it.';

  @override
  String get guideFocusStopGuider =>
      'Stop the guider first; it owns its camera while it is looping, guiding, calibrating or paused.';

  @override
  String get guideFocusNoStars => 'No stars';

  @override
  String get guideFocusNoStarsDetail =>
      'Point at a star field or lengthen the exposure. Far out of focus? Make big moves until stars appear.';

  @override
  String get guideFocusBigMoves => 'big moves';

  @override
  String get guideFocusSmallMoves => 'small moves';

  @override
  String guideFocusInFocusDetail(String target) {
    return 'Stars are as tight as this camera resolves (in focus ≤ $target px). Lock the focuser.';
  }

  @override
  String get guideFocusSharpest => 'Sharpest so far';

  @override
  String get guideFocusSharpestDetail =>
      'Hold here, or nudge a touch either way to confirm.';

  @override
  String guideFocusMakeMoveDetail(String size) {
    return 'In or out, $size — then watch which way the number goes.';
  }

  @override
  String guideFocusKeepGoingDetail(String size) {
    return 'Same way you just moved — $size.';
  }

  @override
  String guideFocusGoBackDetail(String size) {
    return 'The other way from your last move — $size.';
  }

  @override
  String get guideFocusHolding => 'Holding';

  @override
  String get guideFocusHoldingFar =>
      'Still well off focus — make a bigger move and watch the number.';

  @override
  String get guideFocusHoldingNear => 'Try a small move either way.';

  @override
  String get focusGuide => 'Guide';

  @override
  String get focusGuideScope =>
      'Guide scope: turn its focuser, or slide the camera in the draw tube, in small moves and watch the number fall.';

  @override
  String get focusGuideOag =>
      'Off-axis guider: it shares the main scope\'s focus. Turn the OAG\'s helical focuser in small moves and watch the number fall.';

  @override
  String get focusGuideMainFirst =>
      'Focus the main telescope first: an off-axis guider sees the same focal plane, so its HFR only means something once the main scope is in focus.';

  @override
  String get focusAlreadyInFocus => 'Already in focus';

  @override
  String get focusFrames => 'Frames';

  @override
  String get focusStartGuide => 'Start the guide camera\'s live focus';

  @override
  String get focusStopGuide => 'Stop the guide camera\'s live focus';

  @override
  String get focusScopeMain => 'Main';

  @override
  String get focusScopeGuide => 'Guide';

  @override
  String get preflightNextFocus => 'Next: Focus';

  @override
  String get preflightNextPolarAlign => 'Next: Polar align';

  @override
  String get preflightStartImaging => 'Start imaging';

  @override
  String get preflightImagingStartsAfter =>
      'Imaging starts after focus and polar alignment.';

  @override
  String get preflightDoneStartImaging => 'Done · Start imaging';

  @override
  String get shellGuideFocalLength => 'Guide scope focal length';

  @override
  String get shellGuidePixelSize => 'Guide camera pixel size';

  @override
  String get searchWordsGuideScope =>
      'guide scope guider camera pixel focal polar';

  @override
  String get shellModeGuide => 'Guider';

  @override
  String get preflightNextGuide => 'Next: Guide';

  @override
  String get guideStart => 'Start guiding';

  @override
  String get guideStop => 'Stop guiding';

  @override
  String get guideActionStart => 'start guiding';

  @override
  String get guideActionStop => 'stop guiding';

  @override
  String get guideIdle => 'Idle';

  @override
  String get guideNoGuider => 'Guider not connected';

  @override
  String get guideTapShutter =>
      'Tap the shutter to start guiding. It calibrates first when needed.';

  @override
  String get guideConnectFirst => 'Connect the guider in Settings › Devices.';

  @override
  String get deviceGuiderCamera => 'Camera (Guide)';

  @override
  String get shellNoDevices => 'No devices connected';

  @override
  String get guideStarting => 'Starting…';

  @override
  String get guideStopping => 'Stopping…';

  @override
  String guideStopRefused(String state) {
    return 'The guider didn\'t stop: it is still $state. Try Stop again.';
  }

  @override
  String guideStartFailed(String reason) {
    return 'Guiding didn\'t start: $reason.';
  }

  @override
  String get guideStartFailedNoReason =>
      'Guiding didn\'t start. See the Guider log for why.';

  @override
  String get guideAggressiveness => 'Aggressiveness';

  @override
  String get guideAxisRa => 'RA';

  @override
  String get guideAxisDec => 'Dec';

  @override
  String get guideRaAggressiveness => 'RA aggressiveness';

  @override
  String get guideDecAggressiveness => 'Dec aggressiveness';

  @override
  String get guideTabGuide => 'Guide';

  @override
  String get guideTabTuning => 'Tuning';

  @override
  String get guideTabTools => 'Tools';

  @override
  String get guideMinimumMove => 'Minimum move · goes to the Guider now';

  @override
  String get guideSettle => 'Settle after each dither';

  @override
  String get guideSettleWithin => 'Within';

  @override
  String get guideSettleFor => 'For';

  @override
  String get guideSettleGiveUp => 'Give up after';

  @override
  String get guideRecalibrateNow => 'Recalibrate now';

  @override
  String get guideRunAssistant => 'Run Guiding Assistant';

  @override
  String get guideStopAssistant => 'Stop Guiding Assistant';

  @override
  String guideAssistantApply(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: 'Apply $count suggestions',
      one: 'Apply 1 suggestion',
    );
    return '$_temp0';
  }

  @override
  String get guideStopPlanFirst => 'Stop the plan to use these.';

  @override
  String get guideStopGuidingFirst => 'Stop guiding to use these.';

  @override
  String get guideTabsGroup => 'Guider page';

  @override
  String get guideMinimumMoveName => 'Minimum move';

  @override
  String get guideStopLoopFirst => 'Stop the guide loop to recalibrate.';

  @override
  String get guideAssistantCalibrateFirst =>
      'Calibrate first: press Recalibrate, then run the Guiding Assistant.';

  @override
  String get guideAssistantNoStar =>
      'The guider found no star to measure. Check the guide camera, then try again.';

  @override
  String get focusNudgePlanRunning => 'Stop the plan to move the focuser.';

  @override
  String get focusNudgeAutofocusRunning =>
      'Autofocus has the focuser. Wait for it to finish, or cancel it.';

  @override
  String get guideAssistantFailedNoReason => 'the guider gave no reason';

  @override
  String get guideToolRecalibrate => 'Recalibrate';

  @override
  String get guideToolCalibration => 'Calibration';

  @override
  String get guideToolCalibrationWhat =>
      'Measures again how the mount moves the guide star. Redo it after turning the camera or moving far in Dec.';

  @override
  String guideToolCalibratedAt(String dec) {
    return 'Calibrated at Dec $dec';
  }

  @override
  String get guideToolCalibrated => 'Calibrated';

  @override
  String get guideToolNotCalibrated =>
      'Not calibrated. The next start calibrates first.';

  @override
  String get guideToolAssistant => 'Guiding Assistant';

  @override
  String get guideToolAssistantWhat =>
      'Watches the star drift with guiding off, then suggests settings for this mount and sky.';

  @override
  String get guideToolRun => 'Run';

  @override
  String get guideToolRunAgain => 'Run again';

  @override
  String get guideToolStop => 'Stop';

  @override
  String get guideToolNotRun => 'Not run yet';

  @override
  String get guideToolMeasuring => 'Measuring drift…';

  @override
  String guideToolDone(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: 'Done. $count suggestions to apply.',
      one: 'Done. 1 suggestion to apply.',
      zero: 'Done. Nothing to apply.',
    );
    return '$_temp0';
  }

  @override
  String guideToolFailed(String reason) {
    return 'Last run failed: $reason';
  }

  @override
  String get guideToolCalibrateFirst =>
      'Calibrate first: it needs a calibration to measure against.';

  @override
  String get guideSuggestExposure => 'Guide exposure';

  @override
  String get guideSuggestMinMoveRa => 'RA minimum move';

  @override
  String get guideSuggestMinMoveDec => 'Dec minimum move';

  @override
  String get guideSuggestDecBacklash => 'Dec backlash compensation';

  @override
  String get guideSuggestDecAlgorithm => 'Dec algorithm';

  @override
  String get focusNudgeIn => 'Nudge in';

  @override
  String get focusNudgeOut => 'Nudge out';

  @override
  String get focusNudgeStop => 'Stop';

  @override
  String focusNudgeSteps(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count steps',
      one: '1 step',
    );
    return '$_temp0';
  }

  @override
  String get focusMoveFailed => 'Couldn\'t move the focuser';

  @override
  String get focusStopFailed => 'Couldn\'t stop the focuser';

  @override
  String get focusCalibrationSection => 'Calibration';

  @override
  String get focusCalibrationReset => 'Reset focus calibration';

  @override
  String get focusCalibrationResetHint =>
      'The next autofocus runs a full sweep and calibrates again.';

  @override
  String get focusCalibrationResetAsk => 'Reset the focus calibration?';

  @override
  String get focusCalibrationResetCancel => 'Keep it';

  @override
  String get focusCalibrationResetConfirm => 'Reset';

  @override
  String get focusCalibrationResetDone => 'Focus calibration reset.';

  @override
  String get focusCalibrationResetAction => 'reset the focus calibration';

  @override
  String get guideDecMode => 'Dec guide mode';

  @override
  String get guideDecAuto => 'Auto';

  @override
  String get guideDecNorth => 'North';

  @override
  String get guideDecSouth => 'South';

  @override
  String get guideExposureOwn => 'The guider\'s own';

  @override
  String get focusNeedsFocuser =>
      'Connect a focuser to autofocus, or focus by hand on Manual.';

  @override
  String get shellMainCameraBusy =>
      'The main camera is busy: wait for its frame, or stop it.';

  @override
  String get albumTitle => 'Sky Album';

  @override
  String get albumBookMessier => 'Messier';

  @override
  String get albumBookCaldwell => 'Caldwell';

  @override
  String get albumBookRcw => 'RCW';

  @override
  String get dsoTypeGalaxy => 'Galaxy';

  @override
  String get dsoTypeGalaxyPair => 'Galaxy pair';

  @override
  String get dsoTypeGalaxyTriplet => 'Galaxy triplet';

  @override
  String get dsoTypeGalaxyGroup => 'Galaxy group';

  @override
  String get dsoTypeNebula => 'Nebula';

  @override
  String get dsoTypeEmissionNebula => 'Emission nebula';

  @override
  String get dsoTypeReflectionNebula => 'Reflection nebula';

  @override
  String get dsoTypePlanetaryNebula => 'Planetary nebula';

  @override
  String get dsoTypeSupernovaRemnant => 'Supernova remnant';

  @override
  String get dsoTypeCluster => 'Cluster';

  @override
  String get dsoTypeOpenCluster => 'Open cluster';

  @override
  String get dsoTypeGlobularCluster => 'Globular cluster';

  @override
  String get dsoTypeClusterNebula => 'Cluster + nebula';

  @override
  String get dsoTypeObject => 'Object';

  @override
  String get albumCatalogs => 'Catalogs';

  @override
  String albumBookCount(String book, int collected) {
    return '$book · $collected';
  }

  @override
  String albumCollected(int collected, int total) {
    return '$collected / $total collected';
  }

  @override
  String get albumTierBronze => 'Bronze';

  @override
  String get albumTierSilver => 'Silver';

  @override
  String get albumTierGold => 'Gold';

  @override
  String albumTierCount(String tier, int count) {
    return '$tier $count';
  }

  @override
  String get albumEmpty => 'Image a target to collect your first card';

  @override
  String albumShelfTitle(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count missing cards well placed tonight',
      one: '1 missing card well placed tonight',
    );
    return '$_temp0';
  }

  @override
  String get albumNotCollected => 'Not collected yet';

  @override
  String albumSetNumber(int number, int total) {
    return '$number / $total';
  }

  @override
  String get albumNight => 'Night';

  @override
  String get albumLights => 'Lights';

  @override
  String get albumSub => 'Sub';

  @override
  String get albumHours => 'Hours';

  @override
  String albumNightsTotal(int nights) {
    String _temp0 = intl.Intl.pluralLogic(
      nights,
      locale: localeName,
      other: '$nights nights',
      one: '1 night',
    );
    return 'Total · $_temp0';
  }

  @override
  String get albumDidYouKnow => 'Did you know?';

  @override
  String albumArtCredit(String credit) {
    return 'Art (cropped): $credit';
  }

  @override
  String get albumArtCreditOpenFailed => 'Couldn\'t open the license page';

  @override
  String get shellAlbum => 'Sky Album';

  @override
  String get albumImageThis => 'Image this target';

  @override
  String get albumAddTime => 'Add more time';

  @override
  String albumLoadFailed(String reason) {
    return 'Couldn\'t load the album: $reason';
  }

  @override
  String get albumNoServer => 'Connect to a rig to fill the album';

  @override
  String albumCardSemantics(String label, String name, String state) {
    return '$label $name, $state';
  }

  @override
  String get albumFilterAll => 'All';

  @override
  String get albumFilterCollected => 'Collected';

  @override
  String get albumFilterMissing => 'Missing';

  @override
  String albumNewCards(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count new cards',
      one: '1 new card',
    );
    return '$_temp0';
  }

  @override
  String albumTiersUp(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count tiers up',
      one: '1 tier up',
    );
    return '$_temp0';
  }

  @override
  String get albumCardNew => 'new';

  @override
  String get albumCardTierUp => 'up a tier';

  @override
  String albumSinceLastVisit(String changes) {
    return '$changes since your last visit';
  }

  @override
  String get wizardGuideCameraIsMainCamera =>
      'The Main Camera can\'t also be the Guide Camera. Guiding needs a separate Guide Camera, because two programs on one sensor break guiding. Pick another camera, or choose None.';

  @override
  String get wizardDarksSkippedPushFailed =>
      'Dark-library build skipped: the guider didn\'t take the new profile. Build it from the Guider panel once the profile push succeeds.';

  @override
  String get shellStorageSavedTo => 'Frames are saved to';

  @override
  String get shellStorageInternal => 'Internal storage';

  @override
  String shellStorageFreeOf(String free, String total) {
    return '$free free of $total';
  }

  @override
  String get shellStorageNotPlugged =>
      'No disk is plugged in. Frames can\'t be saved.';

  @override
  String get shellStorageUnreadable =>
      'Couldn\'t read the rig\'s storage. Frames can\'t be saved.';

  @override
  String get shellStorageFull => 'The disk is full. Frames can\'t be saved.';

  @override
  String get shellStorageChoose => 'Choose disk';

  @override
  String get shellStorageEject => 'Eject';

  @override
  String get shellStorageCheck => 'Check disk';

  @override
  String get searchSettingStorage => 'Storage';

  @override
  String get searchWordsStorage => 'disk drive frames save space eject usb';

  @override
  String get coolingFan => 'Cooling fan';

  @override
  String get coolingFanNone => 'None';

  @override
  String get coolingFanNotConnected => 'Chosen fan (not connected)';

  @override
  String get coolingFanChosenNote =>
      'This fan starts with the cooler and cannot be turned off while the camera is cooling.';

  @override
  String get coolingFanNoneNote =>
      'No fan is chosen, so Ara does not run a fan with the cooler.';

  @override
  String get coolingFanCouldntSave => 'Couldn\'t save the cooling fan';

  @override
  String get coolingFanAsk =>
      'Choose your camera\'s cooling fan so it runs with the cooler.';

  @override
  String get coolingFanAskFullUi =>
      'Choose your camera\'s cooling fan in Settings › Camera so it runs with the cooler.';

  @override
  String get coolingFanChoose => 'Choose';

  @override
  String get coolingFanNotNow => 'Not now';

  @override
  String get planFlats => 'Flats';

  @override
  String get planFlatsNone => 'None';

  @override
  String get shellStorageReading => 'Checking the disk…';

  @override
  String get shellStorageUnknownDisk => 'Unknown disk';

  @override
  String get storageDiskChooseTitle => 'Choose a disk for your frames';

  @override
  String get storageDiskRescan => 'Rescan';

  @override
  String get storageDiskUse => 'Use Disk';

  @override
  String get storageDiskEraseUse => 'Erase and Use Disk';

  @override
  String get storageDiskNoneFound =>
      'No external disks found.\n\nConnect a USB drive or SSD to the server, then Rescan.';

  @override
  String get storageDiskInUse => 'In use';

  @override
  String get storageDiskEraseChange => 'Erase and change format';

  @override
  String storageDiskEraseChangeNote(String fileSystem) {
    return 'This disk already works as a store ($fileSystem). Turn this on only to reformat it — everything on it will be lost.';
  }

  @override
  String get storageDiskMustErase => 'This disk must be erased';

  @override
  String get storageDiskMustEraseNote =>
      'ARA saves frames in a format this disk doesn\'t use, so it has to be erased first. Everything on it will be lost.';

  @override
  String get storageDiskExfat => 'exFAT — take the drive with you';

  @override
  String get storageDiskExfatNote =>
      'Plugs straight into Windows and Mac at home. After a power cut, run Check disk.';

  @override
  String get storageDiskExt4 => 'ext4 — drive lives on the rig';

  @override
  String get storageDiskExt4Note =>
      'Journaled (self-healing after power cuts). Readable on Linux computers; Windows and Mac can\'t read it — frames arrive there via the backup mirror.';

  @override
  String storageDiskTypeLabel(String label) {
    return 'Type “$label” to confirm:';
  }

  @override
  String get storageDiskTypeErase =>
      'This disk has no label — type ERASE to confirm:';

  @override
  String get storageDiskErasing => 'Erasing and connecting the disk…';

  @override
  String get storageDiskConnecting => 'Connecting the disk…';

  @override
  String storageDiskNowUsed(String name) {
    return '$name is now used for frames.';
  }

  @override
  String get storageDiskReadAction => 'read the server\'s disks';

  @override
  String get storageConfigureNotExt4 =>
      'This disk needs to be erased before ARA can use it.';

  @override
  String get storageConfigureLabelMismatch =>
      'The disk changed since the list was loaded. Rescan and try again.';

  @override
  String get storageConfigurePreviousBusy =>
      'The disk frames are saved to now is in use, so ARA can\'t switch away from it. Close anything reading it, then try again.';

  @override
  String get storageConfigureBusy =>
      'The disk is in use. Close anything reading it, then try again.';

  @override
  String get storageConfigureSystemDisk =>
      'That disk holds the server\'s operating system and can\'t be used.';

  @override
  String get storageConfigureDisconnected =>
      'The disk disconnected. Reconnect it and rescan.';

  @override
  String get storageConfigureHelperMissing =>
      'The server is missing part of its installation. Reinstall openastroara-server.';

  @override
  String get storageConfigureMkfsFailed =>
      'Erasing the disk failed. See the server log for details.';

  @override
  String get storageConfigureMountFailed =>
      'The disk was prepared but could not be connected.';

  @override
  String get storageDiskContentsChecking => 'Checking what is on this disk…';

  @override
  String storageDiskContentsFrames(int count, String date) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: 'This disk holds $count frames. The newest is from $date.',
      one: 'This disk holds 1 frame, from $date.',
    );
    return '$_temp0';
  }

  @override
  String storageDiskContentsFramesUndated(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: 'This disk holds $count frames.',
      one: 'This disk holds 1 frame.',
    );
    return '$_temp0';
  }

  @override
  String get storageDiskContentsReadFailed =>
      'ARA couldn\'t read what is on this disk. Erasing it loses everything on it.';

  @override
  String get storageDiskContentsNone => 'There are no frames on this disk.';

  @override
  String storageDiskContentsNotCopied(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count frames have not been copied to another disk by ARA.',
      one: '1 frame has not been copied to another disk by ARA.',
    );
    return '$_temp0';
  }

  @override
  String get storageDiskContentsUnknown =>
      'ARA can\'t see what is on this disk while it isn\'t connected. Erasing it loses everything on it.';

  @override
  String get storageConfigureExfatUnsupported =>
      'This server can\'t read exFAT drives. Erase the drive as ext4 and ARA can use it.';

  @override
  String storageConfigureFailed(String name) {
    return 'Could not use $name.';
  }

  @override
  String get storageCheckTitle => 'Check the store drive?';

  @override
  String get storageCheckBody =>
      'The drive is briefly taken offline while its filesystem is verified and repaired if needed. Takes seconds to a couple of minutes; capture must be idle.';

  @override
  String get storageNoDriveEject => 'No store drive to eject.';

  @override
  String get storageNoDriveCheck =>
      'No store drive to check — connect one first.';

  @override
  String deviceChooserTitle(String type) {
    return 'Choose $type';
  }

  @override
  String get deviceChooserRescan => 'Re-scan';

  @override
  String get deviceChooserManualWheel =>
      'No driver: you swap filters by hand and Ara asks for each one';

  @override
  String get deviceChooserNoneTitle => 'No devices found';

  @override
  String get deviceChooserNoneBody =>
      'No Alpaca responder answered on Ara\'s subnet — most often AlpacaBridge (ARA\'s equipment hub) is not installed or its service is stopped. On Ara host, check:';

  @override
  String get deviceChooserOtherHost =>
      'A driver on another host must be reachable from Ara\'s subnet (UDP 32227).';

  @override
  String get deviceChooserFailed => 'Discovery failed';

  @override
  String get deviceChooserRetry => 'Retry';

  @override
  String get deviceChooserNotConnected =>
      'Not connected — connect to your rig to save this.';

  @override
  String get bugReviewTitle => 'Before you share this bug report';

  @override
  String get bugReviewDownload => 'Download anyway';

  @override
  String get bugReviewContains => 'The bundle contains:';

  @override
  String get bugReviewNoSources => '• Ara\'s recent log files';

  @override
  String get bugReviewProfileRedacted =>
      '• your profile with coordinates, serial numbers, hostnames, usernames in paths, private network addresses and tokens scrubbed';

  @override
  String get bugReviewProfileCoarse =>
      '• your profile with the site rounded to ~111 km — it may still include equipment credentials, API / notification tokens and network addresses';

  @override
  String get bugReviewProfileFull =>
      '• your full profile — which may include equipment credentials, API / notification tokens, precise observatory coordinates, and network addresses';

  @override
  String get bugReviewSystemRedacted =>
      '• system info, with this device\'s username scrubbed from its path';

  @override
  String get bugReviewSystemFull =>
      '• system info, including this device\'s filesystem path';

  @override
  String get bugReviewClientLog =>
      '• this app\'s own error log, saved as a separate file beside the ZIP';

  @override
  String bugReviewSize(String size) {
    return 'Approximate size: $size.';
  }

  @override
  String get bugReviewTrust =>
      'Only share it with people you trust (e.g. the developers) — treat it like a password.';

  @override
  String get bugReviewNone => 'none';

  @override
  String get bugReviewOneFile => '1 file';

  @override
  String bugReviewFiles(String count) {
    return '$count files';
  }

  @override
  String get bugReviewCut => ', the largest cut to their newest part';

  @override
  String get bugSourceAra => 'Ara\'s own logs';

  @override
  String get bugSourceAraNina => 'Ara\'s NINA-layer logs';

  @override
  String get bugSourceAlpacaBridge =>
      'AlpacaBridge logs (camera, mount, focuser, wheel)';

  @override
  String get bugSourceGuider => 'guider (PHD2) debug and guide logs';

  @override
  String get bugSourceSystem => 'package install history';

  @override
  String get bugSourceJournal =>
      'system journal (Ara, AlpacaBridge, the guider, the kernel)';

  @override
  String takeoverWants(String name) {
    return '$name wants to connect';
  }

  @override
  String get takeoverDetail =>
      'Only one client can control the observatory at a time. Allowing hands control to the other client and disconnects this one; the running sequence continues either way.';

  @override
  String get takeoverKeep => 'Keep me connected';

  @override
  String get takeoverAllow => 'Allow';

  @override
  String filterSwapInstall(String target) {
    return 'Install the $target filter';
  }

  @override
  String filterSwapDetail(String target) {
    return 'Swap in $target, then confirm. A run waiting on it continues when you do.';
  }

  @override
  String filterSwapDetailFrom(String current, String target) {
    return 'Take out $current and swap in $target, then confirm. A run waiting on it continues when you do.';
  }

  @override
  String get filterSwapCancel => 'Cancel swap';

  @override
  String get filterSwapLater => 'Later';

  @override
  String filterSwapIsIn(String target) {
    return '$target is in';
  }

  @override
  String get filterSwapCouldntConfirm => 'Couldn\'t confirm the filter';

  @override
  String get filterSwapCouldntCancel => 'Couldn\'t cancel the swap';

  @override
  String get bugCardTitle => 'Send me a bug report';

  @override
  String get bugCardBody =>
      'Bundles the rig\'s logs (Ara, AlpacaBridge, the guider, the system journal) + your profile + system info into a ZIP you can attach to a report.';

  @override
  String bugCardPacking(String size) {
    return 'Packing the logs on the rig… $size read';
  }

  @override
  String get bugCardCovers => 'Covers: ';

  @override
  String get bugCardSharing => 'Sharing: ';

  @override
  String get bugCardAppLog => 'This app\'s error log';

  @override
  String get bugCardPrepare => 'Prepare & download';

  @override
  String get bugCardSaveLog => 'Save client log';

  @override
  String get bugRangeNight => 'Last night (whole rig)';

  @override
  String get bugRange3Days => 'Last 3 days (whole rig)';

  @override
  String get bugRange7Days => 'Last 7 days (whole rig)';

  @override
  String get bugRangeAraOnly => 'Ara logs only';

  @override
  String get bugModeFull => 'Include everything';

  @override
  String get bugModeFullNote => 'best for debugging';

  @override
  String get bugModeCoarse => 'Coarse GPS only';

  @override
  String get bugModeCoarseNote => 'site rounded to ~111 km';

  @override
  String get bugModeRedacted => 'Redact all sensitive items';

  @override
  String get bugModeRedactedNote =>
      'coordinates, serials, hostnames, usernames in paths, private IPs, tokens';

  @override
  String get bugPickTitle => 'Choose where to save the bug report';

  @override
  String bugSaved(String name) {
    return 'Saved $name';
  }

  @override
  String bugSavedBoth(String name, String log) {
    return 'Saved $name and $log';
  }

  @override
  String bugFailed(String reason) {
    return 'Bug report failed: $reason';
  }

  @override
  String get bugFailStillPacking =>
      'the rig is still packing the logs; try again in a moment';

  @override
  String get bugFailCouldNotPack => 'the rig could not pack the logs';

  @override
  String get bugFailTimeout => 'Ara took too long to respond.';

  @override
  String get bugFailUnreachable => 'could not reach Ara.';

  @override
  String bugFailStatus(String status) {
    return 'Ara returned an error ($status).';
  }

  @override
  String get bugFailTalking => 'something went wrong talking to Ara.';

  @override
  String get bugFailUnexpected => 'an unexpected error occurred.';

  @override
  String get clientLogPickTitle => 'Choose where to save the client log';

  @override
  String clientLogSaved(String path) {
    return 'Saved $path';
  }

  @override
  String get clientLogSaveAction => 'save the log';

  @override
  String get storageEjected => 'Ejected — safe to unplug the drive.';

  @override
  String get storageEjectBusy =>
      'The drive is busy — wait for writes to finish and try again.';

  @override
  String storageEjectFailed(String reason) {
    return 'Eject failed: $reason';
  }

  @override
  String get storageEjectAction => 'eject the drive';

  @override
  String get storageCheckRepaired =>
      'Disk checked — repairs were made. Worth re-running \"Find frames on disk\".';

  @override
  String get storageCheckClean => 'Disk checked — clean.';

  @override
  String storageCheckFailed(String reason) {
    return 'Check failed: $reason';
  }

  @override
  String get storageCheckAction => 'check the disk';

  @override
  String get sessionTransferredTitle => 'Session transferred';

  @override
  String get sessionTransferredDetail =>
      'Another client took control of the observatory, so this client was disconnected. The running sequence is unaffected — it continues on Ara.';

  @override
  String get storageCheckRepairedSimple => 'Disk checked — repairs were made.';

  @override
  String get storageDiskNotFormatted => 'not formatted';

  @override
  String storageDiskNeedsErasing(String fileSystem) {
    return 'needs erasing ($fileSystem)';
  }

  @override
  String storageDiskInUseAt(String path) {
    return 'in use at $path';
  }

  @override
  String get deviceChooserFindAction => 'find devices';

  @override
  String get stopMountAction => 'stop the mount';

  @override
  String get stopMountNoServer =>
      'Stop Mount was not issued — no server connection.';

  @override
  String stopMountFailed(String reason) {
    return '$reason Try Stop Mount again.';
  }

  @override
  String get focusChartFirstProbe => 'Waiting for the first probe…';

  @override
  String get focusChartEmpty => 'Run autofocus to see the V-curve.';

  @override
  String get focusChartPosition => 'Focuser position';

  @override
  String focusChartFit(int position) {
    return 'fit $position';
  }

  @override
  String focusChartCalibrated(int position) {
    return 'calibrated $position';
  }

  @override
  String focusChartFocus(int position) {
    return 'focus $position';
  }

  @override
  String get simpleGuiderCalibrationFailed =>
      'The guider could not calibrate, so it is guiding on its earlier calibration. Guiding may be less accurate tonight.';

  @override
  String get simpleDewWarning =>
      'Dew may be forming: stars are swelling at high humidity. Wipe the optics or turn on the dew heaters. Scheduled autofocus waits until it clears.';

  @override
  String simpleDeviceDidNotConnect(String device) {
    return '$device didn\'t connect. Check it\'s powered and plugged in, then Reconnect.';
  }

  @override
  String get simpleRigConnectTitle => 'Connect to your rig';

  @override
  String get simpleRigGoOnline => 'Go online';

  @override
  String simpleRigDropped(String rig) {
    return 'The link to $rig dropped. A running plan keeps going on the rig.';
  }

  @override
  String get simpleRigOfflineHint =>
      'You are planning offline. Connect a rig to image.';

  @override
  String get simpleRigThisSession => 'This session';

  @override
  String get simpleRigOthers => 'Other rigs';

  @override
  String get simpleRigRigs => 'Rigs';

  @override
  String get simpleRigFound => 'Found';

  @override
  String get simpleRigSavedNotSeen => 'Saved, not seen now';

  @override
  String get simpleRigConnecting => 'Connecting…';

  @override
  String get simpleRigConnect => 'Connect';

  @override
  String get simpleRigLooking => 'Looking for rigs on this network…';

  @override
  String simpleRigNoAnswer(String address) {
    return 'No rig answered at $address. Check the rig is on and on this network, or try its name.';
  }

  @override
  String get simpleRigAddByAddress => 'Add by address';

  @override
  String get simpleRigAddressHint => 'astro-pi.local or 192.0.2.24';

  @override
  String get simpleRigAddressInvalid =>
      'Type the rig\'s name or address, for example astro-pi.local or 192.0.2.24:5555.';

  @override
  String get deviceMountPosition => 'Position';

  @override
  String get deviceTrackingSpeed => 'Tracking speed';

  @override
  String get trackingRateSidereal => 'Sidereal';

  @override
  String get trackingRateLunar => 'Lunar';

  @override
  String get trackingRateSolar => 'Solar';

  @override
  String get trackingRateKing => 'King';

  @override
  String deviceTrackingOffSidereal(String rate) {
    return 'Tracking at the $rate rate. Back to Sidereal for stars and galaxies.';
  }

  @override
  String get deviceCouldntSetTrackingSpeed =>
      'Couldn\'t change the tracking speed';

  @override
  String get padSlow => 'Slow';

  @override
  String get padMedium => 'Medium';

  @override
  String get padFast => 'Fast';

  @override
  String get padSpeed => 'Speed';

  @override
  String get padNorth => 'N';

  @override
  String get padSouth => 'S';

  @override
  String get padEast => 'E';

  @override
  String get padWest => 'W';

  @override
  String get padMoveNorth => 'Move north';

  @override
  String get padMoveSouth => 'Move south';

  @override
  String get padMoveEast => 'Move east';

  @override
  String get padMoveWest => 'Move west';

  @override
  String get padMoveNorthEast => 'Move north-east';

  @override
  String get padMoveNorthWest => 'Move north-west';

  @override
  String get padMoveSouthEast => 'Move south-east';

  @override
  String get padMoveSouthWest => 'Move south-west';

  @override
  String get padStopMount => 'Stop the mount';

  @override
  String get padUnparkToMove => 'Unpark to move the mount';

  @override
  String get padPausePlan => 'Pause the plan to move the mount';

  @override
  String get padPolarRunning => 'Polar alignment is running';

  @override
  String get padStopGuiding => 'Stop guiding to move the mount';

  @override
  String get padSlewing => 'Moving to a target: Stop ends it';

  @override
  String get padNoRates => 'This mount can\'t be moved by hand';

  @override
  String get pointingLabel => 'Pointing';

  @override
  String get pointingSolved => 'Pointing (solved)';

  @override
  String get pointingCheck => 'Check';

  @override
  String get pointingCheckHint => 'Take a quick frame and plate-solve it';

  @override
  String get pointingSolving => 'Taking a frame and solving…';

  @override
  String pointingFrom(String separation, String target) {
    return '$separation from $target';
  }

  @override
  String pointingFromLastGoto(String separation) {
    return '$separation from the last go-to';
  }

  @override
  String get pointingOnTarget => 'On target';

  @override
  String pointingMountOff(String separation) {
    return 'Mount reports $separation off';
  }

  @override
  String pointingRotatorSynced(String angle) {
    return 'Rotator synced to $angle';
  }

  @override
  String get pointingCouldntSolve => 'Couldn\'t solve this frame';

  @override
  String get pointingCentre => 'Center';

  @override
  String get pointingCentring => 'Centering…';

  @override
  String get pointingSync => 'Sync';

  @override
  String get pointingSynced => 'Synced';

  @override
  String get pointingSyncedPill => 'The mount is synced to the solved position';

  @override
  String get pointingCouldntSync => 'Couldn\'t sync the mount';

  @override
  String get pointingCouldntCentre => 'Couldn\'t center';

  @override
  String get pointingOpenPlateSolving => 'Plate solving settings';

  @override
  String get pointingOpenOptics => 'Optics settings';

  @override
  String get pointingOpenCamera => 'Camera settings';

  @override
  String get pointingCameraBusy =>
      'The camera is taking another frame. Check again once it finishes.';

  @override
  String get devicePlanRunningHomeAnyway =>
      'A plan is running. Send the mount home anyway?';

  @override
  String get deviceHomeAnyway => 'Home anyway';

  @override
  String get pointingNoCamera =>
      'Connect the main camera to check where it points';

  @override
  String get monitorTitle => 'Monitor';

  @override
  String monitorBattery(int level) {
    return 'This device\'s battery: $level %';
  }

  @override
  String get monitorLastKnown => 'Last known';

  @override
  String monitorLinkLost(String age) {
    return 'Lost contact with the rig · $age ago';
  }

  @override
  String get monitorLinkLostReason =>
      'A running plan carries on by itself on the rig.';

  @override
  String monitorDeviceDropped(String device) {
    return '$device disconnected';
  }

  @override
  String get monitorDeviceDroppedReason =>
      'Reconnect it from its window. Tap its tile to open it.';

  @override
  String get monitorUnsafeReason =>
      'The rig reacts as your safety settings say.';

  @override
  String get monitorStarLost => 'Guide star lost';

  @override
  String get monitorStarLostReason =>
      'Often clouds passing. Ara holds the next frame until guiding is back.';

  @override
  String get monitorStarLostWord => 'Star lost';

  @override
  String monitorLowDisk(String free) {
    return 'Low disk space · $free free';
  }

  @override
  String get monitorLowDiskReason =>
      'The queue\'s frames won\'t all fit. Free space or shorten the queue.';

  @override
  String monitorCooler(String target, String sensor) {
    return 'Cooler can\'t reach $target · sensor at $sensor';
  }

  @override
  String get monitorCoolerReason =>
      'Frames still save, but they won\'t match your darks.';

  @override
  String get monitorFlippingReason =>
      'Imaging carries on by itself when it is done.';

  @override
  String get monitorWaitingForFlipReason => 'No frames until the mount flips.';

  @override
  String get monitorPaused => 'Paused';

  @override
  String get monitorWaitingForYou => 'Plan paused · waiting for you';

  @override
  String get monitorWaitingForYouWord => 'Waiting for you';

  @override
  String monitorWaitingForDark(String time) {
    return 'Waiting for dark · plan starts at $time';
  }

  @override
  String get monitorWaitingForDarkReason => 'Imaging starts on its own.';

  @override
  String get monitorNoPlan => 'Rig ready · no plan running';

  @override
  String get monitorNoPlanReason => 'Start a plan and this screen fills in.';

  @override
  String get monitorNoPlanBig => 'No plan';

  @override
  String get monitorNoPlanRunning => 'No plan running';

  @override
  String monitorAllGood(String target) {
    return 'All good · imaging $target';
  }

  @override
  String get monitorAllGoodReason => 'Nothing needs you.';

  @override
  String monitorAllGoodParks(String time) {
    return 'Nothing needs you. The plan ends at $time, then the mount parks.';
  }

  @override
  String get monitorNow => 'Now';

  @override
  String get monitorRunning => 'Running';

  @override
  String get monitorFlipping => 'Flipping';

  @override
  String get monitorWaiting => 'Waiting';

  @override
  String get monitorIdle => 'Idle';

  @override
  String get monitorNothingRunning => 'Nothing running';

  @override
  String get monitorLeftInFrame => 'left in this frame';

  @override
  String get monitorUntilStart => 'until the plan starts';

  @override
  String get monitorUntilFlip => 'until the flip';

  @override
  String monitorFrameOf(int frame, int frames, String left, String target) {
    return 'Frame $frame of $frames · about $left left on $target';
  }

  @override
  String get monitorUpNext => 'Up next';

  @override
  String get monitorTonightsBest => 'Tonight\'s best';

  @override
  String monitorUpFrame(int frame, String filter) {
    return 'Frame $frame · $filter';
  }

  @override
  String get monitorUpPark => 'Mount parks';

  @override
  String get monitorThen => 'then';

  @override
  String monitorAltitudeUp(String alt) {
    return '$alt° up';
  }

  @override
  String get monitorTarget => 'Target';

  @override
  String get monitorNoTarget => 'No target picked';

  @override
  String monitorImagingTo(String time) {
    return 'Imaging to $time';
  }

  @override
  String monitorMaxAltitude(String alt) {
    return 'max $alt°';
  }

  @override
  String monitorTransits(String time) {
    return 'Transits $time';
  }

  @override
  String monitorMoon(int pct) {
    return 'Moon $pct %';
  }

  @override
  String get monitorTracking => 'Tracking';

  @override
  String get monitorQueued => 'Queued';

  @override
  String get monitorParked => 'Parked';

  @override
  String get monitorFlipAutomatic => 'Automatic';

  @override
  String get monitorFlipComingUp => 'Coming up';

  @override
  String monitorFlipAt(String time) {
    return 'At $time';
  }

  @override
  String get monitorFlipWaitsForFrame =>
      'Waits for the frame in hand, then flips';

  @override
  String get monitorFlipOff => 'Automatic flip is off';

  @override
  String get monitorFlipNone => 'No flip tonight';

  @override
  String get monitorDisconnected => 'Disconnected';

  @override
  String monitorSince(String time, String duration) {
    return 'Since $time · $duration';
  }

  @override
  String get monitorPastLimit => 'Past limit';

  @override
  String get monitorCloud => 'Cloud';

  @override
  String get monitorSkyQuality => 'Sky quality';

  @override
  String get monitorAir => 'Air';

  @override
  String get monitorDewMargin => 'Above dew point';

  @override
  String get monitorHumidity => 'Humidity';

  @override
  String get monitorRain => 'Rain';

  @override
  String get monitorWind => 'Wind';

  @override
  String monitorGusts(String kmh) {
    return 'gusts $kmh';
  }

  @override
  String monitorLimit(String value) {
    return 'limit $value';
  }

  @override
  String monitorLastDither(String time) {
    return 'Last dither $time';
  }

  @override
  String get monitorLastFrame => 'Last frame';

  @override
  String get monitorHfr => 'HFR';

  @override
  String get monitorStars => 'Stars';

  @override
  String get monitorSkyBackground => 'Sky background';

  @override
  String get monitorClippedStars => 'Clipped stars';

  @override
  String monitorAutofocusSince(String time, String delta) {
    return 'Autofocus $time · $delta since';
  }

  @override
  String get monitorTonight => 'Tonight';

  @override
  String monitorDarkSpan(String start, String end, String imaged) {
    return 'Dark $start to $end · $imaged imaged';
  }

  @override
  String get monitorNoDark => 'Set the site to see tonight\'s dark';

  @override
  String get monitorLinkLostShort => 'Lost contact with the rig';

  @override
  String get actionAbortCapture => 'abort the capture';

  @override
  String get actionApplyToFrames => 'apply that to the selected frames';

  @override
  String get actionBuildCalibration => 'build the calibration';

  @override
  String get actionCancelCopy => 'cancel the copy';

  @override
  String get actionCheckDisk => 'check the disk for frames';

  @override
  String get actionCleanPreviewCache => 'clean the preview cache';

  @override
  String get actionCleanSkyPhotoCache => 'clean the sky photo cache';

  @override
  String get actionClearCeiling => 'clear the ceiling';

  @override
  String get actionConfirmFrame => 'confirm the frame arrived';

  @override
  String get actionConnect => 'connect';

  @override
  String get actionDownload => 'download that';

  @override
  String get actionExportFrames => 'export those frames';

  @override
  String get actionExportStats => 'export the stats';

  @override
  String get actionGenerateCalibration => 'generate the calibration';

  @override
  String get actionListFolders => 'list the folders';

  @override
  String get actionLoadDarkLibrary => 'load the dark library';

  @override
  String get actionLoadFaults => 'load the fault history';

  @override
  String get actionLoadFrames => 'load the frames';

  @override
  String get actionLoadLibrary => 'load your library';

  @override
  String get actionLoadLogs => 'load the logs';

  @override
  String get actionLoadNotifications => 'load your notifications';

  @override
  String get actionLoadOlderLogs => 'load older logs';

  @override
  String get actionLoadPreview => 'load the preview';

  @override
  String get actionLoadSessions => 'load the sessions';

  @override
  String get actionLoadSettings => 'load your saved settings';

  @override
  String get actionLookForEquipment => 'look for equipment';

  @override
  String get actionPushGuiderProfile => 'push the guider profile';

  @override
  String get actionPushTime => 'push the time';

  @override
  String get actionRateFrame => 'rate the frame';

  @override
  String get actionReadCamera => 'read the camera';

  @override
  String get actionReadClock => 'read the server clock state';

  @override
  String get actionReadServerDisks => 'read the server\'s disks';

  @override
  String get actionRearmSafety => 're-arm safety';

  @override
  String get actionRefitSkyModel => 'refit the sky model';

  @override
  String get actionRestartRig => 'restart your rig';

  @override
  String get actionResumeSequence => 'resume the sequence';

  @override
  String get actionSave => 'save that';

  @override
  String get actionSaveHorizon => 'save the horizon skyline';

  @override
  String get actionSaveSlotLabels => 'save the slot labels';

  @override
  String get actionSendCalibrationChoice => 'send the calibration choice';

  @override
  String get actionSequenceAction => 'do that sequence action';

  @override
  String get actionSolveFrame => 'solve the frame';

  @override
  String get actionStarDatabase =>
      'work out which star database this rig needs';

  @override
  String get actionStartCopy => 'start the copy';

  @override
  String get actionTakeExposure => 'take that exposure';

  @override
  String get actionTakeVerification => 'take the verification image';

  @override
  String get actionUpdateTag => 'update the tag';

  @override
  String get actionBackUpEntry => 'back up that entry';

  @override
  String get actionKeepBackupGoing => 'keep the backup stream going';

  @override
  String get actionPushGpsFix => 'push the GPS fix';

  @override
  String get actionRebuildStats => 'rebuild the stats catalog';

  @override
  String get actionStartCopyingFrames => 'start copying frames';

  @override
  String backupStreamHiccup(String reason) {
    return 'Backup stream hiccup: $reason';
  }

  @override
  String backupEntryFailing(String id, String reason) {
    return 'Backup of $id failing: $reason';
  }

  @override
  String gpsFixRefused(String reason) {
    return 'Fix read, but the rig refused it: $reason';
  }

  @override
  String get gpsDaemonTooOld =>
      ' — update Ara Server on the rig; this version predates GPS on this computer.';

  @override
  String get calibrationDarks => 'Darks';

  @override
  String get calibrationBias => 'Bias';

  @override
  String get calibrationTakeFlats => 'Take flats';

  @override
  String get calibrationTakeDarks => 'Take darks';

  @override
  String calibrationTaking(int done, int total) {
    return 'Calibration frames · $done of $total';
  }

  @override
  String calibrationStopped(int done, int total) {
    return 'Stopped · $done of $total';
  }

  @override
  String get calibrationDone => 'Calibration frames done';

  @override
  String get calibrationActionShoot => 'start the calibration frames';

  @override
  String get calibrationActionStop => 'stop the calibration frames';

  @override
  String get shellModeCalibration => 'Calibration';

  @override
  String get calibrationTabsGroup => 'Calibration tabs';

  @override
  String get calibrationTabFlats => 'Flats';

  @override
  String get calibrationTabDarks => 'Darks & bias';

  @override
  String get calibrationTabGuide => 'Guide Camera';

  @override
  String calibrationChipMissing(int count) {
    return '$count missing';
  }

  @override
  String get calibrationChipReady => 'Ready';

  @override
  String get calibrationChipNoLights => 'No lights';

  @override
  String get calibrationNoLightsYet =>
      'No plan lights yet. Build a dark library to reuse for weeks.';

  @override
  String get calibrationCovered => 'Covered';

  @override
  String get calibrationMissing => 'Missing';

  @override
  String calibrationFlatsFrom(String date) {
    return 'Flats from $date';
  }

  @override
  String get calibrationAddToQueue => 'Add to queue';

  @override
  String get calibrationLensCapPrompt =>
      'Put the lens cap on the scope, then press Resume to take the darks.';

  @override
  String get calibrationPlanRunning =>
      'A plan is running. Calibration would have to wait for it: add the darks and bias to the queue instead?';

  @override
  String get calibrationPlanRunningNote =>
      'A plan is running: calibration frames go in the queue.';

  @override
  String get calibrationNotNow => 'Not now';

  @override
  String get calibrationConnectCamera => 'Connect the camera first.';

  @override
  String get calibrationShutterStop => 'Stop calibration';

  @override
  String get calibrationNothingMissing => 'Nothing missing.';

  @override
  String get calibrationGuideNoGuider =>
      'Connect the Guider to build its dark library and bad-pixel map.';

  @override
  String get calibrationGuideDarks => 'Dark library';

  @override
  String calibrationGuideCovers(String min, String max) {
    return 'Covers $min to $max';
  }

  @override
  String get calibrationGuideNotBuilt => 'Not built';

  @override
  String get calibrationGuideUse => 'Use while guiding';

  @override
  String get calibrationGuideRebuild => 'Rebuild';

  @override
  String get calibrationGuideDelete => 'Delete';

  @override
  String get calibrationGuideDeleteDarksTitle => 'Delete the dark library?';

  @override
  String get calibrationGuideDeleteDefectsTitle => 'Delete the bad-pixel map?';

  @override
  String get calibrationGuideDeleteBody =>
      'Building it again takes several minutes of frames with the guide scope covered.';

  @override
  String get calibrationGuideDefect => 'Bad-pixel map';

  @override
  String get calibrationGuideCover => 'Cover the guide scope, then build.';

  @override
  String get calibrationGuideCovered => 'Covered, build';

  @override
  String get calibrationGuideStopFirst =>
      'Stop guiding to build: a plan would lose its guide star.';

  @override
  String get calibrationNoQueue =>
      'Queue a target first: the darks and bias run after it.';

  @override
  String get calibrationAlreadyQueued =>
      'The queue already has its calibration frames.';

  @override
  String get calibrationActionQueue =>
      'add the calibration frames to the queue';

  @override
  String get calibrationActionBuildLibrary => 'build the dark library';

  @override
  String get calibrationActionGuide => 'change the guide camera\'s calibration';

  @override
  String get searchCalibrationWords =>
      'flats darks bias calibration frames dark library';

  @override
  String get focusOffsets => 'Offsets';

  @override
  String get focusOffsetsTitle => 'Focus offsets';

  @override
  String focusOffsetsProgress(String filter, int index, int count) {
    return '$filter · $index of $count';
  }

  @override
  String get focusOffsetsIntro =>
      'Point at a star field, pick the filters, then Start. Each filter is focused once, the reference before and after.';

  @override
  String focusOffsetsReference(String filter) {
    return 'Offsets are measured from $filter, the reference.';
  }

  @override
  String focusOffsetsReferenceChip(String filter) {
    return '$filter · reference';
  }

  @override
  String get focusOffsetsPicker => 'Filters to measure';

  @override
  String get focusOffsetsStart => 'Calibrate focus offsets';

  @override
  String get focusOffsetsCancel => 'Stop calibrating focus offsets';

  @override
  String focusOffsetsMeasuring(String filter, int index, int count) {
    return 'Measuring $filter · $index of $count';
  }

  @override
  String get focusOffsetsStarting => 'Starting…';

  @override
  String get focusOffsetsPlanRunning =>
      'Wait for the plan to finish: calibrating changes filters and focus.';

  @override
  String get focusOffsetsFocusBusy =>
      'Wait for the other focus routine to finish.';

  @override
  String get focusOffsetsNeedsCamera =>
      'Connect the camera to calibrate focus offsets.';

  @override
  String get focusOffsetsNeedsFocuser =>
      'Connect the focuser to calibrate focus offsets.';

  @override
  String get focusOffsetsNeedsWheel =>
      'Connect the filter wheel to calibrate focus offsets.';

  @override
  String get focusOffsetsNeedsMotorisedWheel =>
      'Calibrating needs a motorised filter wheel: a manual one can\'t change filters by itself.';

  @override
  String get focusOffsetsNeedsAbsoluteFocuser =>
      'Calibrating needs an absolute focuser: a relative one has no positions to compare.';

  @override
  String get focusOffsetsPickOne =>
      'Pick at least one filter besides the reference.';

  @override
  String get focusOffsetsResults => 'Offsets measured';

  @override
  String get focusOffsetsStopped =>
      'Stopped. Your saved offsets are unchanged.';

  @override
  String get focusOffsetsFailed =>
      'The calibration failed. Your saved offsets are unchanged.';

  @override
  String get focusOffsetsColFilter => 'Filter';

  @override
  String get focusOffsetsColOffset => 'Offset';

  @override
  String get focusOffsetsColPrevious => 'Before';

  @override
  String get focusOffsetsRowFailed => 'Failed';

  @override
  String get focusOffsetsRowSkipped => 'Not measured';

  @override
  String get focusOffsetsRowPending => 'Waiting';

  @override
  String focusOffsetsWeakFit(String r2) {
    return 'Weak curve fit (R² $r2): run again if the sky has settled.';
  }

  @override
  String focusOffsetsDrift(int steps, int cfz) {
    return 'The reference moved $steps steps between its two runs, more than its critical focus zone ($cfz steps). Run it again for steadier numbers.';
  }

  @override
  String get focusOffsetsDriftNotChecked =>
      'Drift not checked: the reference\'s second run didn\'t measure.';

  @override
  String get focusOffsetsSave => 'Save offsets';

  @override
  String get focusOffsetsDiscard => 'Discard';

  @override
  String get focusOffsetsSaved =>
      'Offsets saved. Autofocus after a filter change can now be turned off.';

  @override
  String deviceFocusOffset(String offset) {
    return 'Offset $offset steps';
  }

  @override
  String get searchFocusOffsetsWords =>
      'focus offsets filter offsets calibrate';

  @override
  String get calibrationLightGroup => 'Light';

  @override
  String get calibrationLightFront => 'Panel or screen';

  @override
  String get calibrationLightSky => 'Twilight sky';

  @override
  String get calibrationLightPanelNote => 'The flat panel lights itself.';

  @override
  String get calibrationLightScreenNote =>
      'No flat panel connected: the run asks you to put a screen or light box over the scope first.';

  @override
  String get calibrationLightSkyNote =>
      'The flats wait for twilight and use the sky.';

  @override
  String get calibrationLightPrompt =>
      'Put the screen or light box over the scope, then press Resume to take the flats.';

  @override
  String calibrationPillProgress(int done, int total) {
    return '$done of $total';
  }

  @override
  String get calibrationGuideBuilding => 'Building…';

  @override
  String calibrationGuideBuildProgress(int done, int total) {
    return 'Building · $done of $total';
  }

  @override
  String calibrationGuideBuildFailed(String what) {
    return '$what wasn\'t built';
  }

  @override
  String calibrationGuideBuilt(String what) {
    return '$what built';
  }

  @override
  String calibrationFlatsForSession(String target) {
    return 'Tonight\'s filters · $target';
  }

  @override
  String get calibrationOnFileTag => 'On file';

  @override
  String get calibrationGuideBuildDarks => 'Build dark library';

  @override
  String get calibrationGuideBuildDefects => 'Build bad-pixel map';

  @override
  String get calibrationGuideCoverNote =>
      'Building asks you to cover the guide scope first.';

  @override
  String get calibrationGuideReady => 'Built';

  @override
  String get calibrationFlatsMissing => 'Flats missing';

  @override
  String get calibrationFlatsOnFile => 'Flats on file';

  @override
  String get calibrationDarksMissingFromLibrary =>
      'Darks missing from the library';

  @override
  String get calibrationChipNoPlan => 'No plan';

  @override
  String calibrationChipOld(int count) {
    return '$count old';
  }

  @override
  String get calibrationChipNoGuider => 'No guider';

  @override
  String get calibrationCapShootFlats => 'Shoot flats';

  @override
  String calibrationCapBuild(String what) {
    return 'Build $what';
  }

  @override
  String calibrationCapRefresh(String what) {
    return 'Refresh $what';
  }

  @override
  String get calibrationLibraryNoPlan =>
      'Queue a target to see which darks its plan needs.';

  @override
  String calibrationShootMissingFlats(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: 'Shoot $count missing flat sets',
      one: 'Shoot 1 missing flat set',
    );
    return '$_temp0';
  }

  @override
  String get calibrationReshootAll => 'Reshoot all';

  @override
  String get calibrationReshootAllNote =>
      'Reshoot all keeps what you have and shoots every filter again.';

  @override
  String calibrationAgeDays(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count days old',
      one: '1 day old',
      zero: 'taken today',
    );
    return '$_temp0';
  }

  @override
  String calibrationAgeWeeks(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count weeks old',
      one: '1 week old',
    );
    return '$_temp0';
  }

  @override
  String calibrationAgeMonths(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count months old',
      one: '1 month old',
    );
    return '$_temp0';
  }

  @override
  String calibrationGain(int gain) {
    return 'gain $gain';
  }

  @override
  String calibrationOffset(int offset) {
    return 'offset $offset';
  }

  @override
  String calibrationLibraryCamera(String camera) {
    return '$camera · reused for months';
  }

  @override
  String get calibrationLibraryCovers => 'Covers your plan';

  @override
  String calibrationLibraryNeeds(String what) {
    return 'Your plan needs $what';
  }

  @override
  String calibrationLibraryOld(String what) {
    return 'Covers your plan, but the $what darks are old';
  }

  @override
  String get calibrationLibraryOldNote =>
      'Darks drift as a sensor ages. Refresh them when you can.';

  @override
  String get calibrationLibraryEmpty => 'No darks or bias for this camera yet';

  @override
  String get calibrationLibraryEmptyNote =>
      'Build them once on a cloudy night; they last months.';

  @override
  String get calibrationLibraryNone => 'None yet.';

  @override
  String calibrationLibraryAge(String age, int frames) {
    String _temp0 = intl.Intl.pluralLogic(
      frames,
      locale: localeName,
      other: '$frames frames',
      one: '1 frame',
    );
    return '$age · $_temp0';
  }

  @override
  String get calibrationOld => 'Old';

  @override
  String get calibrationLibraryBuildSection => 'Build or refresh';

  @override
  String calibrationLibraryNote(String settings) {
    return 'At $settings, from the plan. Bias at the same gain. The run asks for the lens cap first.';
  }

  @override
  String get calibrationBuildPicked => 'Build picked';

  @override
  String planFlatsFor(String filters) {
    return 'For $filters';
  }

  @override
  String get planFlatsAfterTarget => 'After target';

  @override
  String get planFlatsEndOfNight => 'End of night';

  @override
  String get planFlatsHint =>
      'After target: right after its last frame, at its rotator angle. End of night: after the last target. With no panel the run asks for a screen first.';

  @override
  String get calibrationLibraryNoCamera => 'camera not recorded';

  @override
  String get calibrationStartQueue =>
      'The calibration frames are in the queue after your targets. Start the queue now?';

  @override
  String get calibrationStartQueueNow => 'Start queue';

  @override
  String imagingMosaicHelp(int min, int max) {
    return 'Ranks Tonight\'s Sky framing for a mosaic of this many tiles ($min–$max each way). 1×1 is a single frame.';
  }

  @override
  String get imagingMosaicTilesAcross => 'Tonight\'s Sky mosaic tiles across';

  @override
  String get imagingMosaicTilesDown => 'Tonight\'s Sky mosaic tiles down';

  @override
  String tonightSkyMosaicFraming(int across, int down) {
    return 'Framing for a $across×$down mosaic';
  }

  @override
  String get libraryTagsCommaSeparated => 'Comma-separated';

  @override
  String get rigSetupTitle => 'Rig setup';

  @override
  String get rigSetupIntro =>
      'Before a first night, Ara needs a few facts about this rig.';

  @override
  String get rigSetupSite => 'Site';

  @override
  String get rigSetupSiteMissing => 'Latitude and longitude are not set.';

  @override
  String get rigSetupSiteAction => 'Set site';

  @override
  String get rigSetupOptics => 'Main camera and telescope';

  @override
  String get rigSetupOpticsMissing =>
      'The sensor or the focal length is not set.';

  @override
  String get rigSetupOpticsAction => 'Set up optics';

  @override
  String get rigSetupGuideScope => 'Guide scope';

  @override
  String get rigSetupGuideScopeMissing =>
      'The guide focal length or the Guide Camera\'s pixel size is not set.';

  @override
  String get rigSetupGuideScopeAction => 'Set guide scope';

  @override
  String get rigSetupCalibration => 'Guider calibration';

  @override
  String get rigSetupCalibrationMissing =>
      'Not calibrated yet, or Ara says to calibrate again.';

  @override
  String get rigSetupCalibrationConnect =>
      'Connect the Guider to check its calibration.';

  @override
  String get rigSetupCalibrationAction => 'Open Guider mode';

  @override
  String get rigSetupItemDone => 'Done';

  @override
  String get rigSetupNotNow => 'Not now';

  @override
  String rigSetupProgress(int done, int total) {
    return '$done of $total done';
  }

  @override
  String rigSetupNext(String item) {
    return 'Next: $item';
  }

  @override
  String get rigSetupDonePill => 'Rig set up';

  @override
  String faultGroupCount(int count) {
    return '$count times';
  }

  @override
  String faultGroupSpan(String first, String last) {
    return 'First $first · last $last';
  }

  @override
  String faultGroupUnresolved(int count) {
    return '$count unresolved';
  }

  @override
  String get faultGroupRecovered => 'recovered';

  @override
  String get faultGroupReleased => 'released — disconnected by hand';
}
