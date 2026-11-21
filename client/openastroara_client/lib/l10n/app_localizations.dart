import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:intl/intl.dart' as intl;

import 'app_localizations_en.dart';
import 'app_localizations_pt.dart';

// ignore_for_file: type=lint

/// Callers can lookup localized strings with an instance of AppLocalizations
/// returned by `AppLocalizations.of(context)`.
///
/// Applications need to include `AppLocalizations.delegate()` in their app's
/// `localizationDelegates` list, and the locales they support in the app's
/// `supportedLocales` list. For example:
///
/// ```dart
/// import 'l10n/app_localizations.dart';
///
/// return MaterialApp(
///   localizationsDelegates: AppLocalizations.localizationsDelegates,
///   supportedLocales: AppLocalizations.supportedLocales,
///   home: MyApplicationHome(),
/// );
/// ```
///
/// ## Update pubspec.yaml
///
/// Please make sure to update your pubspec.yaml to include the following
/// packages:
///
/// ```yaml
/// dependencies:
///   # Internationalization support.
///   flutter_localizations:
///     sdk: flutter
///   intl: any # Use the pinned version from flutter_localizations
///
///   # Rest of dependencies
/// ```
///
/// ## iOS Applications
///
/// iOS applications define key application metadata, including supported
/// locales, in an Info.plist file that is built into the application bundle.
/// To configure the locales supported by your app, you’ll need to edit this
/// file.
///
/// First, open your project’s ios/Runner.xcworkspace Xcode workspace file.
/// Then, in the Project Navigator, open the Info.plist file under the Runner
/// project’s Runner folder.
///
/// Next, select the Information Property List item, select Add Item from the
/// Editor menu, then select Localizations from the pop-up menu.
///
/// Select and expand the newly-created Localizations item then, for each
/// locale your application supports, add a new item and select the locale
/// you wish to add from the pop-up menu in the Value field. This list should
/// be consistent with the languages listed in the AppLocalizations.supportedLocales
/// property.
abstract class AppLocalizations {
  AppLocalizations(String locale)
    : localeName = intl.Intl.canonicalizedLocale(locale.toString());

  final String localeName;

  static AppLocalizations? of(BuildContext context) {
    return Localizations.of<AppLocalizations>(context, AppLocalizations);
  }

  static const LocalizationsDelegate<AppLocalizations> delegate =
      _AppLocalizationsDelegate();

  /// A list of this localizations delegate along with the default localizations
  /// delegates.
  ///
  /// Returns a list of localizations delegates containing this delegate along with
  /// GlobalMaterialLocalizations.delegate, GlobalCupertinoLocalizations.delegate,
  /// and GlobalWidgetsLocalizations.delegate.
  ///
  /// Additional delegates can be added by appending to this list in
  /// MaterialApp. This list does not have to be used at all if a custom list
  /// of delegates is preferred or required.
  static const List<LocalizationsDelegate<dynamic>> localizationsDelegates =
      <LocalizationsDelegate<dynamic>>[
        delegate,
        GlobalMaterialLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
      ];

  /// A list of this localizations delegate's supported locales.
  static const List<Locale> supportedLocales = <Locale>[
    Locale('en'),
    Locale('pt'),
    Locale('pt', 'BR'),
  ];

  /// Simple Mode message pills: the pill under the visible messages that unfolds the hidden ones; count is how many are hidden
  ///
  /// In en, this message translates to:
  /// **'+{count} more'**
  String pillsMore(int count);

  /// Simple Mode message pills: the pill that folds the extra messages away again
  ///
  /// In en, this message translates to:
  /// **'Show fewer'**
  String get pillsShowFewer;

  /// Simple Mode message pills: short count of hidden messages at the end of the newest pill when there is no room below it
  ///
  /// In en, this message translates to:
  /// **'+{count}'**
  String pillsMoreShort(int count);

  /// Simple Mode message pills: screen reader label for the short +N count button
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{1 more message} other{{count} more messages}}'**
  String pillsMoreSemantics(int count);

  /// Simple Mode message pills: tooltip of the close button on an error message
  ///
  /// In en, this message translates to:
  /// **'Dismiss'**
  String get pillsDismiss;

  /// Simple Mode safety alarm: screen reader label for the full-screen warning
  ///
  /// In en, this message translates to:
  /// **'Safety alert'**
  String get safetyAlertSemantics;

  /// Simple Mode safety alarm: big heading of the full-screen warning (all caps)
  ///
  /// In en, this message translates to:
  /// **'SAFETY ALERT'**
  String get safetyAlertTitle;

  /// Simple Mode safety alarm: shown when the alarm gives no reason of its own
  ///
  /// In en, this message translates to:
  /// **'Conditions are UNSAFE.'**
  String get safetyUnsafe;

  /// Simple Mode safety alarm: status line while the alarm tone is sounding
  ///
  /// In en, this message translates to:
  /// **'Alarm ringing'**
  String get safetyRinging;

  /// Simple Mode safety alarm: status line during the silent delay before the tone starts
  ///
  /// In en, this message translates to:
  /// **'Silence it before it sounds'**
  String get safetySilenceBefore;

  /// Simple Mode safety alarm: the button that stops the sound and closes the warning
  ///
  /// In en, this message translates to:
  /// **'Silence alarm'**
  String get safetySilence;

  /// Camera window title, before the camera's name
  ///
  /// In en, this message translates to:
  /// **'Camera (Main)'**
  String get deviceCamera;

  /// Mount window title, before the mount's name
  ///
  /// In en, this message translates to:
  /// **'Mount'**
  String get deviceMount;

  /// Focuser window title, before the focuser's name
  ///
  /// In en, this message translates to:
  /// **'Focuser'**
  String get deviceFocuser;

  /// Guider window title, before the guide camera's name
  ///
  /// In en, this message translates to:
  /// **'Guider'**
  String get deviceGuider;

  /// Device window caption while the device connects
  ///
  /// In en, this message translates to:
  /// **'Connecting…'**
  String get deviceConnecting;

  /// Device window caption when the device is not connected
  ///
  /// In en, this message translates to:
  /// **'Not connected'**
  String get deviceNotConnected;

  /// Camera window row: what the sensor reads now
  ///
  /// In en, this message translates to:
  /// **'Sensor temperature'**
  String get deviceCurrentTemperature;

  /// Camera window row: the cooler's On / Off
  ///
  /// In en, this message translates to:
  /// **'Cooler'**
  String get deviceCooler;

  /// Camera window: shown in the Cooler row when the camera has no cooler
  ///
  /// In en, this message translates to:
  /// **'No cooler'**
  String get deviceNoCooler;

  /// Camera window row: the cooler's target temperature
  ///
  /// In en, this message translates to:
  /// **'Target temperature'**
  String get deviceTarget;

  /// Error message when the cooler target could not be saved to the profile
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t save the target'**
  String get deviceCouldntSaveTarget;

  /// Camera window: the row that switches the camera's own anti-dew heater; also the name in its error pill and for a screen reader
  ///
  /// In en, this message translates to:
  /// **'Dew heater'**
  String get deviceDewHeater;

  /// Error message when the camera refused a cooler change
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t change the cooler'**
  String get deviceCouldntChangeCooler;

  /// Versions table in a device's Settings page: the vendor SDK version
  ///
  /// In en, this message translates to:
  /// **'SDK'**
  String get deviceSdk;

  /// Versions table in a device's Settings page: the driver version
  ///
  /// In en, this message translates to:
  /// **'Driver'**
  String get deviceDriver;

  /// Versions table in a device's Settings page: the firmware version
  ///
  /// In en, this message translates to:
  /// **'Firmware'**
  String get deviceFirmware;

  /// Versions table in a device's Settings page: the device's serial number
  ///
  /// In en, this message translates to:
  /// **'Serial number'**
  String get deviceSerialNumber;

  /// Screen-reader label of the minus button of a device window's stepper
  ///
  /// In en, this message translates to:
  /// **'Less'**
  String get deviceLess;

  /// Screen-reader label of the plus button of a device window's stepper
  ///
  /// In en, this message translates to:
  /// **'More'**
  String get deviceMore;

  /// Device window segment: turns a feature on
  ///
  /// In en, this message translates to:
  /// **'On'**
  String get deviceOn;

  /// Device window segment or value: a feature is off
  ///
  /// In en, this message translates to:
  /// **'Off'**
  String get deviceOff;

  /// Title of the versions table in Settings (Rig, and a device's page)
  ///
  /// In en, this message translates to:
  /// **'Versions'**
  String get deviceVersions;

  /// Mount window row: tracking On / Off
  ///
  /// In en, this message translates to:
  /// **'Tracking'**
  String get deviceTracking;

  /// Mount window row: Parked / Unparked
  ///
  /// In en, this message translates to:
  /// **'Park'**
  String get devicePark;

  /// Mount window: shown in the Park row while the mount parks
  ///
  /// In en, this message translates to:
  /// **'Parking…'**
  String get deviceParking;

  /// Mount window: shown in the Park row while the mount unparks
  ///
  /// In en, this message translates to:
  /// **'Unparking…'**
  String get deviceUnparking;

  /// Message pill when a park Simple Mode asked for stopped short (aborted, faulted, or the mount disconnected)
  ///
  /// In en, this message translates to:
  /// **'The mount didn\'t finish parking'**
  String get deviceParkUnfinished;

  /// Message pill when an unpark Simple Mode asked for stopped short
  ///
  /// In en, this message translates to:
  /// **'The mount didn\'t finish unparking'**
  String get deviceUnparkUnfinished;

  /// Mount window segment in the Park row: the mount is parked
  ///
  /// In en, this message translates to:
  /// **'Parked'**
  String get deviceParked;

  /// Mount window segment in the Park row: the mount is unparked
  ///
  /// In en, this message translates to:
  /// **'Unparked'**
  String get deviceUnparked;

  /// Mount window: asked when Parked is tapped while a plan runs
  ///
  /// In en, this message translates to:
  /// **'A plan is running. Park the mount anyway?'**
  String get devicePlanRunningParkAnyway;

  /// Mount window button: don't park while a plan runs
  ///
  /// In en, this message translates to:
  /// **'Cancel'**
  String get deviceCancel;

  /// Mount window button: park even though a plan runs
  ///
  /// In en, this message translates to:
  /// **'Park anyway'**
  String get deviceParkAnyway;

  /// Mount window row: the automatic meridian flip
  ///
  /// In en, this message translates to:
  /// **'Meridian flip'**
  String get deviceMeridianFlip;

  /// Simple Mode Mount window: the row that shows the profile's clearance limit (#1589)
  ///
  /// In en, this message translates to:
  /// **'Clearance limit'**
  String get deviceClearanceLimit;

  /// Simple Mode Mount window: the clearance limit is on; limit is in degrees, whole or with one decimal rounded down (#1589)
  ///
  /// In en, this message translates to:
  /// **'Stops at {limit}°'**
  String deviceClearanceLimitAt(String limit);

  /// Simple Mode Mount window: where the clearance limit is set (#1589)
  ///
  /// In en, this message translates to:
  /// **'Set in the full app: Settings › Unattended running › When to stop.'**
  String get deviceClearanceLimitHint;

  /// Mount window segment in the Meridian flip row: flip automatically
  ///
  /// In en, this message translates to:
  /// **'Auto'**
  String get deviceAuto;

  /// Error message when the meridian flip setting could not be saved
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t save the meridian flip'**
  String get deviceCouldntSaveMeridianFlip;

  /// Error message when the mount refused to track, park or unpark
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t move the mount'**
  String get deviceCouldntMoveMount;

  /// Camera and guider window row: the camera's gain
  ///
  /// In en, this message translates to:
  /// **'Gain'**
  String get deviceGain;

  /// Pill title when a device tap was dropped because its last command hasn't finished
  ///
  /// In en, this message translates to:
  /// **'Still busy'**
  String get deviceBusy;

  /// Pill detail under deviceBusy
  ///
  /// In en, this message translates to:
  /// **'The last command hasn\'t finished. Try again in a moment.'**
  String get deviceBusyDetail;

  /// Error message when the focuser refused a move
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t move the focuser'**
  String get deviceCouldntMoveFocuser;

  /// Filter wheel window row: the filter in place
  ///
  /// In en, this message translates to:
  /// **'Filter'**
  String get deviceFilter;

  /// Filter wheel window: a slot the wheel gives no name, by its number
  ///
  /// In en, this message translates to:
  /// **'Slot {position}'**
  String deviceSlot(int position);

  /// Filter wheel window: a filter's segment, its slot number then its name (e.g. 1 · Ha)
  ///
  /// In en, this message translates to:
  /// **'{position} · {filter}'**
  String deviceSlotFilter(int position, String filter);

  /// Filter wheel window: the wheel reports no slots
  ///
  /// In en, this message translates to:
  /// **'This wheel reports no filters.'**
  String get deviceNoFilters;

  /// Error message when the filter wheel refused a change
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t change the filter'**
  String get deviceCouldntChangeFilter;

  /// Switches window: a device's section title, with how many ports it lists
  ///
  /// In en, this message translates to:
  /// **'{device} · {count, plural, =1{1 port} other{{count} ports}}'**
  String deviceSwitchPorts(String device, int count);

  /// Settings › Switches: the title over a switch device's readings (ports that can't be set: voltage, temperature, a cover state)
  ///
  /// In en, this message translates to:
  /// **'Sensors'**
  String get deviceSensors;

  /// Switches window: the connected switch devices have no port that can be set (only readings, which Simple Mode leaves out)
  ///
  /// In en, this message translates to:
  /// **'Nothing to switch'**
  String get deviceNothingToSwitch;

  /// Switches window: a dew channel's mode segment, the duty set by hand
  ///
  /// In en, this message translates to:
  /// **'Manual'**
  String get deviceModeManual;

  /// Switches window: a dew channel's mode segment, plain on/off
  ///
  /// In en, this message translates to:
  /// **'Switch'**
  String get deviceModeSwitch;

  /// Switches window: a dew output its controller regulates (mode Auto)
  ///
  /// In en, this message translates to:
  /// **'Set automatically'**
  String get deviceSetAutomatically;

  /// Switches status pill: a port named with its device, when two devices have a port of that name
  ///
  /// In en, this message translates to:
  /// **'{port} ({device})'**
  String deviceSwitchPortOnDevice(String port, String device);

  /// Error message when a switch device refused a port change
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t set {port}'**
  String deviceCouldntSetPort(String port);

  /// Simple Mode status pill: a switch port change on its way to the rig
  ///
  /// In en, this message translates to:
  /// **'Setting {port}…'**
  String deviceSettingPort(String port);

  /// Filter wheel window: asked when a filter is tapped while a plan runs
  ///
  /// In en, this message translates to:
  /// **'A plan is running. Change the filter anyway?'**
  String get devicePlanRunningChangeFilter;

  /// Filter wheel window button: change the filter even though a plan runs
  ///
  /// In en, this message translates to:
  /// **'Change anyway'**
  String get deviceChangeAnyway;

  /// Filter wheel window, manual wheel: asks the user to put the chosen filter in by hand
  ///
  /// In en, this message translates to:
  /// **'Swap in {filter}, then confirm.'**
  String deviceSwapIn(String filter);

  /// Filter wheel window, manual wheel button: the chosen filter is now in
  ///
  /// In en, this message translates to:
  /// **'{filter} is in'**
  String deviceFilterIsIn(String filter);

  /// Filter wheel window, manual wheel button: drop the swap asked for
  ///
  /// In en, this message translates to:
  /// **'Cancel swap'**
  String get deviceCancelSwap;

  /// Flat panel window: the cover row's label
  ///
  /// In en, this message translates to:
  /// **'Cover'**
  String get deviceCover;

  /// Flat panel window: the cover's open segment
  ///
  /// In en, this message translates to:
  /// **'Open'**
  String get deviceOpen;

  /// Flat panel window: the cover's closed segment
  ///
  /// In en, this message translates to:
  /// **'Closed'**
  String get deviceClosed;

  /// Flat panel window: the light row's label
  ///
  /// In en, this message translates to:
  /// **'Light'**
  String get deviceLight;

  /// Flat panel window: the brightness slider's label
  ///
  /// In en, this message translates to:
  /// **'Brightness'**
  String get deviceBrightness;

  /// Flat panel window: the light row while the panel's light warms up
  ///
  /// In en, this message translates to:
  /// **'Warming up…'**
  String get deviceWarmingUp;

  /// Status pill detail while the flat panel's cover moves
  ///
  /// In en, this message translates to:
  /// **'Cover moving…'**
  String get deviceCoverMoving;

  /// Status pill detail while the flat panel's light warms up
  ///
  /// In en, this message translates to:
  /// **'Light warming up…'**
  String get deviceLightWarmingUp;

  /// Flat panel window: asked before a cover, light or brightness change while a plan runs
  ///
  /// In en, this message translates to:
  /// **'A plan is running. Change the flat panel anyway?'**
  String get devicePlanRunningChangeFlatPanel;

  /// Error message when the flat panel refused a command
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t change the flat panel'**
  String get deviceCouldntChangeFlatPanel;

  /// Simple Mode Settings › Devices row and window title: the safety monitor
  ///
  /// In en, this message translates to:
  /// **'Safety monitor'**
  String get shellSafetyMonitor;

  /// Dome window: the shutter's row
  ///
  /// In en, this message translates to:
  /// **'Shutter'**
  String get deviceShutter;

  /// Dome window and status pill: the dome's shutter is opening or closing
  ///
  /// In en, this message translates to:
  /// **'Shutter moving…'**
  String get deviceShutterMoving;

  /// Dome window: the caption under the dome's azimuth
  ///
  /// In en, this message translates to:
  /// **'Azimuth'**
  String get deviceAzimuth;

  /// Dome window: the dome sits at its home position
  ///
  /// In en, this message translates to:
  /// **'At home'**
  String get deviceAtHome;

  /// Dome window: button that sends the dome to find its home position
  ///
  /// In en, this message translates to:
  /// **'Home'**
  String get deviceHome;

  /// Dome window: button that stops the dome
  ///
  /// In en, this message translates to:
  /// **'Stop'**
  String get deviceStop;

  /// Dome window: screen reader label of the stop square in the dome's middle
  ///
  /// In en, this message translates to:
  /// **'Stop the dome'**
  String get deviceStopDome;

  /// Dome window: asked before a shutter change, Park or Home while a plan runs
  ///
  /// In en, this message translates to:
  /// **'A plan is running. Change the dome anyway?'**
  String get devicePlanRunningChangeDome;

  /// Error message when the dome refused a shutter change, Park or Home
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t move the dome'**
  String get deviceCouldntMoveDome;

  /// Error message when the dome refused Stop
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t stop the dome'**
  String get deviceCouldntStopDome;

  /// Dome window: the On / Off row that makes the dome keep its slit in front of the telescope
  ///
  /// In en, this message translates to:
  /// **'Follows the mount'**
  String get deviceFollowsMount;

  /// Dome window: the moving line while the dome turns to follow the mount
  ///
  /// In en, this message translates to:
  /// **'Following the mount…'**
  String get deviceFollowingMount;

  /// Message pill: a dial turn, Park, Home or Stop turned following the mount off
  ///
  /// In en, this message translates to:
  /// **'The dome no longer follows the mount'**
  String get deviceStoppedFollowingMount;

  /// Error message when the daemon refused the Follows the mount On / Off
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t change whether the dome follows the mount'**
  String get deviceCouldntFollowMount;

  /// Full UI Dome panel: the section header above the Follow the mount switch and its note
  ///
  /// In en, this message translates to:
  /// **'Following the mount'**
  String get domePanelFollowingHeader;

  /// Full UI Dome panel: shown while the dome settings load
  ///
  /// In en, this message translates to:
  /// **'Reading…'**
  String get domePanelReadingSettings;

  /// Full UI Dome panel: error lead when the daemon refused a geometry change (the reason follows after a colon)
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t save the dome'**
  String get domePanelCouldntSave;

  /// Full UI Dome panel: the note under Following the mount; names what turns the dome and what turns following off
  ///
  /// In en, this message translates to:
  /// **'With this on, Ara turns the dome so its slit stays in front of the telescope: after a mount slew, after a meridian flip, and while tracking whenever the slit falls behind by more than the tolerance. Turning the dome by hand, parking it, sending it home, stopping it or disconnecting it turns this off, and so do an emergency stop, an unsafe-weather reaction and a geometry that puts the telescope outside the dome. The dome must be able to turn to an azimuth.'**
  String get domePanelFollowNote;

  /// Full UI Dome panel: the switch that makes the dome keep its slit in front of the telescope
  ///
  /// In en, this message translates to:
  /// **'Follow the mount'**
  String get domePanelFollowMount;

  /// Full UI Dome panel: hint under Follow the mount when the connected dome cannot turn to an azimuth
  ///
  /// In en, this message translates to:
  /// **'Connect a dome that can turn to an azimuth first'**
  String get domePanelFollowNeedsAzimuth;

  /// Full UI Dome panel: the section header above the dome and mount measurements
  ///
  /// In en, this message translates to:
  /// **'Geometry'**
  String get domePanelGeometryHeader;

  /// Full UI Dome panel: the mount type picker in the dome geometry
  ///
  /// In en, this message translates to:
  /// **'Mount type'**
  String get domePanelMountType;

  /// Full UI Dome panel: mount type choice; a German equatorial mount adds its axis offsets to the slit math
  ///
  /// In en, this message translates to:
  /// **'German equatorial'**
  String get domePanelMountGermanEquatorial;

  /// Full UI Dome panel: mount type choice for a fork or alt-az mount on an equatorial wedge
  ///
  /// In en, this message translates to:
  /// **'Fork or alt-az (on a wedge)'**
  String get domePanelMountForkOnWedge;

  /// Full UI Dome panel: dome geometry field, in millimeters
  ///
  /// In en, this message translates to:
  /// **'Dome radius (mm)'**
  String get domePanelDomeRadius;

  /// Full UI Dome panel: dome geometry field, in millimeters; negative is south
  ///
  /// In en, this message translates to:
  /// **'Scope north of dome center (mm)'**
  String get domePanelScopeNorth;

  /// Full UI Dome panel: dome geometry field, in millimeters; negative is west
  ///
  /// In en, this message translates to:
  /// **'Scope east of dome center (mm)'**
  String get domePanelScopeEast;

  /// Full UI Dome panel: dome geometry field, in millimeters; negative is below
  ///
  /// In en, this message translates to:
  /// **'Scope above dome center (mm)'**
  String get domePanelScopeAbove;

  /// Full UI Dome panel: German equatorial mount geometry field, in millimeters
  ///
  /// In en, this message translates to:
  /// **'Mount axis length (mm)'**
  String get domePanelMountAxisLength;

  /// Full UI Dome panel: German equatorial mount geometry field, in millimeters
  ///
  /// In en, this message translates to:
  /// **'Mount lateral offset (mm)'**
  String get domePanelMountLateralOffset;

  /// Full UI Dome panel: German equatorial mount geometry field, in millimeters
  ///
  /// In en, this message translates to:
  /// **'Dec axis horizontal offset (mm)'**
  String get domePanelDecAxisOffset;

  /// Full UI Dome panel: how far the slit may fall behind before the dome turns again, in degrees
  ///
  /// In en, this message translates to:
  /// **'Azimuth tolerance (°)'**
  String get domePanelAzimuthTolerance;

  /// Full UI Dome panel: switch; the dome starts turning while the mount is still slewing
  ///
  /// In en, this message translates to:
  /// **'Turn during mount slews'**
  String get domePanelTurnDuringSlews;

  /// Full UI Dome panel: hint under Turn during mount slews
  ///
  /// In en, this message translates to:
  /// **'Off: the dome turns when the mount has finished slewing'**
  String get domePanelTurnDuringSlewsHint;

  /// Full UI Dome panel: the moving line while the dome turns on its own (not following the mount)
  ///
  /// In en, this message translates to:
  /// **'Slewing…'**
  String get domePanelSlewing;

  /// Dome window: the daemon couldn't read the dome
  ///
  /// In en, this message translates to:
  /// **'Dome read failed — check the device.'**
  String get deviceDomeReadFailed;

  /// Safety monitor window: the monitor reads safe
  ///
  /// In en, this message translates to:
  /// **'Safe'**
  String get deviceSafe;

  /// Safety monitor window: the monitor reads unsafe
  ///
  /// In en, this message translates to:
  /// **'Unsafe'**
  String get deviceUnsafe;

  /// Safety monitor window: when the reading last changed (local 24 h time) and how long ago
  ///
  /// In en, this message translates to:
  /// **'since {time} · {duration}'**
  String deviceSafetySince(String time, String duration);

  /// Status pill detail while the safety monitor reads unsafe (local 24 h time)
  ///
  /// In en, this message translates to:
  /// **'Unsafe since {time}'**
  String deviceUnsafeSince(String time);

  /// Safety monitor window while unsafe: the safety policy resumes the plan this long after the monitor reads safe again
  ///
  /// In en, this message translates to:
  /// **'Resumes {minutes} min after Safe'**
  String deviceResumesAfterSafe(int minutes);

  /// Safety monitor window while unsafe: the safety policy does not resume the plan by itself
  ///
  /// In en, this message translates to:
  /// **'Stays paused until you resume'**
  String get deviceStaysPaused;

  /// Dome window and status pill: the shutter is opening
  ///
  /// In en, this message translates to:
  /// **'Opening…'**
  String get deviceOpening;

  /// Dome window and status pill: the shutter is closing
  ///
  /// In en, this message translates to:
  /// **'Closing…'**
  String get deviceClosing;

  /// Dome window: screen reader label of the dial's middle with no target yet
  ///
  /// In en, this message translates to:
  /// **'Turn'**
  String get deviceTurn;

  /// Dome window: screen reader label of the dial's middle, turning the dome to the target
  ///
  /// In en, this message translates to:
  /// **'Turn to {angle}'**
  String deviceTurnTo(String angle);

  /// Dome window: the daemon reads the dome's shutter as faulted (ASCOM ShutterState.Error)
  ///
  /// In en, this message translates to:
  /// **'Shutter fault: open or close again.'**
  String get deviceShutterFault;

  /// Dome window and status pill: the dome is finding its home position
  ///
  /// In en, this message translates to:
  /// **'Finding home…'**
  String get deviceFindingHome;

  /// Dome window and status pill: the dome is turning to the dial's target
  ///
  /// In en, this message translates to:
  /// **'Turning to {angle}…'**
  String deviceTurningTo(String angle);

  /// Safety monitor window: a weather reason as a sentence in the Why list (the same words are a status pill's detail without the full stop)
  ///
  /// In en, this message translates to:
  /// **'{reason}.'**
  String deviceReasonSentence(String reason);

  /// Safety monitor window: heading over the reasons the rig is unsafe
  ///
  /// In en, this message translates to:
  /// **'Why'**
  String get deviceWhy;

  /// Safety monitor window: heading over what the safety policy does while unsafe
  ///
  /// In en, this message translates to:
  /// **'What Ara does'**
  String get deviceWhatAraDoes;

  /// Safety monitor window: an ASCOM safety monitor reports only safe or unsafe, never a reason
  ///
  /// In en, this message translates to:
  /// **'{name} says unsafe. It doesn\'t say why: check the device.'**
  String deviceMonitorNoReason(String name);

  /// Safety monitor window and status pill: the wind (worse of speed and gust) past the safety limit; no full stop, it is a pill detail too
  ///
  /// In en, this message translates to:
  /// **'Wind {value} km/h, over the {limit} km/h limit'**
  String deviceWindOverLimit(String value, int limit);

  /// Safety monitor window: the humidity past the safety limit
  ///
  /// In en, this message translates to:
  /// **'Humidity {value} %, over the {limit} % limit'**
  String deviceHumidityOverLimit(String value, int limit);

  /// Safety monitor window: the air too close to the dew point
  ///
  /// In en, this message translates to:
  /// **'Dew point {value} °C below the air, under the {limit} °C margin'**
  String deviceDewUnderMargin(String value, String limit);

  /// Safety monitor window: under Unsafe when the weather, not the monitor, makes the rig unsafe
  ///
  /// In en, this message translates to:
  /// **'{name} reads Safe'**
  String deviceMonitorReadsSafe(String name);

  /// Safety monitor window: the On unsafe policy Pause + park + close dome
  ///
  /// In en, this message translates to:
  /// **'On unsafe, Ara pauses the plan, parks and closes the dome.'**
  String get deviceOnUnsafePause;

  /// Safety monitor window: the On unsafe policy Park only
  ///
  /// In en, this message translates to:
  /// **'On unsafe, Ara parks; the plan carries on.'**
  String get deviceOnUnsafePark;

  /// Safety monitor window: the On unsafe policy Abort sequence + park
  ///
  /// In en, this message translates to:
  /// **'On unsafe, Ara stops the plan and parks.'**
  String get deviceOnUnsafeAbort;

  /// Safety monitor window: the On unsafe policy Ignore
  ///
  /// In en, this message translates to:
  /// **'On unsafe, Ara does nothing; imaging carries on.'**
  String get deviceOnUnsafeIgnore;

  /// Error message when the flat panel took a command but never showed it
  ///
  /// In en, this message translates to:
  /// **'The flat panel didn\'t make that change'**
  String get deviceFlatPanelUnconfirmed;

  /// Detail under the flat panel's unconfirmed-change message
  ///
  /// In en, this message translates to:
  /// **'Check the cover isn\'t jammed and the panel isn\'t busy.'**
  String get deviceFlatPanelUnconfirmedDetail;

  /// Weather window: the station's last read failed, so no readings show
  ///
  /// In en, this message translates to:
  /// **'Sensor read failed — check the device.'**
  String get deviceSensorReadFailed;

  /// Weather window tile: cloud cover in %
  ///
  /// In en, this message translates to:
  /// **'Cloud cover'**
  String get deviceCloudCover;

  /// Weather window tile: sky quality (SQM) in mag/arcsec²
  ///
  /// In en, this message translates to:
  /// **'Sky quality'**
  String get deviceSkyQuality;

  /// Weather window tile: dew point
  ///
  /// In en, this message translates to:
  /// **'Dew point'**
  String get deviceDewPoint;

  /// Weather window tile: relative humidity
  ///
  /// In en, this message translates to:
  /// **'Humidity'**
  String get deviceHumidity;

  /// Weather window tile: rain rate
  ///
  /// In en, this message translates to:
  /// **'Rain'**
  String get deviceRain;

  /// Weather window tile: wind speed
  ///
  /// In en, this message translates to:
  /// **'Wind'**
  String get deviceWind;

  /// Weather window wind tile caption: the gust speed, same unit as the tile's number
  ///
  /// In en, this message translates to:
  /// **'gusts {speed}'**
  String deviceGusts(String speed);

  /// Weather window wind tile caption: the wind's compass point (8 points)
  ///
  /// In en, this message translates to:
  /// **'{point, select, n{N} ne{NE} e{E} se{SE} s{S} sw{SW} w{W} other{NW}}'**
  String deviceCompass(String point);

  /// Weather window tile caption when the reading is past the rig's safety limit, e.g. 'limit 85 %'
  ///
  /// In en, this message translates to:
  /// **'limit {value}'**
  String deviceLimit(String value);

  /// Weather window dew point caption when the air is closer to the dew point than the safety margin, e.g. 'margin 2 °C'
  ///
  /// In en, this message translates to:
  /// **'margin {value}'**
  String deviceMargin(String value);

  /// Screen reader text after a weather reading past the rig's safety limit
  ///
  /// In en, this message translates to:
  /// **'past the safety limit'**
  String get devicePastLimit;

  /// Weather window: how old the readings are, e.g. 'Updated 8 s ago'
  ///
  /// In en, this message translates to:
  /// **'Updated {age} ago'**
  String deviceUpdatedAgo(String age);

  /// Weather window, amber: the readings are over a minute old, e.g. 'Last reading 4 min ago'
  ///
  /// In en, this message translates to:
  /// **'Last reading {age} ago'**
  String deviceLastReadingAgo(String age);

  /// Weather window tile: the moon's lit fraction, computed (not a station reading)
  ///
  /// In en, this message translates to:
  /// **'Moon'**
  String get deviceMoon;

  /// Weather window moon tile caption: the moon's phase
  ///
  /// In en, this message translates to:
  /// **'{phase, select, newMoon{New moon} waxingCrescent{Waxing crescent} firstQuarter{First quarter} waxingGibbous{Waxing gibbous} fullMoon{Full moon} waningGibbous{Waning gibbous} lastQuarter{Last quarter} other{Waning crescent}}'**
  String deviceMoonPhase(String phase);

  /// Weather window, amber, under the tiles: the rig's safety limits could not be read, so no reading is marked past them
  ///
  /// In en, this message translates to:
  /// **'Safety limits not loaded: nothing is marked.'**
  String get deviceSafetyLimitsNotLoaded;

  /// Error message when a focuser or guider setting could not be saved
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t save that'**
  String get deviceCouldntSaveThat;

  /// Focuser window row (on or off): whether a running plan refocuses by itself
  ///
  /// In en, this message translates to:
  /// **'Autofocus during plans'**
  String get deviceAutofocusDuringPlans;

  /// Focuser window: under Autofocus during plans when it is on
  ///
  /// In en, this message translates to:
  /// **'Ara refocuses on its own while a plan runs.'**
  String get deviceAutofocusOnHint;

  /// Focuser window: under Autofocus during plans when it is off
  ///
  /// In en, this message translates to:
  /// **'Plans never refocus by themselves. The Autofocus button still works.'**
  String get deviceAutofocusOffHint;

  /// Focuser window row: how readily a plan refocuses (Relaxed, Normal or Tight)
  ///
  /// In en, this message translates to:
  /// **'Keep focus sharp'**
  String get deviceKeepFocusSharp;

  /// Focuser window, Keep focus sharp: refocus rarely
  ///
  /// In en, this message translates to:
  /// **'Relaxed'**
  String get deviceKeepSharpRelaxed;

  /// Focuser window, Keep focus sharp: today's defaults
  ///
  /// In en, this message translates to:
  /// **'Normal'**
  String get deviceKeepSharpNormal;

  /// Focuser window, Keep focus sharp: refocus often
  ///
  /// In en, this message translates to:
  /// **'Tight'**
  String get deviceKeepSharpTight;

  /// Focuser window, Keep focus sharp: the refocus triggers were changed in Advanced Mode and match no preset (shown picked, cannot be tapped)
  ///
  /// In en, this message translates to:
  /// **'Custom'**
  String get deviceKeepSharpCustom;

  /// Focuser window: what the picked Keep focus sharp preset does
  ///
  /// In en, this message translates to:
  /// **'Refocus after a filter change, when the temperature changes by {temp}, when stars grow {hfr} %, or every {hours} h.'**
  String deviceKeepSharpHint(String temp, String hfr, String hours);

  /// Focuser window: under Keep focus sharp when the triggers match no preset
  ///
  /// In en, this message translates to:
  /// **'Set in Advanced Mode\'s Settings. Pick a preset to replace those values.'**
  String get deviceKeepSharpCustomHint;

  /// Settings header switch, on by default: on shows every setting; off shows only the Autofocus panel's Simple tier and hides the derived step size in Equipment › Focuser
  ///
  /// In en, this message translates to:
  /// **'Advanced'**
  String get settingsAdvancedToggle;

  /// Autofocus settings, under the step size: the step size the next sweep uses and where it comes from
  ///
  /// In en, this message translates to:
  /// **'Derived: {steps} steps, {source}'**
  String autofocusStepSizeDerived(int steps, String source);

  /// Where the derived autofocus step size comes from: automatic sizing is off
  ///
  /// In en, this message translates to:
  /// **'set by you'**
  String get autofocusStepSourceManual;

  /// Where the derived autofocus step size comes from: the last sweep's curve
  ///
  /// In en, this message translates to:
  /// **'from the V width of the last sweep'**
  String get autofocusStepSourceMeasured;

  /// Where the derived autofocus step size comes from: the focuser step size and the optics
  ///
  /// In en, this message translates to:
  /// **'from half the critical focus zone'**
  String get autofocusStepSourceCfz;

  /// Where the derived autofocus step size comes from: no sweep yet and no focuser step size
  ///
  /// In en, this message translates to:
  /// **'the stored first-sweep step'**
  String get autofocusStepSourceDefault;

  /// Autofocus settings, under a trigger: the Keep focus sharp preset these four triggers currently match
  ///
  /// In en, this message translates to:
  /// **'Keep focus sharp: {preset}'**
  String autofocusTriggerSetBy(String preset);

  /// Focuser window row: the filter autofocus runs through, one chip per filter in the wheel
  ///
  /// In en, this message translates to:
  /// **'AF filter'**
  String get deviceAfFilter;

  /// Focuser window: under the AF filter chips
  ///
  /// In en, this message translates to:
  /// **'Luminance is brightest, so autofocus finishes fastest.'**
  String get deviceAfFilterHint;

  /// Focuser window row: the focuser's position in steps
  ///
  /// In en, this message translates to:
  /// **'Position'**
  String get devicePosition;

  /// Focuser window row: the focuser's temperature
  ///
  /// In en, this message translates to:
  /// **'Temperature'**
  String get deviceTemperature;

  /// Focuser window caption while the focuser moves
  ///
  /// In en, this message translates to:
  /// **'Moving…'**
  String get deviceMoving;

  /// Simple Mode camera pill: the main camera is exposing a frame nothing in this app started (#1755)
  ///
  /// In en, this message translates to:
  /// **'Exposing'**
  String get deviceExposing;

  /// Simple Mode camera pill: the main camera is downloading a frame nothing in this app started (#1755)
  ///
  /// In en, this message translates to:
  /// **'Downloading'**
  String get deviceDownloading;

  /// Full-width button under the focuser knobs: runs autofocus
  ///
  /// In en, this message translates to:
  /// **'Autofocus'**
  String get deviceAutofocus;

  /// The focuser's Autofocus button while autofocus runs: stops it
  ///
  /// In en, this message translates to:
  /// **'Cancel autofocus'**
  String get deviceCancelAutofocus;

  /// Focuser window, above the knobs: rolling a knob up moves the focuser in
  ///
  /// In en, this message translates to:
  /// **'In'**
  String get deviceIn;

  /// Focuser window, under the knobs: rolling a knob down moves the focuser out
  ///
  /// In en, this message translates to:
  /// **'Out'**
  String get deviceOut;

  /// The focuser window's big knob, which moves in large clicks
  ///
  /// In en, this message translates to:
  /// **'Coarse'**
  String get deviceCoarse;

  /// The focuser window's small knob, which moves in small clicks
  ///
  /// In en, this message translates to:
  /// **'Fine'**
  String get deviceFine;

  /// Screen-reader label of the focuser's coarse knob
  ///
  /// In en, this message translates to:
  /// **'Coarse focus'**
  String get deviceCoarseFocus;

  /// Screen-reader label of the focuser's fine knob
  ///
  /// In en, this message translates to:
  /// **'Fine focus'**
  String get deviceFineFocus;

  /// Rotator window button with no target set on the dial (disabled)
  ///
  /// In en, this message translates to:
  /// **'Rotate'**
  String get deviceRotate;

  /// Rotator window button: move the rotator to the angle set on the dial, e.g. 'Rotate to 75°'
  ///
  /// In en, this message translates to:
  /// **'Rotate to {angle}'**
  String deviceRotateTo(String angle);

  /// Rotator window button with no target set on the dial (disabled)
  ///
  /// In en, this message translates to:
  /// **'Sync'**
  String get deviceSync;

  /// Rotator window button: tell the rotator it is at the angle set on the dial, without moving it, e.g. 'Sync to 75°'
  ///
  /// In en, this message translates to:
  /// **'Sync to {angle}'**
  String deviceSyncTo(String angle);

  /// Error pill when the rotator refused or failed a sync
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t sync the rotator'**
  String get deviceCouldntSyncRotator;

  /// Rotator window: asked before a sync while a plan runs
  ///
  /// In en, this message translates to:
  /// **'A plan is running. Sync anyway?'**
  String get devicePlanRunningSync;

  /// Rotator window: confirms a sync while a plan runs
  ///
  /// In en, this message translates to:
  /// **'Sync anyway'**
  String get deviceSyncAnyway;

  /// Rotator window: the camera's angle on the sky (position angle), and the dial's screen-reader label
  ///
  /// In en, this message translates to:
  /// **'Sky angle'**
  String get deviceSkyAngle;

  /// Rotator window: the rotator's own angle, shown when it differs from the sky angle
  ///
  /// In en, this message translates to:
  /// **'Mechanical angle'**
  String get deviceMechanicalAngle;

  /// Error pill when the rotator refused or failed a move
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t move the rotator'**
  String get deviceCouldntMoveRotator;

  /// Error pill when a stop never reached the rotator
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t stop the rotator'**
  String get deviceCouldntStopRotator;

  /// Screen-reader label of the red stop square in the middle of the rotator dial
  ///
  /// In en, this message translates to:
  /// **'Stop the rotator'**
  String get deviceStopRotator;

  /// Rotator window: asked before a move while a plan runs
  ///
  /// In en, this message translates to:
  /// **'A plan is running. Rotate anyway?'**
  String get devicePlanRunningRotate;

  /// Rotator window: confirms a move while a plan runs
  ///
  /// In en, this message translates to:
  /// **'Rotate anyway'**
  String get deviceRotateAnyway;

  /// Screen-reader hint on the focuser's knobs: increase (the bigger number) moves the focuser out, though rolling the knob up or the Up key moves it in
  ///
  /// In en, this message translates to:
  /// **'Increase moves out, decrease moves in'**
  String get deviceKnobHint;

  /// Guider window row: what the guider is doing
  ///
  /// In en, this message translates to:
  /// **'State'**
  String get deviceState;

  /// Guider window row: the guiding error (root mean square)
  ///
  /// In en, this message translates to:
  /// **'RMS'**
  String get deviceRms;

  /// Guider window row: how often a plan dithers
  ///
  /// In en, this message translates to:
  /// **'Dither'**
  String get deviceDither;

  /// Guider window row: how far a dither moves, in pixels
  ///
  /// In en, this message translates to:
  /// **'Dither by'**
  String get deviceDitherBy;

  /// Camera window, Current temperature row: the cooler's power
  ///
  /// In en, this message translates to:
  /// **'{percent} % power'**
  String devicePowerPercent(int percent);

  /// Focuser window caption while a knob rolls; position is a step count like 12 000
  ///
  /// In en, this message translates to:
  /// **'Let go to move to {position}'**
  String deviceLetGoToMoveTo(String position);

  /// Focuser window caption while the focuser moves to a position (a step count like 12 000)
  ///
  /// In en, this message translates to:
  /// **'Moving to {position}…'**
  String deviceMovingTo(String position);

  /// Guider window, under the RMS value: the error per axis (values like 0.42″)
  ///
  /// In en, this message translates to:
  /// **'RA {ra} · Dec {dec}'**
  String deviceRmsRaDec(String ra, String dec);

  /// Guider window, Dither row: a plan dithers every this many frames
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{Every frame} other{{count} frames}}'**
  String deviceDitherEveryFrames(int count);

  /// Simple Mode System tile: free space on the save drive; size is a number in gigabytes
  ///
  /// In en, this message translates to:
  /// **'{size} GB free'**
  String previewFree(String size);

  /// Simple Mode System tile: the save drive can't be read
  ///
  /// In en, this message translates to:
  /// **'Unplugged'**
  String get previewUnplugged;

  /// Simple Mode System tile label: storage free on the server
  ///
  /// In en, this message translates to:
  /// **'Storage'**
  String get previewStorage;

  /// Simple Mode System tile label: server CPU use
  ///
  /// In en, this message translates to:
  /// **'CPU'**
  String get previewCpu;

  /// Simple Mode System tile label: server memory in use
  ///
  /// In en, this message translates to:
  /// **'Memory'**
  String get previewMemory;

  /// Simple Mode System tile label: server uptime (short, tile is 72 px wide)
  ///
  /// In en, this message translates to:
  /// **'Up'**
  String get previewUp;

  /// Simple Mode Info tile label: half-flux radius of the last frame's stars
  ///
  /// In en, this message translates to:
  /// **'HFR'**
  String get previewHfr;

  /// Simple Mode Info tile label: number of stars detected in the last frame
  ///
  /// In en, this message translates to:
  /// **'Stars'**
  String get previewStars;

  /// Why the Main row in Preview's Camera field is locked while the guide camera loops
  ///
  /// In en, this message translates to:
  /// **'Stop the guide camera\'s frames first.'**
  String get previewStopGuideFrames;

  /// Empty screen in Preview with the guide camera picked and no guide frame yet
  ///
  /// In en, this message translates to:
  /// **'Tap the shutter for the guide camera\'s frames'**
  String get previewGuideEmpty;

  /// Simple Mode: label above the list of connected cameras in Preview's settings.
  ///
  /// In en, this message translates to:
  /// **'Camera'**
  String get previewCamera;

  /// Simple Mode: exposure length, as an Info tile label and the Preview settings picker label
  ///
  /// In en, this message translates to:
  /// **'Exposure'**
  String get previewExposure;

  /// Simple Mode Info tile label: the last frame's camera gain and offset
  ///
  /// In en, this message translates to:
  /// **'Gain · offset'**
  String get previewGainOffset;

  /// Simple Mode Info tile label: share of saturated (clipped) highlight pixels
  ///
  /// In en, this message translates to:
  /// **'Clipped'**
  String get previewClipped;

  /// Simple Mode Preview settings: segment for continuous live view (frames not saved)
  ///
  /// In en, this message translates to:
  /// **'Loop'**
  String get previewLoop;

  /// Simple Mode Preview settings: segment for taking one saved frame
  ///
  /// In en, this message translates to:
  /// **'Single'**
  String get previewSingle;

  /// Simple Mode: button closing a wheel picker and applying the pick
  ///
  /// In en, this message translates to:
  /// **'Done'**
  String get previewDone;

  /// Simple Mode shutter button, screen-reader label while something runs
  ///
  /// In en, this message translates to:
  /// **'Stop'**
  String get previewStop;

  /// Simple Mode shutter button, screen-reader label at rest
  ///
  /// In en, this message translates to:
  /// **'Take a frame'**
  String get previewTakeFrame;

  /// Simple Mode status pill: a scheduled plan's start time (HH:MM)
  ///
  /// In en, this message translates to:
  /// **'Starts at {time}'**
  String previewStartsAt(String time);

  /// Simple Mode status pill: the shot is being cancelled
  ///
  /// In en, this message translates to:
  /// **'Stopping…'**
  String get previewStopping;

  /// Simple Mode status pill: the camera is exposing
  ///
  /// In en, this message translates to:
  /// **'Taking a frame'**
  String get previewTakingFrame;

  /// Simple Mode status pill detail: the exposure is about to start
  ///
  /// In en, this message translates to:
  /// **'Starting…'**
  String get previewStarting;

  /// Simple Mode status pill detail: whole seconds left in the exposure (s = seconds)
  ///
  /// In en, this message translates to:
  /// **'{seconds} s left'**
  String previewSecondsLeft(int seconds);

  /// Simple Mode status pill: the frame is downloading from the camera
  ///
  /// In en, this message translates to:
  /// **'Downloading…'**
  String get previewDownloading;

  /// Simple Mode status pill: the frame has arrived
  ///
  /// In en, this message translates to:
  /// **'Frame ready'**
  String get previewFrameReady;

  /// Simple Mode status pill: the capture failed (the reason follows)
  ///
  /// In en, this message translates to:
  /// **'Capture failed'**
  String get previewCaptureFailed;

  /// Simple Mode status pill: live view is running
  ///
  /// In en, this message translates to:
  /// **'Looping'**
  String get previewLooping;

  /// Simple Mode status pill: the imaging plan is paused (its step follows)
  ///
  /// In en, this message translates to:
  /// **'Plan paused'**
  String get previewPlanPaused;

  /// Simple Mode status pill, a running plan whose Start guiding step has the Guider calibrating and no leg reported yet
  ///
  /// In en, this message translates to:
  /// **'Start guiding · Guider calibrating'**
  String get previewPlanGuiderCalibrating;

  /// Simple Mode status pill, a running plan whose Start guiding step has the Guider calibrating; leg is the calibration leg, e.g. 'West step 3 · 11.9 px'
  ///
  /// In en, this message translates to:
  /// **'Start guiding · Guider calibrating · {leg}'**
  String previewPlanGuiderCalibratingLeg(String leg);

  /// Simple Mode status pill: the imaging plan is running (its step follows)
  ///
  /// In en, this message translates to:
  /// **'Plan'**
  String get previewPlan;

  /// Simple Mode status pill: a finished plan imaged nothing because its target never started (#1596). Keep it short: the pill is one line, max 300 px, after the 'Plan' label, and the cause must survive the ellipsis.
  ///
  /// In en, this message translates to:
  /// **'Skipped: conditions not met'**
  String get previewPlanSkipped;

  /// Simple Mode Plan pill while the plan waits out the meridian, with the time left (mm:ss or h:mm:ss) (#1747)
  ///
  /// In en, this message translates to:
  /// **'Waiting for the meridian flip · {countdown}'**
  String previewWaitingForFlip(String countdown);

  /// Simple Mode status pill: the mount is parking
  ///
  /// In en, this message translates to:
  /// **'Parking'**
  String get previewParking;

  /// Simple Mode status pill: the mount is unparking
  ///
  /// In en, this message translates to:
  /// **'Unparking'**
  String get previewUnparking;

  /// Simple Mode status pill: the camera sensor is cooling to its target (temperatures follow)
  ///
  /// In en, this message translates to:
  /// **'Cooling'**
  String get previewCooling;

  /// Simple Mode status pill: the camera's cooler went off and the sensor is warming back up (its temperature follows)
  ///
  /// In en, this message translates to:
  /// **'Warming'**
  String get previewWarming;

  /// Simple Mode status pill, screen-reader text: the status and its detail
  ///
  /// In en, this message translates to:
  /// **'{label}, {detail}'**
  String previewStatusSpoken(String label, String detail);

  /// Simple Mode Focus: rail chip caption when focus runs by autofocus
  ///
  /// In en, this message translates to:
  /// **'Auto'**
  String get focusAuto;

  /// Simple Mode Focus: rail chip caption when focusing by hand
  ///
  /// In en, this message translates to:
  /// **'Manual'**
  String get focusManual;

  /// Simple Mode Focus shutter, screen-reader label: start autofocus
  ///
  /// In en, this message translates to:
  /// **'Autofocus'**
  String get focusAutofocus;

  /// Simple Mode Focus shutter, screen-reader label while autofocus runs
  ///
  /// In en, this message translates to:
  /// **'Cancel autofocus'**
  String get focusCancelAutofocus;

  /// Simple Mode Focus panel: the big half-flux radius readout; value is a number or a dash
  ///
  /// In en, this message translates to:
  /// **'HFR {value}'**
  String focusHfr(String value);

  /// Simple Mode Focus panel: shown when no focuser is connected
  ///
  /// In en, this message translates to:
  /// **'No focuser connected'**
  String get focusNoFocuser;

  /// Simple Mode Polar align panel: loop mode segment (shoots frames on its own); also the rail chip caption
  ///
  /// In en, this message translates to:
  /// **'Loop'**
  String get polarLoop;

  /// Simple Mode Polar align panel: single mode segment (waits for a tap before each frame); also the rail chip caption
  ///
  /// In en, this message translates to:
  /// **'Single'**
  String get polarSingle;

  /// Polar align shutter button's spoken label when idle
  ///
  /// In en, this message translates to:
  /// **'Start polar alignment'**
  String get polarStart;

  /// Polar align shutter button's spoken label while the routine runs
  ///
  /// In en, this message translates to:
  /// **'Stop'**
  String get polarStop;

  /// Simple Mode Polar align panel: error message tail after "Couldn't " when starting the routine fails; lower-case, no period
  ///
  /// In en, this message translates to:
  /// **'start polar alignment'**
  String get polarActionStart;

  /// Simple Mode Polar align panel: error message tail after "Couldn't " when stopping the routine fails; lower-case, no period
  ///
  /// In en, this message translates to:
  /// **'stop polar alignment'**
  String get polarActionStop;

  /// Simple Mode Polar align panel: error message tail after "Couldn't " when saving its settings fails; lower-case, no period
  ///
  /// In en, this message translates to:
  /// **'save the polar-align settings'**
  String get polarActionSave;

  /// Simple Mode Polar align panel: error message tail after "Couldn't " when taking a frame fails; lower-case, no period
  ///
  /// In en, this message translates to:
  /// **'take a frame'**
  String get polarActionCapture;

  /// Simple Mode Polar align panel: error message tail after "Couldn't " when finishing the routine fails; lower-case, no period
  ///
  /// In en, this message translates to:
  /// **'complete polar alignment'**
  String get polarActionComplete;

  /// Simple Mode Polar align panel: routine phase: first frames, locating the pole
  ///
  /// In en, this message translates to:
  /// **'Finding the pole…'**
  String get polarPhaseSeeding;

  /// Simple Mode Polar align panel: routine phase: the user turns the mount bolts
  ///
  /// In en, this message translates to:
  /// **'Adjusting'**
  String get polarPhaseAdjusting;

  /// Simple Mode Polar align panel: routine phase: paused
  ///
  /// In en, this message translates to:
  /// **'Paused'**
  String get polarPhasePaused;

  /// Simple Mode Polar align panel: routine phase: failed
  ///
  /// In en, this message translates to:
  /// **'Polar alignment failed'**
  String get polarPhaseFailed;

  /// Simple Mode Polar align panel: routine phase: idle
  ///
  /// In en, this message translates to:
  /// **'Ready'**
  String get polarPhaseReady;

  /// Simple Mode Polar align panel: routine phase: the mount is finding its home position
  ///
  /// In en, this message translates to:
  /// **'Going Home…'**
  String get polarPhaseHoming;

  /// Simple Mode Polar align panel: routine phase: waiting for the user to put the mount at Home
  ///
  /// In en, this message translates to:
  /// **'Put the mount at Home'**
  String get polarPhaseNeedsHome;

  /// Simple Mode Polar align panel: routine phase: the coarse (main camera at Home) stage
  ///
  /// In en, this message translates to:
  /// **'Rough step'**
  String get polarPhaseRough;

  /// Simple Mode Polar align panel: routine phase: the three-point check, solves done so far of the daemon's total
  ///
  /// In en, this message translates to:
  /// **'Checking {done} of {total}'**
  String polarPhaseChecking(int done, int total);

  /// Simple Mode Polar align panel: the daemon's check verdict 'excellent' (1 arcminute or better)
  ///
  /// In en, this message translates to:
  /// **'Excellent'**
  String get polarVerdictExcellent;

  /// Simple Mode Polar align panel: the daemon's check verdict 'very_good' (3 arcminutes or better)
  ///
  /// In en, this message translates to:
  /// **'Very good'**
  String get polarVerdictVeryGood;

  /// Simple Mode Polar align panel: the daemon's check verdict 'good' (10 arcminutes or better)
  ///
  /// In en, this message translates to:
  /// **'Good'**
  String get polarVerdictGood;

  /// Simple Mode Polar align panel: the daemon's check verdict 'rough' (30 arcminutes or better)
  ///
  /// In en, this message translates to:
  /// **'Rough'**
  String get polarVerdictRough;

  /// Simple Mode Polar align panel: the daemon's check verdict 'far_off' (worse than 30 arcminutes)
  ///
  /// In en, this message translates to:
  /// **'Far off'**
  String get polarVerdictFarOff;

  /// Simple Mode Polar align panel: routine phase: the three-point check has its result
  ///
  /// In en, this message translates to:
  /// **'Checked'**
  String get polarPhaseChecked;

  /// Simple Mode Polar align panel: rough stage before the first solve
  ///
  /// In en, this message translates to:
  /// **'The main camera measures the mount axis with a turn in RA, then follows your knob moves. Waiting for the first solve…'**
  String get polarRoughWaiting;

  /// Simple Mode Polar align panel, rough stage: the axis sits within half an arcminute of the pole
  ///
  /// In en, this message translates to:
  /// **'The mount axis is on the pole.'**
  String get polarSideOnPole;

  /// Simple Mode Polar align panel, rough stage: which side of the pole the axis sits; parts is e.g. 'above and east'
  ///
  /// In en, this message translates to:
  /// **'The mount axis sits {parts} of the pole.'**
  String polarSideSits(String parts);

  /// Simple Mode Polar align panel, rough stage: the axis sits only above or below the pole (no east/west part), so English drops the 'of'
  ///
  /// In en, this message translates to:
  /// **'The mount axis sits {parts} the pole.'**
  String polarSideSitsVertical(String parts);

  /// Simple Mode Polar align panel, rough stage: the axis sits above the pole (fills polarSideSits)
  ///
  /// In en, this message translates to:
  /// **'above'**
  String get polarSideAbove;

  /// Simple Mode Polar align panel, rough stage: the axis sits below the pole (fills polarSideSits)
  ///
  /// In en, this message translates to:
  /// **'below'**
  String get polarSideBelow;

  /// Simple Mode Polar align panel, rough stage: the axis sits east of the pole (fills polarSideSits)
  ///
  /// In en, this message translates to:
  /// **'east'**
  String get polarSideEast;

  /// Simple Mode Polar align panel, rough stage: the axis sits west of the pole (fills polarSideSits)
  ///
  /// In en, this message translates to:
  /// **'west'**
  String get polarSideWest;

  /// Simple Mode Polar align panel, rough stage: joins the two directions in polarSideSits ('above' + ' and ' + 'east')
  ///
  /// In en, this message translates to:
  /// **' and '**
  String get polarSideJoin;

  /// Simple Mode Polar align panel: the check's verdict
  ///
  /// In en, this message translates to:
  /// **'Three-point check: {verdict}'**
  String polarCheckedLine(String verdict);

  /// Simple Mode Polar align panel: the check's verdict and its disagreement with the readout of the last run (#2082)
  ///
  /// In en, this message translates to:
  /// **'Three-point check: {verdict}, {delta}′ from the last run\'s readout'**
  String polarCheckedVsFine(String verdict, String delta);

  /// Polar align: the button that starts a three-point check, a confirmed new measurement (#2082): on the adjust screen it ends the adjust loop first
  ///
  /// In en, this message translates to:
  /// **'Check'**
  String get polarCheck;

  /// Completes "Couldn't …" for the Check it button
  ///
  /// In en, this message translates to:
  /// **'start the check'**
  String get polarActionCheck;

  /// Simple Mode Polar align panel: altitude axis row label (bolt to turn)
  ///
  /// In en, this message translates to:
  /// **'Altitude'**
  String get polarAltitude;

  /// Simple Mode Polar align panel: azimuth axis row label (bolt to turn)
  ///
  /// In en, this message translates to:
  /// **'Azimuth'**
  String get polarAzimuth;

  /// Simple Mode Polar align panel: warning after a bolt turn made the error worse
  ///
  /// In en, this message translates to:
  /// **'{axis} got worse. Turn that bolt back past where you started.'**
  String polarBoltWorse(String axis);

  /// Simple Mode Polar align panel: warning when frames fail to plate-solve
  ///
  /// In en, this message translates to:
  /// **'No solve ({count} in a row). Check the sky.'**
  String polarNoSolve(int count);

  /// Simple Mode Polar align panel: exposure picker tile label
  ///
  /// In en, this message translates to:
  /// **'Exposure'**
  String get polarExposure;

  /// Simple Mode Polar align panel: button that shoots the next frame in Single mode
  ///
  /// In en, this message translates to:
  /// **'Take frame'**
  String get polarTakeFrame;

  /// Simple Mode Polar align panel: button that finishes polar alignment
  ///
  /// In en, this message translates to:
  /// **'Done'**
  String get polarDone;

  /// Simple Mode search panel: search field placeholder
  ///
  /// In en, this message translates to:
  /// **'Objects, settings, modes, images'**
  String get searchHint;

  /// Simple Mode search panel: heading over the results found in sky
  ///
  /// In en, this message translates to:
  /// **'Sky'**
  String get searchGroupSky;

  /// Simple Mode search panel: heading over the results found in settings
  ///
  /// In en, this message translates to:
  /// **'Settings'**
  String get searchGroupSettings;

  /// Simple Mode search panel: heading over the results found in modes
  ///
  /// In en, this message translates to:
  /// **'Modes'**
  String get searchGroupModes;

  /// Simple Mode search panel: heading over the results found in images
  ///
  /// In en, this message translates to:
  /// **'Images'**
  String get searchGroupImages;

  /// Simple Mode search panel: detail beside an imaged target result, where it opens
  ///
  /// In en, this message translates to:
  /// **'Image Library'**
  String get searchImageLibrary;

  /// Simple Mode search panel: shown when nothing matches what was typed
  ///
  /// In en, this message translates to:
  /// **'Nothing in Simple Mode matches \"{query}\".'**
  String searchNoMatch(String query);

  /// Simple Mode search panel: what screen readers hear after each keystroke, the number of results shown
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{1 match} other{{count} matches}}'**
  String searchMatches(int count);

  /// Simple Mode search panel: name of the Settings sheet's Rig section, shown beside a setting result (and the result for the section itself)
  ///
  /// In en, this message translates to:
  /// **'Rig'**
  String get searchSectionRig;

  /// Simple Mode search panel: name of the Settings sheet's Devices section, shown beside a setting result (and the result for the section itself)
  ///
  /// In en, this message translates to:
  /// **'Devices'**
  String get searchSectionDevices;

  /// Simple Mode search panel: name of the Settings sheet's Camera section, shown beside a setting result (and the result for the section itself)
  ///
  /// In en, this message translates to:
  /// **'Camera (Main)'**
  String get searchSectionCamera;

  /// Simple Mode search panel: name of the Settings sheet's Mount section, shown beside a setting result (and the result for the section itself)
  ///
  /// In en, this message translates to:
  /// **'Mount'**
  String get searchSectionMount;

  /// Simple Mode search panel: name of the Settings sheet's Focuser section, shown beside a setting result (and the result for the section itself)
  ///
  /// In en, this message translates to:
  /// **'Focuser'**
  String get searchSectionFocuser;

  /// Simple Mode search panel: name of the Settings sheet's Guide Camera section, shown beside a setting result (and the result for the section itself)
  ///
  /// In en, this message translates to:
  /// **'Camera (Guide)'**
  String get searchSectionGuider;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Main scope aperture'**
  String get searchSettingAperture;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Main scope focal length'**
  String get searchSettingFocalLength;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Latitude'**
  String get searchSettingLatitude;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Longitude'**
  String get searchSettingLongitude;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Elevation'**
  String get searchSettingElevation;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Location and time'**
  String get searchSettingLocationTime;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Permanent pier'**
  String get searchSettingPermanentPier;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Simple Mode'**
  String get searchSettingSimpleMode;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Change profile'**
  String get searchSettingChangeProfile;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Change rig'**
  String get searchSettingChangeRig;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Versions'**
  String get searchSettingVersions;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Logs & bug report'**
  String get searchSettingLogs;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Connect'**
  String get searchSettingConnect;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Disconnect'**
  String get searchSettingDisconnect;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Reconnect'**
  String get searchSettingReconnect;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Cooler'**
  String get searchSettingCooler;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Cooler target'**
  String get searchSettingCoolerTarget;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Gain'**
  String get searchSettingGain;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Tracking'**
  String get searchSettingTracking;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Park'**
  String get searchSettingPark;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Meridian flip'**
  String get searchSettingMeridianFlip;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Focuser step'**
  String get searchSettingFocuserStep;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Dither'**
  String get searchSettingDither;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'system profile'**
  String get searchWordsRig;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'telescope diameter'**
  String get searchWordsAperture;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'telescope'**
  String get searchWordsFocalLength;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'site location coordinates'**
  String get searchWordsSite;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'site altitude height'**
  String get searchWordsElevation;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'gps clock time zone site'**
  String get searchWordsLocationTime;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'polar alignment observatory'**
  String get searchWordsPermanentPier;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'full ui advanced'**
  String get searchWordsSimpleMode;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'profile'**
  String get searchWordsChangeProfile;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'server'**
  String get searchWordsChangeRig;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'version about firmware driver'**
  String get searchWordsVersions;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'support logs'**
  String get searchWordsLogs;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'equipment gear'**
  String get searchWordsDevices;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'camera temperature cooling'**
  String get searchWordsCooler;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'camera iso hcg'**
  String get searchWordsGain;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'mount sidereal'**
  String get searchWordsTracking;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'mount unpark'**
  String get searchWordsPark;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'mount pier side'**
  String get searchWordsMeridianFlip;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'focus position knob move'**
  String get searchWordsFocuser;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'focus move'**
  String get searchWordsFocuserStep;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'guiding rms phd2'**
  String get searchWordsGuider;

  /// Simple Mode search panel: never shown: space-separated lower-case words that also find this setting; translate as search words people might type
  ///
  /// In en, this message translates to:
  /// **'guiding dithering'**
  String get searchWordsDither;

  /// Simple Mode Preflight strip: step name: autofocus
  ///
  /// In en, this message translates to:
  /// **'Focus'**
  String get simplePreflightStepFocus;

  /// Simple Mode Preflight strip: step name: polar alignment
  ///
  /// In en, this message translates to:
  /// **'Polar align'**
  String get simplePreflightStepPolarAlign;

  /// Simple Mode Preflight strip: step name: the imaging plan
  ///
  /// In en, this message translates to:
  /// **'Plan'**
  String get simplePreflightStepPlan;

  /// Preflight's step that starts guiding
  ///
  /// In en, this message translates to:
  /// **'Guide'**
  String get simplePreflightStepGuide;

  /// Simple Mode Preflight strip: screen-reader label of a step done tonight
  ///
  /// In en, this message translates to:
  /// **'{step}, done'**
  String simplePreflightStepDone(String step);

  /// Simple Mode Preflight strip: screen-reader label of a skipped step
  ///
  /// In en, this message translates to:
  /// **'{step}, skipped'**
  String simplePreflightStepSkipped(String step);

  /// Simple Mode Preflight strip: screen-reader label of the current step
  ///
  /// In en, this message translates to:
  /// **'{step}, current step'**
  String simplePreflightStepCurrent(String step);

  /// Simple Mode Preflight strip: screen-reader label of a step still ahead
  ///
  /// In en, this message translates to:
  /// **'{step}, next'**
  String simplePreflightStepNext(String step);

  /// Simple Mode Preflight strip: button that goes back one step
  ///
  /// In en, this message translates to:
  /// **'Back'**
  String get simplePreflightBack;

  /// Simple Mode Preflight strip: button that skips the current step
  ///
  /// In en, this message translates to:
  /// **'Skip'**
  String get simplePreflightSkip;

  /// Simple Mode mode name (mode grid, Mode: label for screen readers)
  ///
  /// In en, this message translates to:
  /// **'Preflight'**
  String get shellModePreflight;

  /// Simple Mode mode name
  ///
  /// In en, this message translates to:
  /// **'Preview'**
  String get shellModePreview;

  /// Simple Mode mode name
  ///
  /// In en, this message translates to:
  /// **'Polar align'**
  String get shellModePolarAlign;

  /// Simple Mode mode name
  ///
  /// In en, this message translates to:
  /// **'Focus'**
  String get shellModeFocus;

  /// Simple Mode mode name
  ///
  /// In en, this message translates to:
  /// **'Plan'**
  String get shellModePlan;

  /// Simple Mode right rail: short name of Polar align (60 px wide rail, keep very short)
  ///
  /// In en, this message translates to:
  /// **'P.A.'**
  String get shellRailPolarAlign;

  /// Simple Mode right rail: the full name of Preflight
  ///
  /// In en, this message translates to:
  /// **'Preflight'**
  String get shellRailPreflight;

  /// Simple Mode message after the stop keyboard shortcut once; shortcut is the key combo (⌘. or Ctrl+.)
  ///
  /// In en, this message translates to:
  /// **'Press {shortcut} again to stop the run'**
  String shellStopAgain(String shortcut);

  /// Simple Mode question pill when a rail or image tap would drop the unsaved rows of a queued target being edited
  ///
  /// In en, this message translates to:
  /// **'Discard changes to {name}?'**
  String shellDiscardEditQuestion(String name);

  /// Discard-changes question: leave the edit sheet open
  ///
  /// In en, this message translates to:
  /// **'Keep editing'**
  String get shellKeepEditing;

  /// Discard-changes question: drop the edit and carry on with the tap
  ///
  /// In en, this message translates to:
  /// **'Discard'**
  String get shellDiscard;

  /// Simple Mode screen reader label of the image when the controls are hidden
  ///
  /// In en, this message translates to:
  /// **'Show controls'**
  String get shellShowControls;

  /// Simple Mode screen reader label of the image when the controls are shown
  ///
  /// In en, this message translates to:
  /// **'Hide controls'**
  String get shellHideControls;

  /// Simple Mode hint in the middle of the screen before the first frame
  ///
  /// In en, this message translates to:
  /// **'Tap the shutter to take a frame'**
  String get shellTapShutter;

  /// Simple Mode button on a picked sky object: start imaging it
  ///
  /// In en, this message translates to:
  /// **'Image this'**
  String get shellImageThis;

  /// Simple Mode top bar: search button tooltip
  ///
  /// In en, this message translates to:
  /// **'Search'**
  String get shellSearch;

  /// Simple Mode top bar: night mode button tooltip while night mode is on
  ///
  /// In en, this message translates to:
  /// **'Night mode on'**
  String get shellNightModeOn;

  /// Simple Mode top bar: night mode button tooltip while night mode is off
  ///
  /// In en, this message translates to:
  /// **'Night mode'**
  String get shellNightMode;

  /// Simple Mode top bar: Settings button tooltip; title of the Settings page
  ///
  /// In en, this message translates to:
  /// **'Settings'**
  String get shellSettings;

  /// Simple Mode left rail button: show the sky (planetarium)
  ///
  /// In en, this message translates to:
  /// **'Sky Map'**
  String get shellSky;

  /// Simple Mode left rail button: open the Stats page
  ///
  /// In en, this message translates to:
  /// **'Stats'**
  String get shellStats;

  /// Simple Mode left rail button: show the info strips
  ///
  /// In en, this message translates to:
  /// **'Info'**
  String get shellInfo;

  /// Simple Mode left rail button: open the image library
  ///
  /// In en, this message translates to:
  /// **'Library'**
  String get shellLibrary;

  /// Simple Mode screen reader label of the right rail's tab list of the six modes
  ///
  /// In en, this message translates to:
  /// **'Modes'**
  String get shellRailTabs;

  /// Simple Mode screen reader label of a right rail tab whose step is done tonight; mode is the mode name
  ///
  /// In en, this message translates to:
  /// **'{mode}, done tonight'**
  String shellRailTabDone(String mode);

  /// Simple Mode screen reader label of the right rail tab of the mode that runs now; mode is the mode name
  ///
  /// In en, this message translates to:
  /// **'{mode}, running'**
  String shellRailTabRunning(String mode);

  /// Simple Mode screen reader label of the mode button on the right rail; mode is the mode name
  ///
  /// In en, this message translates to:
  /// **'Mode: {mode}'**
  String shellModeSemantics(String mode);

  /// Simple Mode info strip title: the guiding graph
  ///
  /// In en, this message translates to:
  /// **'Guiding'**
  String get shellGuiding;

  /// Simple Mode guiding strip: total RMS guiding error; rms is the formatted value with its unit
  ///
  /// In en, this message translates to:
  /// **'RMS {rms}'**
  String shellGuidingRms(String rms);

  /// Simple Mode guiding strip legend: right ascension line (abbreviation)
  ///
  /// In en, this message translates to:
  /// **'RA'**
  String get shellRa;

  /// Simple Mode guiding strip legend: declination line (abbreviation)
  ///
  /// In en, this message translates to:
  /// **'Dec'**
  String get shellDec;

  /// Simple Mode info strip title: the frame histogram
  ///
  /// In en, this message translates to:
  /// **'Histogram'**
  String get shellHistogram;

  /// Simple Mode histogram strip: the frame's median pixel value
  ///
  /// In en, this message translates to:
  /// **'Median {median}'**
  String shellHistogramMedian(int median);

  /// Simple Mode Settings section: the rig (site, telescope, versions)
  ///
  /// In en, this message translates to:
  /// **'Rig'**
  String get shellSectionRig;

  /// Simple Mode Settings section: the list of devices
  ///
  /// In en, this message translates to:
  /// **'Devices'**
  String get shellSectionDevices;

  /// Simple Mode Settings section and device row: the camera
  ///
  /// In en, this message translates to:
  /// **'Camera (Main)'**
  String get shellSectionCamera;

  /// Simple Mode Settings section and device row: the mount
  ///
  /// In en, this message translates to:
  /// **'Mount'**
  String get shellSectionMount;

  /// Simple Mode Settings section and device row: the focuser
  ///
  /// In en, this message translates to:
  /// **'Focuser'**
  String get shellSectionFocuser;

  /// Simple Mode Settings section and device row: the guide camera
  ///
  /// In en, this message translates to:
  /// **'Camera (Guide)'**
  String get shellSectionGuider;

  /// Simple Mode Settings › Devices, and a full UI settings panel whose values are on the rig (Storage, File naming), when the app has no link to the rig
  ///
  /// In en, this message translates to:
  /// **'Rig not connected'**
  String get shellRigNotConnected;

  /// Simple Mode Settings › Devices row: the filter wheel
  ///
  /// In en, this message translates to:
  /// **'Filter wheel'**
  String get shellFilterWheel;

  /// Simple Mode: the switch devices (power boxes, dew outputs): their window's title, Settings section and status pill
  ///
  /// In en, this message translates to:
  /// **'Switches'**
  String get shellSwitches;

  /// Simple Mode Settings › Devices row: the rotator
  ///
  /// In en, this message translates to:
  /// **'Rotator'**
  String get shellRotator;

  /// Simple Mode Settings › Devices row: the flat panel
  ///
  /// In en, this message translates to:
  /// **'Flat panel'**
  String get shellFlatPanel;

  /// Simple Mode Settings › Devices row: the dome
  ///
  /// In en, this message translates to:
  /// **'Dome'**
  String get shellDome;

  /// Simple Mode Settings › Devices row: the weather station
  ///
  /// In en, this message translates to:
  /// **'Weather'**
  String get shellWeather;

  /// Simple Mode Settings › Devices row title: device type and its name
  ///
  /// In en, this message translates to:
  /// **'{type} · {name}'**
  String shellGearTitle(String type, String name);

  /// Message pill: a Connect / Disconnect / Reconnect in Settings › Devices failed; action is the button's label, device the row's title
  ///
  /// In en, this message translates to:
  /// **'{action} failed · {device}'**
  String shellGearActionFailed(String action, String device);

  /// Message pill button that only closes it (Simple Mode's morning summary)
  ///
  /// In en, this message translates to:
  /// **'OK'**
  String get shellOk;

  /// Simple Mode message pill: the once-a-night pre-sleep checklist
  ///
  /// In en, this message translates to:
  /// **'Before you sleep'**
  String get shellBedtimeTitle;

  /// Pre-sleep pill when nothing needs a look
  ///
  /// In en, this message translates to:
  /// **'Everything is ready for the night.'**
  String get shellBedtimeAllGood;

  /// Pre-sleep pill's one button: answers the checklist for tonight
  ///
  /// In en, this message translates to:
  /// **'Got it — let me go to bed'**
  String get shellBedtimeAnswer;

  /// Simple Mode Settings › Devices row: device state
  ///
  /// In en, this message translates to:
  /// **'Connected'**
  String get shellConnected;

  /// Simple Mode Settings › Devices row: device state
  ///
  /// In en, this message translates to:
  /// **'Connecting…'**
  String get shellConnecting;

  /// Simple Mode Settings › Devices row: device state after a failed connect
  ///
  /// In en, this message translates to:
  /// **'Failed'**
  String get shellFailed;

  /// Simple Mode Settings › Devices row: device state
  ///
  /// In en, this message translates to:
  /// **'Not connected'**
  String get shellNotConnected;

  /// Simple Mode Settings › Devices row button
  ///
  /// In en, this message translates to:
  /// **'Disconnect'**
  String get shellDisconnect;

  /// Simple Mode Settings › Devices row button after a failure
  ///
  /// In en, this message translates to:
  /// **'Reconnect'**
  String get shellReconnect;

  /// Simple Mode Settings › Devices row and dialog title: pick a device this rig has none of yet
  ///
  /// In en, this message translates to:
  /// **'Add a device'**
  String get shellAddDevice;

  /// Simple Mode Settings › Devices row button: open the device chooser
  ///
  /// In en, this message translates to:
  /// **'Choose…'**
  String get shellChooseDevice;

  /// Simple Mode Settings › Devices row button
  ///
  /// In en, this message translates to:
  /// **'Connect'**
  String get shellConnect;

  /// Simple Mode Settings › Rig: message when a changed value failed to save
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t save that'**
  String get shellCouldntSave;

  /// Simple Mode Settings › Rig: message when sending this device's time to the rig failed
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t set the rig\'s clock'**
  String get shellCouldntSetClock;

  /// Simple Mode Settings › Rig: message when no location could be found
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t get a location'**
  String get shellCouldntGetLocation;

  /// Simple Mode Settings › Rig: message when saving the location failed
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t save the site'**
  String get shellCouldntSaveSite;

  /// Simple Mode Settings › Rig: message after the location was set; source names where it came from (e.g. this iPad's GPS)
  ///
  /// In en, this message translates to:
  /// **'Site from {source}'**
  String shellSiteFrom(String source);

  /// Simple Mode Settings › Rig: button beside the rig name or the profile
  ///
  /// In en, this message translates to:
  /// **'Change'**
  String get shellChange;

  /// Simple Mode Settings › Rig: the rig's name
  ///
  /// In en, this message translates to:
  /// **'Name'**
  String get shellName;

  /// Simple Mode Settings › Rig: screen reader label of the Change button beside the rig name
  ///
  /// In en, this message translates to:
  /// **'Change rig'**
  String get shellChangeRig;

  /// Simple Mode Settings › Rig: the active profile
  ///
  /// In en, this message translates to:
  /// **'Profile'**
  String get shellProfile;

  /// Simple Mode Settings › Rig: screen reader label of the Change button beside the profile
  ///
  /// In en, this message translates to:
  /// **'Change profile'**
  String get shellChangeProfile;

  /// Simple Mode Settings › Rig: note under the profile while a plan runs
  ///
  /// In en, this message translates to:
  /// **'A plan is running. It keeps running on the rig; pick this profile again to come back to it.'**
  String get shellPlanRunning;

  /// Simple Mode Settings › Rig: the telescope's aperture
  ///
  /// In en, this message translates to:
  /// **'Main scope aperture'**
  String get shellAperture;

  /// Simple Mode Settings › Rig: the telescope's focal length
  ///
  /// In en, this message translates to:
  /// **'Main scope focal length'**
  String get shellFocalLength;

  /// Simple Mode Settings › Rig: the site's latitude
  ///
  /// In en, this message translates to:
  /// **'Latitude'**
  String get shellLatitude;

  /// Simple Mode Settings › Rig: the site's longitude
  ///
  /// In en, this message translates to:
  /// **'Longitude'**
  String get shellLongitude;

  /// Simple Mode Settings › Rig: the site's elevation
  ///
  /// In en, this message translates to:
  /// **'Elevation'**
  String get shellElevation;

  /// Simple Mode Settings › Rig button; device names this device (e.g. this iPad)
  ///
  /// In en, this message translates to:
  /// **'Use {device}\'s location and time'**
  String shellUseLocation(String device);

  /// Simple Mode Settings › Rig switch: the mount stays on a permanent pier
  ///
  /// In en, this message translates to:
  /// **'Permanent pier'**
  String get shellPermanentPier;

  /// Simple Mode Settings › Rig: hint under the Permanent pier switch
  ///
  /// In en, this message translates to:
  /// **'Skips polar alignment'**
  String get shellPermanentPierHint;

  /// Simple Mode status pill: a Preflight step with nothing running
  ///
  /// In en, this message translates to:
  /// **'{step}: waiting for you'**
  String previewPreflightWaiting(Object step);

  /// Simple Mode status strip: tooltip of the button back to the Full UI
  ///
  /// In en, this message translates to:
  /// **'Full UI'**
  String get shellFullUi;

  /// OpenAstro Ara sheet: screen-reader name of the link to the licenses page
  ///
  /// In en, this message translates to:
  /// **'Open-source licenses'**
  String get shellOpenSourceLicenses;

  /// OpenAstro Ara sheet: short link to the licenses page on the title line
  ///
  /// In en, this message translates to:
  /// **'Licenses'**
  String get shellLicenses;

  /// Simple Mode Settings › Rig switch: turn Simple Mode off to go to the full app
  ///
  /// In en, this message translates to:
  /// **'Simple Mode'**
  String get shellSimpleMode;

  /// Simple Mode Settings › Rig versions list: this app's version
  ///
  /// In en, this message translates to:
  /// **'This app'**
  String get shellThisApp;

  /// Simple Mode Settings › Rig versions list: the rig's server (OpenAstro Ara is the product name)
  ///
  /// In en, this message translates to:
  /// **'Ara server'**
  String get shellAraServer;

  /// Simple Mode Settings › Rig versions list: the plate solving star databases
  ///
  /// In en, this message translates to:
  /// **'Star database'**
  String get shellStarDatabase;

  /// Simple Mode Settings › Rig versions list: the rig's operating system and board
  ///
  /// In en, this message translates to:
  /// **'System'**
  String get shellSystem;

  /// Simple Mode Settings › Rig button: open the logs and send a bug report
  ///
  /// In en, this message translates to:
  /// **'Logs & bug report'**
  String get shellLogs;

  /// Simple Mode Settings › Rig: an aperture or focal length not set yet
  ///
  /// In en, this message translates to:
  /// **'Not set'**
  String get shellNotSet;

  /// Simple Mode Settings › Rig: latitude hemisphere, north (one letter)
  ///
  /// In en, this message translates to:
  /// **'N'**
  String get shellHemiNorth;

  /// Simple Mode Settings › Rig: latitude hemisphere, south (one letter)
  ///
  /// In en, this message translates to:
  /// **'S'**
  String get shellHemiSouth;

  /// Simple Mode Settings › Rig: longitude hemisphere, east (one letter)
  ///
  /// In en, this message translates to:
  /// **'E'**
  String get shellHemiEast;

  /// Simple Mode Settings › Rig: longitude hemisphere, west (one letter)
  ///
  /// In en, this message translates to:
  /// **'W'**
  String get shellHemiWest;

  /// Simple Mode Settings › Rig: screen reader label of one digit wheel; label is the value's name
  ///
  /// In en, this message translates to:
  /// **'{label}, wheel {index} of {count}'**
  String shellWheelSemantics(String label, int index, int count);

  /// Simple Mode Settings › Rig: screen reader label of the + button (elevation)
  ///
  /// In en, this message translates to:
  /// **'Raise'**
  String get shellRaise;

  /// Simple Mode Settings › Rig: screen reader label of the − button (elevation)
  ///
  /// In en, this message translates to:
  /// **'Lower'**
  String get shellLower;

  /// Plan sheet, under the picked target: it never rises at this site
  ///
  /// In en, this message translates to:
  /// **'Never rises here'**
  String get planNeverRises;

  /// Plan sheet, under the picked target: it is below the horizon, no rise time known
  ///
  /// In en, this message translates to:
  /// **'Below your horizon'**
  String get planBelowHorizon;

  /// Plan sheet, under the picked target: below the horizon, with its rise time (HH:MM)
  ///
  /// In en, this message translates to:
  /// **'Below your horizon · rises {time}'**
  String planBelowHorizonRises(String time);

  /// Plan sheet, under the picked target: altitude now and highest tonight, in degrees
  ///
  /// In en, this message translates to:
  /// **'{alt}° up · max {max}°'**
  String planAltitudeUpMax(int alt, int max);

  /// Tonight's best suggestion line: altitude now in degrees
  ///
  /// In en, this message translates to:
  /// **'{alt}° up'**
  String planAltitudeUp(int alt);

  /// Tonight's best suggestion line, lowercase: below the horizon
  ///
  /// In en, this message translates to:
  /// **'below the horizon'**
  String get planBelowTheHorizon;

  /// Lowercase: when a target rises (HH:MM); suggestion line and plan square
  ///
  /// In en, this message translates to:
  /// **'rises {time}'**
  String planRisesAt(String time);

  /// Shutter screen-reader label while the plan waits for the target to rise (HH:MM)
  ///
  /// In en, this message translates to:
  /// **'Rises {time}'**
  String planRisesAtCap(String time);

  /// Plan square: the run is pausing after the current frame
  ///
  /// In en, this message translates to:
  /// **'Pausing…'**
  String get planPausing;

  /// Plan square: the run is paused
  ///
  /// In en, this message translates to:
  /// **'Paused'**
  String get planPaused;

  /// Plan square state word while the plan waits out the meridian before the flip (#1747)
  ///
  /// In en, this message translates to:
  /// **'Waiting for flip'**
  String get planWaitingForFlip;

  /// Plan square: the target or run is finished
  ///
  /// In en, this message translates to:
  /// **'Done'**
  String get planDone;

  /// Screen-reader label of the +N plan square: targets not shown
  ///
  /// In en, this message translates to:
  /// **'{count, plural, other{{count} more in the queue}}'**
  String planMoreInQueue(int count);

  /// Plan square for the hidden rest of the queue, lowercase
  ///
  /// In en, this message translates to:
  /// **'queued'**
  String get planQueued;

  /// Screen-reader label of a plan square, with the target name
  ///
  /// In en, this message translates to:
  /// **'Plan: {name}'**
  String planSquareLabel(String name);

  /// Shutter screen-reader label: nothing to start yet
  ///
  /// In en, this message translates to:
  /// **'Pick a target first'**
  String get planPickTargetFirst;

  /// Shutter screen-reader label: start the run
  ///
  /// In en, this message translates to:
  /// **'Start imaging'**
  String get planStartImaging;

  /// Shutter screen-reader label while imaging
  ///
  /// In en, this message translates to:
  /// **'Pause after this frame'**
  String get planPauseAfterFrame;

  /// Shutter screen-reader label while pausing
  ///
  /// In en, this message translates to:
  /// **'Pausing'**
  String get planPausingLabel;

  /// Plan pill and the greyed shutter's tap while a pause waits for the frame in hand to finish
  ///
  /// In en, this message translates to:
  /// **'Pausing after this frame'**
  String get planPausingAfterFrame;

  /// Shutter screen-reader label while the plan waits out the meridian before the flip (#1747)
  ///
  /// In en, this message translates to:
  /// **'Pause after the flip'**
  String get planPauseAfterFlip;

  /// Shutter screen-reader label while paused
  ///
  /// In en, this message translates to:
  /// **'Resume'**
  String get planResume;

  /// Plan sheet caption above tonight's suggested targets (uppercase)
  ///
  /// In en, this message translates to:
  /// **'TONIGHT\'S BEST'**
  String get planTonightsBest;

  /// Plan sheet heading when editing a queued target
  ///
  /// In en, this message translates to:
  /// **'Edit {name}'**
  String planEditTarget(String name);

  /// Plan sheet search field hint
  ///
  /// In en, this message translates to:
  /// **'Search objects'**
  String get planSearchObjects;

  /// Tooltip of the plan sheet's search button
  ///
  /// In en, this message translates to:
  /// **'Search'**
  String get planSearch;

  /// Plan sheet hint when no target is picked
  ///
  /// In en, this message translates to:
  /// **'Search, or pick in the sky.'**
  String get planSearchOrPick;

  /// Plan sheet button: pick the target in the planetarium
  ///
  /// In en, this message translates to:
  /// **'Pick in Sky'**
  String get planPickInSky;

  /// Plan sheet row tile label: sub-exposure length
  ///
  /// In en, this message translates to:
  /// **'Exposure'**
  String get planExposure;

  /// Exposure option marked as the suggested length; exposure is e.g. 2 min
  ///
  /// In en, this message translates to:
  /// **'{exposure} · suggested'**
  String planExposureSuggested(String exposure);

  /// Plan sheet row tile label: frame count; Night done row label for an unfiltered row
  ///
  /// In en, this message translates to:
  /// **'Frames'**
  String get planFrames;

  /// Plan sheet line with the suggested sub lengths, e.g. L 2 min · GB 3 min
  ///
  /// In en, this message translates to:
  /// **'Suggested: {hint}'**
  String planSuggested(String hint);

  /// Plan sheet button: apply all suggested sub lengths
  ///
  /// In en, this message translates to:
  /// **'Use all'**
  String get planUseAll;

  /// Plan sheet: total frames in the plan; progress panel frame count
  ///
  /// In en, this message translates to:
  /// **'{count, plural, other{{count} frames}}'**
  String planFrameCount(int count);

  /// Plan sheet warning: polar alignment not done tonight
  ///
  /// In en, this message translates to:
  /// **'Not polar aligned tonight'**
  String get planNotPolarAligned;

  /// Plan sheet: the run starts right away
  ///
  /// In en, this message translates to:
  /// **'Starts now'**
  String get planStartsNow;

  /// Plan sheet: the run waits for dark (HH:MM)
  ///
  /// In en, this message translates to:
  /// **'Starts at dark ({time})'**
  String planStartsAtDark(String time);

  /// Plan sheet: imaging stops at dawn (HH:MM, the site's twilight definition) and the mount parks
  ///
  /// In en, this message translates to:
  /// **'Stops at dawn · {time}'**
  String planStopsAtDawn(String time);

  /// Plan square caption on the queue's last target: the mount parks once the queue ends. One short word: the square has 60 px for it at 11.5 px
  ///
  /// In en, this message translates to:
  /// **'Parks'**
  String get planSquareParks;

  /// Plan sheet: the run starts at a picked time (HH:MM)
  ///
  /// In en, this message translates to:
  /// **'Starts at {time}'**
  String planStartsAt(String time);

  /// Plan sheet button: don't wait, start right away
  ///
  /// In en, this message translates to:
  /// **'Start now instead'**
  String get planStartNowInstead;

  /// Plan sheet button: wait for dark to start (HH:MM)
  ///
  /// In en, this message translates to:
  /// **'Wait for dark ({time})'**
  String planWaitForDark(String time);

  /// Plan sheet button: choose a start time
  ///
  /// In en, this message translates to:
  /// **'Pick a time'**
  String get planPickATime;

  /// Plan sheet button: leave the edit without saving
  ///
  /// In en, this message translates to:
  /// **'Cancel'**
  String get planCancel;

  /// Plan sheet save button while saving
  ///
  /// In en, this message translates to:
  /// **'Saving…'**
  String get planSaving;

  /// Plan sheet button: save the edited target
  ///
  /// In en, this message translates to:
  /// **'Save'**
  String get planSave;

  /// Plan sheet confirmation after adding a target to the queue
  ///
  /// In en, this message translates to:
  /// **'{name} added · {count} in the queue'**
  String planTargetAdded(String name, int count);

  /// Plan sheet button: queue the picked target
  ///
  /// In en, this message translates to:
  /// **'Add to queue'**
  String get planAddToQueue;

  /// Plan sheet start button while starting
  ///
  /// In en, this message translates to:
  /// **'Starting…'**
  String get planStarting;

  /// Plan sheet start button with a scheduled start (HH:MM)
  ///
  /// In en, this message translates to:
  /// **'Start at {time}'**
  String planStartAt(String time);

  /// Plan sheet start button when a queue exists: start this target now
  ///
  /// In en, this message translates to:
  /// **'Start now'**
  String get planStartNow;

  /// Plan sheet start button: create the plan and start it
  ///
  /// In en, this message translates to:
  /// **'Create and start'**
  String get planCreateAndStart;

  /// Message after a target is removed from the queue
  ///
  /// In en, this message translates to:
  /// **'{name} removed'**
  String planTargetRemoved(String name);

  /// Message action: put the removed target back
  ///
  /// In en, this message translates to:
  /// **'Undo'**
  String get planUndo;

  /// Screen-reader label of a queue row's drag handle
  ///
  /// In en, this message translates to:
  /// **'Move {name}'**
  String planMoveTarget(String name);

  /// Tooltip of a queue row's remove button
  ///
  /// In en, this message translates to:
  /// **'Remove {name} from the queue'**
  String planRemoveFromQueue(String name);

  /// Shown behind a queue row swiped to remove it
  ///
  /// In en, this message translates to:
  /// **'Remove'**
  String get planRemove;

  /// Queue caption (uppercase): target count and total time, e.g. 6 h 20 min
  ///
  /// In en, this message translates to:
  /// **'QUEUE · {count} {count, plural, =1{target} other{targets}} · {duration}'**
  String planQueueHeader(int count, String duration);

  /// Button under a run error: open storage settings
  ///
  /// In en, this message translates to:
  /// **'Open Storage'**
  String get planOpenStorage;

  /// Night done heading after the run ends
  ///
  /// In en, this message translates to:
  /// **'Night done'**
  String get planNightDone;

  /// Night done summary, lowercase: dawn ended the run before its frames ran out
  ///
  /// In en, this message translates to:
  /// **'stopped at dawn'**
  String get planStoppedAtDawn;

  /// Night done summary, lowercase: the run was stopped
  ///
  /// In en, this message translates to:
  /// **'stopped'**
  String get planStopped;

  /// Night done summary, lowercase: the run failed
  ///
  /// In en, this message translates to:
  /// **'failed'**
  String get planFailed;

  /// Night done: the step where the run stopped
  ///
  /// In en, this message translates to:
  /// **'Stopped at: {step}'**
  String planStoppedAt(String step);

  /// Night done button: start a new plan
  ///
  /// In en, this message translates to:
  /// **'New plan'**
  String get planNewPlan;

  /// Progress panel caption (uppercase) with the target name
  ///
  /// In en, this message translates to:
  /// **'IMAGING {name}'**
  String planImagingTarget(String name);

  /// Progress panel: a panic stop paused the run
  ///
  /// In en, this message translates to:
  /// **'Mount stopped'**
  String get planMountStopped;

  /// Progress panel: when the queue ends (HH:MM) and the time left, e.g. 1 h 5 min
  ///
  /// In en, this message translates to:
  /// **'Ends {time} · {duration} left'**
  String planEndsLeft(String time, String duration);

  /// Progress panel hint while paused
  ///
  /// In en, this message translates to:
  /// **'Tap the shutter to resume and re-center.'**
  String get planHintResume;

  /// Progress panel button while paused
  ///
  /// In en, this message translates to:
  /// **'Resume without re-centering'**
  String get planResumeWithoutRecenter;

  /// Progress panel button while paused: resume after re-centering and refocusing
  ///
  /// In en, this message translates to:
  /// **'Re-center and refocus'**
  String get planRecenterAndRefocus;

  /// Progress panel hint while imaging
  ///
  /// In en, this message translates to:
  /// **'Tap the shutter to pause after this frame.'**
  String get planHintPause;

  /// Plan's panel button while a plan runs: asks in place before it stops the plan (the shutter pauses; a 2 s hold is Emergency Stop)
  ///
  /// In en, this message translates to:
  /// **'Stop plan…'**
  String get planStopPlan;

  /// Plan's panel: the in-place question after Stop plan…; count is every frame the run took, across all its targets
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =0{Stop the plan? The mount keeps tracking.} =1{Stop the plan? The 1 frame taken is kept; the mount keeps tracking.} other{Stop the plan? The {count} frames taken are kept; the mount keeps tracking.}}'**
  String planStopPlanAsk(int count);

  /// Plan's panel: confirms stopping the plan (not an Emergency Stop)
  ///
  /// In en, this message translates to:
  /// **'Stop plan'**
  String get planStopPlanConfirm;

  /// Plan's panel: answers the stop question with no stop
  ///
  /// In en, this message translates to:
  /// **'Keep imaging'**
  String get planKeepImaging;

  /// Progress panel button: queue another target
  ///
  /// In en, this message translates to:
  /// **'Add target'**
  String get planAddTarget;

  /// Search suggestion: the object's magnitude, e.g. mag 8.4
  ///
  /// In en, this message translates to:
  /// **'mag {value}'**
  String planMagnitude(String value);

  /// Plan sheet: Add to queue on a target the queue already holds
  ///
  /// In en, this message translates to:
  /// **'{name} is already in the queue.'**
  String planAlreadyQueued(String name);

  /// Plan sheet / Preflight: Add to queue got no answer from the rig (saved nothing); shown as a pill
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t reach the rig. Check the connection and try again.'**
  String get planRigUnreachable;

  /// Simple Mode queue, after Couldn't: Add to queue failed
  ///
  /// In en, this message translates to:
  /// **'create the plan'**
  String get planActionCreate;

  /// Simple Mode queue, after Couldn't: saving an edited target failed
  ///
  /// In en, this message translates to:
  /// **'save the edit'**
  String get planActionSaveEdit;

  /// Simple Mode queue, after Couldn't: Undo of a removed target failed
  ///
  /// In en, this message translates to:
  /// **'put it back'**
  String get planActionPutBack;

  /// Simple Mode queue, after Couldn't
  ///
  /// In en, this message translates to:
  /// **'reorder the queue'**
  String get planActionReorder;

  /// Simple Mode queue, after Couldn't
  ///
  /// In en, this message translates to:
  /// **'remove it from the queue'**
  String get planActionRemove;

  /// Simple Mode queue, after Couldn't: writing the start time failed
  ///
  /// In en, this message translates to:
  /// **'schedule the start'**
  String get planActionSchedule;

  /// Simple Mode shutter, after Couldn't
  ///
  /// In en, this message translates to:
  /// **'start imaging'**
  String get planActionStart;

  /// Simple Mode shutter, after Couldn't
  ///
  /// In en, this message translates to:
  /// **'pause'**
  String get planActionPause;

  /// Simple Mode shutter, after Couldn't
  ///
  /// In en, this message translates to:
  /// **'resume'**
  String get planActionResume;

  /// Simple Mode shutter, after Couldn't
  ///
  /// In en, this message translates to:
  /// **'stop the run'**
  String get planActionStop;

  /// Plan sheet: Add to queue while the run is winding down (the rig refused the append)
  ///
  /// In en, this message translates to:
  /// **'The run is ending; this target can\'t join it.'**
  String get planRunEnding;

  /// Message pill: Start (Preflight's last Done) while an edit or reorder of the queue is still on its way
  ///
  /// In en, this message translates to:
  /// **'The queue is still updating on the rig. Try again in a moment.'**
  String get planQueueBusy;

  /// Settings › Rig: the app language row (this device only)
  ///
  /// In en, this message translates to:
  /// **'Language'**
  String get shellLanguage;

  /// Settings › Rig language picker: follow the device's language
  ///
  /// In en, this message translates to:
  /// **'System'**
  String get shellLanguageSystem;

  /// Emergency Stop button (Simple Mode rail and full UI status bar): the compact square's label at rest; keep it short (60 px)
  ///
  /// In en, this message translates to:
  /// **'STOP'**
  String get estopCompactStop;

  /// Emergency Stop button (Simple Mode rail and full UI status bar): the compact square's label while held
  ///
  /// In en, this message translates to:
  /// **'HOLD'**
  String get estopCompactHold;

  /// Emergency Stop button (Simple Mode rail and full UI status bar): the compact square's label after one keyboard/screen-reader press; two short lines (60 px)
  ///
  /// In en, this message translates to:
  /// **'PRESS\nAGAIN'**
  String get estopCompactPressAgain;

  /// Emergency Stop button (Simple Mode rail and full UI status bar): label while the stop request is in flight
  ///
  /// In en, this message translates to:
  /// **'Stopping…'**
  String get estopStopping;

  /// Emergency Stop button (Simple Mode rail and full UI status bar): label while the press is held
  ///
  /// In en, this message translates to:
  /// **'Hold to confirm'**
  String get estopHoldToConfirm;

  /// Emergency Stop button (Simple Mode rail and full UI status bar): label after one keyboard/screen-reader press
  ///
  /// In en, this message translates to:
  /// **'Press again'**
  String get estopPressAgain;

  /// Emergency Stop button (Simple Mode rail and full UI status bar): label at rest
  ///
  /// In en, this message translates to:
  /// **'Emergency Stop'**
  String get estopLabel;

  /// Emergency Stop button (Simple Mode rail and full UI status bar): screen-reader announcement after the first press
  ///
  /// In en, this message translates to:
  /// **'Press again within 3 seconds to stop everything'**
  String get estopPressAgainAnnouncement;

  /// Emergency Stop confirm dialog (keyboard/screen reader, full UI): title
  ///
  /// In en, this message translates to:
  /// **'Stop everything now?'**
  String get estopConfirmTitle;

  /// Emergency Stop confirm dialog: what the stop does
  ///
  /// In en, this message translates to:
  /// **'This aborts the running sequence, stops the mount where it is with tracking off, halts the rotator where it is, aborts the in-flight exposure, stops the guide-camera focus loop, the Bahtinov readout, autofocus and polar alignment, stops guiding, and switches the flat panel light and the camera cooler off. It cannot be undone — the sequence does not resume.'**
  String get estopConfirmBody;

  /// Emergency Stop confirm dialog: cancel button
  ///
  /// In en, this message translates to:
  /// **'Cancel'**
  String get estopCancel;

  /// Emergency Stop confirm dialog: the confirm button
  ///
  /// In en, this message translates to:
  /// **'EMERGENCY STOP'**
  String get estopConfirm;

  /// Emergency Stop message when no rig is connected
  ///
  /// In en, this message translates to:
  /// **'No server connected — nothing to stop.'**
  String get estopNoServer;

  /// Emergency Stop message when the request itself failed; error is a full sentence saying why
  ///
  /// In en, this message translates to:
  /// **'EMERGENCY STOP FAILED — {error} Check your rig manually.'**
  String estopFailed(String error);

  /// Emergency Stop: error message tail after "Couldn't " when the rig can't be reached; lower-case, no period
  ///
  /// In en, this message translates to:
  /// **'reach the rig'**
  String get estopActionReach;

  /// Emergency Stop result when a stop was already in progress
  ///
  /// In en, this message translates to:
  /// **'An emergency stop is already running — no second volley sent.'**
  String get estopAlreadyRunning;

  /// Emergency Stop result, one item of the comma-separated list: running sequences aborted
  ///
  /// In en, this message translates to:
  /// **'{count, plural, other{{count} sequence run(s) aborted}}'**
  String estopRunsAborted(int count);

  /// Emergency Stop result list item
  ///
  /// In en, this message translates to:
  /// **'no sequence was running'**
  String get estopNoSequence;

  /// Emergency Stop result list item
  ///
  /// In en, this message translates to:
  /// **'exposure aborted'**
  String get estopExposureAborted;

  /// Emergency Stop result list item
  ///
  /// In en, this message translates to:
  /// **'guiding stopped'**
  String get estopGuidingStopped;

  /// Emergency Stop result list item
  ///
  /// In en, this message translates to:
  /// **'mount stopped'**
  String get estopMountStopped;

  /// Emergency Stop result list item
  ///
  /// In en, this message translates to:
  /// **'flat panel light off'**
  String get estopFlatPanelOff;

  /// Emergency Stop result list item
  ///
  /// In en, this message translates to:
  /// **'camera cooler off'**
  String get estopCoolerOff;

  /// Emergency Stop result; parts is the comma-separated list of what each step did
  ///
  /// In en, this message translates to:
  /// **'Emergency stop executed: {parts}.'**
  String estopSummary(String parts);

  /// Emergency Stop result when a step failed; failures is the semicolon-separated list
  ///
  /// In en, this message translates to:
  /// **'CHECK THE RIG: {failures}.'**
  String estopCheckRig(String failures);

  /// Emergency Stop failed step
  ///
  /// In en, this message translates to:
  /// **'SEQUENCE ABORT FAILED (the run may still be executing)'**
  String get estopFailAbortRuns;

  /// Emergency Stop failed step
  ///
  /// In en, this message translates to:
  /// **'EXPOSURE ABORT FAILED'**
  String get estopFailAbortExposure;

  /// Emergency Stop failed step
  ///
  /// In en, this message translates to:
  /// **'GUIDING MAY STILL BE ACTIVE (stop failed)'**
  String get estopFailStopGuiding;

  /// Emergency Stop failed step
  ///
  /// In en, this message translates to:
  /// **'MOUNT STOP FAILED — verify it manually'**
  String get estopFailStopMount;

  /// Emergency Stop failed step
  ///
  /// In en, this message translates to:
  /// **'ROTATOR HALT FAILED — it may still be turning'**
  String get estopFailRotator;

  /// Emergency Stop failed step
  ///
  /// In en, this message translates to:
  /// **'FLAT PANEL LIGHT-OFF FAILED'**
  String get estopFailFlatPanel;

  /// Emergency Stop failed step
  ///
  /// In en, this message translates to:
  /// **'CAMERA COOLER STILL ON (switching it off failed)'**
  String get estopFailCooler;

  /// Emergency Stop failed step
  ///
  /// In en, this message translates to:
  /// **'GUIDE-CAMERA FOCUS LOOP MAY STILL BE RUNNING (stop failed)'**
  String get estopFailGuideFocus;

  /// Emergency Stop failed step
  ///
  /// In en, this message translates to:
  /// **'BAHTINOV READOUT MAY STILL BE RUNNING (stop failed)'**
  String get estopFailBahtinov;

  /// Emergency Stop failed step
  ///
  /// In en, this message translates to:
  /// **'AUTOFOCUS MAY STILL BE RUNNING (cancel failed)'**
  String get estopFailAutofocus;

  /// Emergency Stop failed step
  ///
  /// In en, this message translates to:
  /// **'POLAR ALIGNMENT MAY STILL BE RUNNING (stop failed)'**
  String get estopFailPolarAlign;

  /// Emergency Stop failed step the app has no wording for; step is the daemon's token
  ///
  /// In en, this message translates to:
  /// **'{step} FAILED'**
  String estopFailOther(String step);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): headline after a run
  ///
  /// In en, this message translates to:
  /// **'In focus'**
  String get focusCardInFocus;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): headline after a run, with the final HFR
  ///
  /// In en, this message translates to:
  /// **'In focus · HFR {hfr}'**
  String focusCardInFocusHfr(String hfr);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): headline after a failed run
  ///
  /// In en, this message translates to:
  /// **'Autofocus failed'**
  String get focusCardFailed;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): headline/phase after a cancelled run
  ///
  /// In en, this message translates to:
  /// **'Canceled'**
  String get focusCardCancelled;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): headline before any run
  ///
  /// In en, this message translates to:
  /// **'Not focused yet'**
  String get focusCardNotFocused;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): failure banner when the daemon gave no reason
  ///
  /// In en, this message translates to:
  /// **'Autofocus failed — see Support → Logs'**
  String get focusCardFailedSeeLogs;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): failure banner tail; position is the focuser step
  ///
  /// In en, this message translates to:
  /// **'The focuser is back at {position}.'**
  String focusCardBackAt(String position);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase while Smart Focus runs
  ///
  /// In en, this message translates to:
  /// **'Smart Focus — shot {shot} of {total}'**
  String focusCardSmart(String shot, String total);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase
  ///
  /// In en, this message translates to:
  /// **'Finding rough focus…'**
  String get focusCardCoarse;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase during the V-curve sweep
  ///
  /// In en, this message translates to:
  /// **'Sweeping — probe {done} of {total}'**
  String focusCardSweeping(String done, String total);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): after the sweep phase when it is not the first sweep
  ///
  /// In en, this message translates to:
  /// **'(pass {pass})'**
  String focusCardPass(String pass);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase
  ///
  /// In en, this message translates to:
  /// **'Fitting the curve…'**
  String get focusCardFitting;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase
  ///
  /// In en, this message translates to:
  /// **'Moving to best focus…'**
  String get focusCardMoving;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase
  ///
  /// In en, this message translates to:
  /// **'Confirming focus…'**
  String get focusCardConfirming;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase
  ///
  /// In en, this message translates to:
  /// **'Measuring focuser backlash…'**
  String get focusCardBacklash;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase the app has no wording for
  ///
  /// In en, this message translates to:
  /// **'Autofocus running…'**
  String get focusCardRunning;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase line after a run; position is the focuser step
  ///
  /// In en, this message translates to:
  /// **'In focus at {position}'**
  String focusCardInFocusAt(String position);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): after the In focus phase line; duration like 2m 00s
  ///
  /// In en, this message translates to:
  /// **'in {duration}'**
  String focusCardTook(String duration);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase line after a failed run; reason is the daemon's text
  ///
  /// In en, this message translates to:
  /// **'Failed: {reason}'**
  String focusCardFailedReason(String reason);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): failed phase line when the daemon gave no reason
  ///
  /// In en, this message translates to:
  /// **'see Support → Logs'**
  String get focusCardSeeLogs;

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): after a failed/cancelled phase line, following a dash
  ///
  /// In en, this message translates to:
  /// **'focuser restored to {position}'**
  String focusCardRestoredPosition(String position);

  /// Focus panel, main telescope card (Simple Mode Focus and full UI): phase line before any run
  ///
  /// In en, this message translates to:
  /// **'No autofocus yet this session.'**
  String get focusCardNoRunYet;

  /// Filter picker (Simple Mode camera window and full UI): field label
  ///
  /// In en, this message translates to:
  /// **'Filter'**
  String get filterDropdownLabel;

  /// Filter picker: message when the wheel never moved
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t change the filter - the wheel didn\'t start moving.'**
  String get filterDropdownDidntMove;

  /// Filter picker: the picked name matches no slot on the wheel
  ///
  /// In en, this message translates to:
  /// **'Filter \'{name}\' isn\'t a slot on the connected wheel.'**
  String filterDropdownNotASlot(String name);

  /// Filter picker: the rig is busy with another action
  ///
  /// In en, this message translates to:
  /// **'Another action is still in progress.'**
  String get filterDropdownBusy;

  /// Filter picker: error message tail after "Couldn't "; lower-case, no period
  ///
  /// In en, this message translates to:
  /// **'change the filter'**
  String get filterDropdownActionChange;

  /// Polar align (Simple Mode panel and full UI): an axis within tolerance
  ///
  /// In en, this message translates to:
  /// **'On target'**
  String get polarHintOnTarget;

  /// Polar align (Simple Mode panel and full UI): altitude bolt direction
  ///
  /// In en, this message translates to:
  /// **'Lower'**
  String get polarHintLower;

  /// Polar align (Simple Mode panel and full UI): altitude bolt direction
  ///
  /// In en, this message translates to:
  /// **'Raise'**
  String get polarHintRaise;

  /// Polar align (Simple Mode panel and full UI): azimuth bolt direction
  ///
  /// In en, this message translates to:
  /// **'Move west'**
  String get polarHintMoveWest;

  /// Polar align (Simple Mode panel and full UI): azimuth bolt direction
  ///
  /// In en, this message translates to:
  /// **'Move east'**
  String get polarHintMoveEast;

  /// Polar align (Simple Mode panel and full UI) full UI: warning after an altitude bolt turn made the error worse
  ///
  /// In en, this message translates to:
  /// **'Altitude got worse after that turn — the altitude bolt moves the axis the other way on this mount. Turn it back past where you started.'**
  String get polarHintAltitudeWorse;

  /// Polar align (Simple Mode panel and full UI) full UI: warning after an azimuth bolt turn made the error worse
  ///
  /// In en, this message translates to:
  /// **'Azimuth got worse after that turn — the azimuth bolt moves the axis the other way on this mount. Turn it back past where you started.'**
  String get polarHintAzimuthWorse;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Not guiding'**
  String get guiderStateNotGuiding;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Looping'**
  String get guiderStateLooping;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Star selected'**
  String get guiderStateStarSelected;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Calibrating'**
  String get guiderStateCalibrating;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Guiding'**
  String get guiderStateGuiding;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Settling'**
  String get guiderStateSettling;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Paused'**
  String get guiderStatePaused;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Star lost'**
  String get guiderStateStarLost;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Dithering'**
  String get guiderStateDithering;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Guiding Assistant'**
  String get guiderStateAssistant;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Unknown'**
  String get guiderStateUnknown;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab)
  ///
  /// In en, this message translates to:
  /// **'Not connected'**
  String get guiderStateNotConnected;

  /// Guider state (Simple Mode guider window, guiding strip, full UI guider page and Run tab): the calibration leg; direction is the guider's own word (North, West)
  ///
  /// In en, this message translates to:
  /// **'{direction} step {step}'**
  String guiderStateCalibrationStep(String direction, String step);

  /// Find star failed; reason is a full sentence
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t find a star: {reason}'**
  String guideFindStarFailed(String reason);

  /// Error message lead (every screen); action is a lower-case phrase like "save that"
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t {action}'**
  String errorCouldnt(String action);

  /// Error message: the action when the screen names none, after "Couldn't "
  ///
  /// In en, this message translates to:
  /// **'do that'**
  String get errorActionDefault;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start; device names this device (e.g. this iPhone or iPad)
  ///
  /// In en, this message translates to:
  /// **'your rig didn\'t answer. Check it\'s powered on and on the same network as {device}.'**
  String errorNoAnswer(String device);

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'your rig took too long to answer. It may be busy; try again in a moment.'**
  String get errorTooSlow;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'the connection wasn\'t trusted.'**
  String get errorUntrusted;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'the request was canceled.'**
  String get errorCancelled;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'your rig doesn\'t have that. It may be running an older version of Ara.'**
  String get errorNotFound;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'something else is using it right now.'**
  String get errorBusy;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'your rig refused those values.'**
  String get errorRefusedValues;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'your rig hit an error. Support → Logs has the details.'**
  String get errorServer;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'your rig refused the request.'**
  String get errorRefused;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'the connection failed. Check your rig is reachable.'**
  String get errorConnectionFailed;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start; followed by a period or by ": <reason>."
  ///
  /// In en, this message translates to:
  /// **'a file problem got in the way'**
  String get errorFile;

  /// Error message (every screen), after "Couldn't <action> — "; lower-case start
  ///
  /// In en, this message translates to:
  /// **'your rig sent something Ara didn\'t understand. It may be running a different version.'**
  String get errorUnreadable;

  /// A finished autofocus whose curve fit was weak; r2 is the fit's R² (e.g. 0.55)
  ///
  /// In en, this message translates to:
  /// **'The curve fit was weak (R² {r2}) — passing cloud, a slew during the sweep, or probes that straddle the V unevenly. The focus here comes from the measured frame, not the fit; run again if the sky has settled.'**
  String focusCardWeakFit(String r2);

  /// Weak autofocus fit whose outer probes sit on a plateau, manual step size
  ///
  /// In en, this message translates to:
  /// **'The curve fit was weak (R² {r2}): the outer probes sit on a plateau, so the sweep is wider than the V. A smaller step size would put more probes on the slope. The focus here comes from the measured frame, not the fit.'**
  String focusCardWeakFitPlateau(String r2);

  /// Weak autofocus fit on a plateau, automatic step size
  ///
  /// In en, this message translates to:
  /// **'The curve fit was weak (R² {r2}): the outer probes sit on a plateau, so this sweep was wider than the V. With automatic step size on, Ara sizes the next sweep from the V\'s width where a run lets it be measured, so running again should put more probes on the slope. The focus here comes from the measured frame, not the fit.'**
  String focusCardWeakFitPlateauAuto(String r2);

  /// Simple Focus panel stat: the focuser position autofocus settled on
  ///
  /// In en, this message translates to:
  /// **'Best position'**
  String get focusBestPosition;

  /// Simple Focus panel stat: stars measured in the focused frame
  ///
  /// In en, this message translates to:
  /// **'Stars'**
  String get focusStars;

  /// Simple Focus panel stat: how well the V-curve fit the probes (0-1)
  ///
  /// In en, this message translates to:
  /// **'Fit R²'**
  String get focusFitR2;

  /// Simple Focus panel: the focuser's position, first part of the line under the headline
  ///
  /// In en, this message translates to:
  /// **'Focuser at {position}'**
  String focusFocuserAt(String position);

  /// Simple Focus panel row: show the focus run's frame with its measured stars circled, full screen
  ///
  /// In en, this message translates to:
  /// **'Measured stars'**
  String get focusMeasuredStars;

  /// Simple Focus tool row and its open view's title: the Bahtinov mask readout
  ///
  /// In en, this message translates to:
  /// **'Focus by hand'**
  String get focusToolManual;

  /// Simple Focus tool row button that opens focus by hand
  ///
  /// In en, this message translates to:
  /// **'Open'**
  String get focusToolManualOpen;

  /// Spoken name of the focus by hand button
  ///
  /// In en, this message translates to:
  /// **'Open focus by hand'**
  String get focusToolManualOpenSemantics;

  /// Simple Focus tool row and its open view's title: the filter offsets measurement
  ///
  /// In en, this message translates to:
  /// **'Filter offsets'**
  String get focusToolOffsets;

  /// Simple Focus tool row button that opens the filter offsets measurement
  ///
  /// In en, this message translates to:
  /// **'Measure'**
  String get focusToolOffsetsOpen;

  /// Spoken name of the filter offsets button
  ///
  /// In en, this message translates to:
  /// **'Open filter offsets'**
  String get focusToolOffsetsOpenSemantics;

  /// Simple Focus: heading of the Tools rows under Autofocus
  ///
  /// In en, this message translates to:
  /// **'Tools'**
  String get focusToolsHeading;

  /// Simple Focus tool row status: focus by hand is not open
  ///
  /// In en, this message translates to:
  /// **'Not started'**
  String get focusToolManualStatus;

  /// Simple Focus tool row status: filters whose focus offset is still 0
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{1 to measure} other{{count} to measure}}'**
  String focusToolOffsetsToMeasure(int count);

  /// Simple Focus tool row status: every filter has a focus offset
  ///
  /// In en, this message translates to:
  /// **'All measured'**
  String get focusToolOffsetsAllMeasured;

  /// Simple Focus tool row status: no filter wheel is connected, so there are no offsets to measure. Short, one line.
  ///
  /// In en, this message translates to:
  /// **'No filter wheel'**
  String get focusToolOffsetsNoWheel;

  /// Spoken name of the back arrow in Simple Focus's tool views
  ///
  /// In en, this message translates to:
  /// **'Back to Autofocus'**
  String get focusBackToAutofocus;

  /// Bahtinov focusing finished in focus, mask taken off
  ///
  /// In en, this message translates to:
  /// **'In focus · mask off'**
  String get bahtinovInFocusMaskOff;

  /// Bahtinov focusing finished (not confirmed in focus), mask off
  ///
  /// In en, this message translates to:
  /// **'Mask off'**
  String get bahtinovMaskOff;

  /// Bahtinov readout stopped by a camera error
  ///
  /// In en, this message translates to:
  /// **'Stopped by a camera error'**
  String get bahtinovCameraError;

  /// Bahtinov readout running, no frame yet
  ///
  /// In en, this message translates to:
  /// **'Waiting for the first frame'**
  String get bahtinovWaitingFirst;

  /// Bahtinov headline in focus; px is the spike offset
  ///
  /// In en, this message translates to:
  /// **'In focus · {px} px'**
  String bahtinovInFocusPx(String px);

  /// Bahtinov headline: the turn advice and the offset
  ///
  /// In en, this message translates to:
  /// **'{hint} · {px} px from focus'**
  String bahtinovHintPx(String hint, String px);

  /// Bahtinov readout stopped; the last offset
  ///
  /// In en, this message translates to:
  /// **'Stopped · last {px} px from focus'**
  String bahtinovStoppedLast(String px);

  /// Bahtinov before starting
  ///
  /// In en, this message translates to:
  /// **'Fit the mask, then Start'**
  String get bahtinovFitMask;

  /// Simple Focus Manual, before starting: what to do
  ///
  /// In en, this message translates to:
  /// **'Put the Bahtinov mask over the front of the telescope and point at a bright star. Ara uses the brightest star in the frame.'**
  String get bahtinovFitMaskDetail;

  /// Bahtinov turn advice: no frame yet
  ///
  /// In en, this message translates to:
  /// **'Waiting'**
  String get bahtinovWaiting;

  /// Bahtinov turn advice detail
  ///
  /// In en, this message translates to:
  /// **'The first frame is on its way.'**
  String get bahtinovWaitingDetail;

  /// Bahtinov: no star found
  ///
  /// In en, this message translates to:
  /// **'No star'**
  String get bahtinovNoStar;

  /// Bahtinov: no star found, what to do
  ///
  /// In en, this message translates to:
  /// **'Point at a bright star, or lengthen the exposure.'**
  String get bahtinovNoStarDetail;

  /// Bahtinov: the star is too close to the frame edge
  ///
  /// In en, this message translates to:
  /// **'Star at the edge'**
  String get bahtinovNearEdge;

  /// Bahtinov: star at the edge, what to do
  ///
  /// In en, this message translates to:
  /// **'Center the star so its spikes fit in the frame.'**
  String get bahtinovNearEdgeDetail;

  /// Bahtinov: no diffraction spikes found
  ///
  /// In en, this message translates to:
  /// **'No spikes'**
  String get bahtinovNoSpikes;

  /// Bahtinov: no spikes, what to do
  ///
  /// In en, this message translates to:
  /// **'Fit the mask over the front of the telescope. Far out of focus? Turn until the star shrinks and spikes appear.'**
  String get bahtinovNoSpikesDetail;

  /// Bahtinov turn advice: in focus
  ///
  /// In en, this message translates to:
  /// **'In focus'**
  String get bahtinovInFocus;

  /// Bahtinov in focus with the optics' zone in µm
  ///
  /// In en, this message translates to:
  /// **'The middle spike is centered, inside the ±{um} µm focus zone. Lock the focuser, then Finish.'**
  String bahtinovInFocusZoneDetail(String um);

  /// Bahtinov in focus
  ///
  /// In en, this message translates to:
  /// **'The middle spike is centered. Lock the focuser, then Finish.'**
  String get bahtinovInFocusDetail;

  /// Bahtinov turn advice: no move seen yet
  ///
  /// In en, this message translates to:
  /// **'Make a move'**
  String get bahtinovMakeMove;

  /// Bahtinov: first move
  ///
  /// In en, this message translates to:
  /// **'Turn the focuser a little either way, then watch the number.'**
  String get bahtinovMakeMoveDetail;

  /// Bahtinov turn advice: turn back
  ///
  /// In en, this message translates to:
  /// **'Go back'**
  String get bahtinovGoBack;

  /// Bahtinov: passed focus; size is how far (e.g. 'half', 'a third of')
  ///
  /// In en, this message translates to:
  /// **'You passed focus. Turn back about {size} your last move.'**
  String bahtinovPassedDetail(String size);

  /// Bahtinov turn advice: same way
  ///
  /// In en, this message translates to:
  /// **'Keep going'**
  String get bahtinovKeepGoing;

  /// Bahtinov: keep going; size is how far
  ///
  /// In en, this message translates to:
  /// **'Same way — about {size} your last move again.'**
  String bahtinovKeepGoingDetail(String size);

  /// Bahtinov: turned the wrong way
  ///
  /// In en, this message translates to:
  /// **'Wrong way — turn back past where you started.'**
  String get bahtinovWrongWayDetail;

  /// Move size word placed in 'about {size} your last move'
  ///
  /// In en, this message translates to:
  /// **'a quarter of'**
  String get bahtinovMoveQuarter;

  /// Move size word
  ///
  /// In en, this message translates to:
  /// **'a third of'**
  String get bahtinovMoveThird;

  /// Move size word
  ///
  /// In en, this message translates to:
  /// **'half'**
  String get bahtinovMoveHalf;

  /// Move size word
  ///
  /// In en, this message translates to:
  /// **'three quarters of'**
  String get bahtinovMoveThreeQuarters;

  /// Move size word
  ///
  /// In en, this message translates to:
  /// **'the same as'**
  String get bahtinovMoveSame;

  /// Move size word
  ///
  /// In en, this message translates to:
  /// **'1½×'**
  String get bahtinovMoveOneAndHalf;

  /// Move size word
  ///
  /// In en, this message translates to:
  /// **'twice'**
  String get bahtinovMoveTwice;

  /// Move size word
  ///
  /// In en, this message translates to:
  /// **'several times'**
  String get bahtinovMoveSeveral;

  /// Simple Focus Manual: unit under the big offset number
  ///
  /// In en, this message translates to:
  /// **'px from focus'**
  String get bahtinovPxFromFocus;

  /// Simple Focus Manual: finish focusing (then asks to take the mask off)
  ///
  /// In en, this message translates to:
  /// **'Finish'**
  String get bahtinovFinish;

  /// Simple Focus Manual: the mask-off question, inside the panel
  ///
  /// In en, this message translates to:
  /// **'Take the Bahtinov mask off'**
  String get bahtinovMaskQuestion;

  /// Simple Focus Manual: why the mask must come off
  ///
  /// In en, this message translates to:
  /// **'Take the mask off the front of the telescope before you image. Left on, it puts spikes on every star and costs light.'**
  String get bahtinovMaskQuestionDetail;

  /// Mask-off question: keep the mask on for now
  ///
  /// In en, this message translates to:
  /// **'Not yet'**
  String get bahtinovNotYet;

  /// Mask-off question: the mask is off
  ///
  /// In en, this message translates to:
  /// **'Mask removed'**
  String get bahtinovMaskRemoved;

  /// Simple Focus Manual: Bahtinov frame exposure
  ///
  /// In en, this message translates to:
  /// **'Exposure'**
  String get bahtinovExposure;

  /// Screen reader label of Focus's shutter on Manual
  ///
  /// In en, this message translates to:
  /// **'Start the Bahtinov readout'**
  String get focusStartBahtinov;

  /// Screen reader label of Focus's shutter on Manual while it reads
  ///
  /// In en, this message translates to:
  /// **'Stop the Bahtinov readout'**
  String get focusStopBahtinov;

  /// Simple Focus panel: a finished autofocus whose curve fit was weak, short form
  ///
  /// In en, this message translates to:
  /// **'Weak curve fit (R² {r2}): run again if the sky has settled.'**
  String focusWeakFitShort(String r2);

  /// Guide camera live focus: starting
  ///
  /// In en, this message translates to:
  /// **'Starting…'**
  String get guideFocusStarting;

  /// Guide camera live focus headline: no stars
  ///
  /// In en, this message translates to:
  /// **'Live · no stars measured'**
  String get guideFocusNoStarsMeasured;

  /// Guide camera live focus headline
  ///
  /// In en, this message translates to:
  /// **'Live · HFR {hfr}'**
  String guideFocusLive(String hfr);

  /// Guide camera live focus headline, in focus
  ///
  /// In en, this message translates to:
  /// **'Live · HFR {hfr} — in focus'**
  String guideFocusLiveInFocus(String hfr);

  /// Guide camera live focus headline, the best so far
  ///
  /// In en, this message translates to:
  /// **'Live · HFR {hfr} — best so far'**
  String guideFocusLiveBest(String hfr);

  /// Guide camera live focus stopped on an error
  ///
  /// In en, this message translates to:
  /// **'Stopped on an error'**
  String get guideFocusStoppedError;

  /// Guide focus stopped on its own once held in focus
  ///
  /// In en, this message translates to:
  /// **'In focus — held, stopped'**
  String get guideFocusHeld;

  /// Same, with the best HFR
  ///
  /// In en, this message translates to:
  /// **'In focus — held, stopped · best HFR {hfr}'**
  String guideFocusHeldBest(String hfr);

  /// Guide focus stopped, in focus
  ///
  /// In en, this message translates to:
  /// **'In focus · best HFR {hfr}'**
  String guideFocusInFocusBest(String hfr);

  /// Guide focus stopped
  ///
  /// In en, this message translates to:
  /// **'Stopped'**
  String get guideFocusStopped;

  /// Guide focus stopped with the best HFR
  ///
  /// In en, this message translates to:
  /// **'Stopped · best HFR {hfr}'**
  String guideFocusStoppedBest(String hfr);

  /// Off-axis guider: the main telescope must be focused first
  ///
  /// In en, this message translates to:
  /// **'Waiting for the main telescope'**
  String get guideFocusWaitingMain;

  /// Guide focus can't start yet
  ///
  /// In en, this message translates to:
  /// **'Not ready'**
  String get guideFocusNotReady;

  /// Guide focus can start
  ///
  /// In en, this message translates to:
  /// **'Ready to focus'**
  String get guideFocusReady;

  /// Guide focus blocker
  ///
  /// In en, this message translates to:
  /// **'Connect the guider first — the frames come through it.'**
  String get guideFocusConnectGuider;

  /// Guide focus blocker
  ///
  /// In en, this message translates to:
  /// **'Stop the guider first; it owns its camera while it is looping, guiding, calibrating or paused.'**
  String get guideFocusStopGuider;

  /// Guide focus advice: no stars
  ///
  /// In en, this message translates to:
  /// **'No stars'**
  String get guideFocusNoStars;

  /// Guide focus advice detail: no stars
  ///
  /// In en, this message translates to:
  /// **'Point at a star field or lengthen the exposure. Far out of focus? Make big moves until stars appear.'**
  String get guideFocusNoStarsDetail;

  /// Move size inside the guide focus advice
  ///
  /// In en, this message translates to:
  /// **'big moves'**
  String get guideFocusBigMoves;

  /// Move size inside the guide focus advice
  ///
  /// In en, this message translates to:
  /// **'small moves'**
  String get guideFocusSmallMoves;

  /// Guide focus in focus
  ///
  /// In en, this message translates to:
  /// **'Stars are as tight as this camera resolves (in focus ≤ {target} px). Lock the focuser.'**
  String guideFocusInFocusDetail(String target);

  /// Guide focus advice: at the best HFR seen
  ///
  /// In en, this message translates to:
  /// **'Sharpest so far'**
  String get guideFocusSharpest;

  /// Guide focus advice detail
  ///
  /// In en, this message translates to:
  /// **'Hold here, or nudge a touch either way to confirm.'**
  String get guideFocusSharpestDetail;

  /// Guide focus first move; size is 'small moves'/'big moves'
  ///
  /// In en, this message translates to:
  /// **'In or out, {size} — then watch which way the number goes.'**
  String guideFocusMakeMoveDetail(String size);

  /// Guide focus keep going
  ///
  /// In en, this message translates to:
  /// **'Same way you just moved — {size}.'**
  String guideFocusKeepGoingDetail(String size);

  /// Guide focus go back
  ///
  /// In en, this message translates to:
  /// **'The other way from your last move — {size}.'**
  String guideFocusGoBackDetail(String size);

  /// Guide focus advice: no change seen
  ///
  /// In en, this message translates to:
  /// **'Holding'**
  String get guideFocusHolding;

  /// Guide focus holding, far
  ///
  /// In en, this message translates to:
  /// **'Still well off focus — make a bigger move and watch the number.'**
  String get guideFocusHoldingFar;

  /// Guide focus holding, near
  ///
  /// In en, this message translates to:
  /// **'Try a small move either way.'**
  String get guideFocusHoldingNear;

  /// Focus mode rail chip: focusing the guide camera
  ///
  /// In en, this message translates to:
  /// **'Guide'**
  String get focusGuide;

  /// Focus panel, guide camera: what to turn (guide scope)
  ///
  /// In en, this message translates to:
  /// **'Guide scope: turn its focuser, or slide the camera in the draw tube, in small moves and watch the number fall.'**
  String get focusGuideScope;

  /// Focus panel, guide camera: what to turn (off-axis guider)
  ///
  /// In en, this message translates to:
  /// **'Off-axis guider: it shares the main scope\'s focus. Turn the OAG\'s helical focuser in small moves and watch the number fall.'**
  String get focusGuideOag;

  /// Focus panel, guide camera, off-axis guider gate
  ///
  /// In en, this message translates to:
  /// **'Focus the main telescope first: an off-axis guider sees the same focal plane, so its HFR only means something once the main scope is in focus.'**
  String get focusGuideMainFirst;

  /// Focus panel: the main telescope was focused by hand
  ///
  /// In en, this message translates to:
  /// **'Already in focus'**
  String get focusAlreadyInFocus;

  /// Focus panel, guide camera: frames taken
  ///
  /// In en, this message translates to:
  /// **'Frames'**
  String get focusFrames;

  /// Screen reader label of Focus's shutter, guide camera
  ///
  /// In en, this message translates to:
  /// **'Start the guide camera\'s live focus'**
  String get focusStartGuide;

  /// Screen reader label of Focus's shutter, guide camera, while it reads
  ///
  /// In en, this message translates to:
  /// **'Stop the guide camera\'s live focus'**
  String get focusStopGuide;

  /// Screen reader label of Preview's shutter on the guide camera: starts the guider's plain looping frames
  ///
  /// In en, this message translates to:
  /// **'Loop the guide camera\'s frames'**
  String get previewGuideLoopStart;

  /// Screen reader label of Preview's shutter on the guide camera while its frames loop
  ///
  /// In en, this message translates to:
  /// **'Stop the guide camera\'s frames'**
  String get previewGuideLoopStop;

  /// Focus panel: focus the main telescope (short, shares a line with Guide)
  ///
  /// In en, this message translates to:
  /// **'Main'**
  String get focusScopeMain;

  /// Focus panel: focus the guide camera (short)
  ///
  /// In en, this message translates to:
  /// **'Guide'**
  String get focusScopeGuide;

  /// Preflight plan step: green button, on to focusing
  ///
  /// In en, this message translates to:
  /// **'Next: Focus'**
  String get preflightNextFocus;

  /// Preflight plan step: green button, on to polar alignment
  ///
  /// In en, this message translates to:
  /// **'Next: Polar align'**
  String get preflightNextPolarAlign;

  /// Preflight plan step: green button when focus and polar alignment are done already
  ///
  /// In en, this message translates to:
  /// **'Start imaging'**
  String get preflightStartImaging;

  /// Preflight plan step: line above the buttons
  ///
  /// In en, this message translates to:
  /// **'Imaging starts after focus and polar alignment.'**
  String get preflightImagingStartsAfter;

  /// Preflight: the last setup step's green Done, which starts imaging
  ///
  /// In en, this message translates to:
  /// **'Done · Start imaging'**
  String get preflightDoneStartImaging;

  /// Settings › Rig: the guide scope's focal length (mm), read from the guider when unset
  ///
  /// In en, this message translates to:
  /// **'Guide scope focal length'**
  String get shellGuideFocalLength;

  /// Settings › Rig: the guide camera's pixel size (µm), read from AlpacaBridge through the guider
  ///
  /// In en, this message translates to:
  /// **'Guide camera pixel size'**
  String get shellGuidePixelSize;

  /// Simple search: other words that find the guide scope rows (not shown)
  ///
  /// In en, this message translates to:
  /// **'guide scope guider camera pixel focal polar'**
  String get searchWordsGuideScope;

  /// Guider mode: the guider's settings and guiding with the guide camera, started by the shutter
  ///
  /// In en, this message translates to:
  /// **'Guider'**
  String get shellModeGuide;

  /// Preflight button moving on to the Guide step
  ///
  /// In en, this message translates to:
  /// **'Next: Guide'**
  String get preflightNextGuide;

  /// Shutter label in Guide mode: starts the guider's routine (calibrating when needed)
  ///
  /// In en, this message translates to:
  /// **'Start guiding'**
  String get guideStart;

  /// Shutter label in Guide mode while the guider runs
  ///
  /// In en, this message translates to:
  /// **'Stop guiding'**
  String get guideStop;

  /// Action named in an error: the guider refused to start
  ///
  /// In en, this message translates to:
  /// **'start guiding'**
  String get guideActionStart;

  /// Action named in an error: the guider refused to stop
  ///
  /// In en, this message translates to:
  /// **'stop guiding'**
  String get guideActionStop;

  /// Guide panel hint while idle
  ///
  /// In en, this message translates to:
  /// **'Tap the shutter to start guiding. It calibrates first when needed.'**
  String get guideTapShutter;

  /// Guide panel hint with no guider: the Camera (Guide) row connects it
  ///
  /// In en, this message translates to:
  /// **'Connect Camera (Guide) in Settings › Devices.'**
  String get guideConnectFirst;

  /// The guide camera's device window title
  ///
  /// In en, this message translates to:
  /// **'Camera (Guide)'**
  String get deviceGuiderCamera;

  /// Top bar note when no device is connected; opens Settings › Devices
  ///
  /// In en, this message translates to:
  /// **'No devices connected'**
  String get shellNoDevices;

  /// Guide panel, pill and shutter while a start is on its way to the guider
  ///
  /// In en, this message translates to:
  /// **'Starting…'**
  String get guideStarting;

  /// While a stop is on its way (the guider finishes its frame first)
  ///
  /// In en, this message translates to:
  /// **'Stopping…'**
  String get guideStopping;

  /// Simple Mode pill: a guider Stop the rig accepted but did not act on; {state} is the guider phase.
  ///
  /// In en, this message translates to:
  /// **'The guider didn\'t stop: it is still {state}. Try Stop again.'**
  String guideStopRefused(String state);

  /// Simple Mode pill: the guider accepted a Start and then gave up; {reason} is the guider's own words, e.g. Stopped capturing (#1664).
  ///
  /// In en, this message translates to:
  /// **'Guiding didn\'t start: {reason}.'**
  String guideStartFailed(String reason);

  /// Simple Mode pill: the guider gave up on a Start without saying why (#1664).
  ///
  /// In en, this message translates to:
  /// **'Guiding didn\'t start. See the Guider log for why.'**
  String get guideStartFailedNoReason;

  /// No description provided for @guideAggressiveness.
  ///
  /// In en, this message translates to:
  /// **'Aggressiveness'**
  String get guideAggressiveness;

  /// Right ascension, the short axis name beside its slider
  ///
  /// In en, this message translates to:
  /// **'RA'**
  String get guideAxisRa;

  /// Declination, the short axis name beside its slider
  ///
  /// In en, this message translates to:
  /// **'Dec'**
  String get guideAxisDec;

  /// No description provided for @guideRaAggressiveness.
  ///
  /// In en, this message translates to:
  /// **'RA aggressiveness'**
  String get guideRaAggressiveness;

  /// No description provided for @guideDecAggressiveness.
  ///
  /// In en, this message translates to:
  /// **'Dec aggressiveness'**
  String get guideDecAggressiveness;

  /// No description provided for @guideTabGuide.
  ///
  /// In en, this message translates to:
  /// **'Guide'**
  String get guideTabGuide;

  /// No description provided for @guideTabTuning.
  ///
  /// In en, this message translates to:
  /// **'Tuning'**
  String get guideTabTuning;

  /// No description provided for @guideTabTools.
  ///
  /// In en, this message translates to:
  /// **'Tools'**
  String get guideTabTools;

  /// No description provided for @guideMinimumMove.
  ///
  /// In en, this message translates to:
  /// **'Minimum move · goes to the Guider now'**
  String get guideMinimumMove;

  /// No description provided for @guideSettle.
  ///
  /// In en, this message translates to:
  /// **'Settle after each dither'**
  String get guideSettle;

  /// No description provided for @guideSettleWithin.
  ///
  /// In en, this message translates to:
  /// **'Within'**
  String get guideSettleWithin;

  /// No description provided for @guideSettleFor.
  ///
  /// In en, this message translates to:
  /// **'For'**
  String get guideSettleFor;

  /// No description provided for @guideSettleGiveUp.
  ///
  /// In en, this message translates to:
  /// **'Give up after'**
  String get guideSettleGiveUp;

  /// No description provided for @guideRecalibrateNow.
  ///
  /// In en, this message translates to:
  /// **'Recalibrate now'**
  String get guideRecalibrateNow;

  /// No description provided for @guideRunAssistant.
  ///
  /// In en, this message translates to:
  /// **'Run Guiding Assistant'**
  String get guideRunAssistant;

  /// No description provided for @guideStopAssistant.
  ///
  /// In en, this message translates to:
  /// **'Stop Guiding Assistant'**
  String get guideStopAssistant;

  /// No description provided for @guideAssistantApply.
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{Apply 1 suggestion} other{Apply {count} suggestions}}'**
  String guideAssistantApply(int count);

  /// No description provided for @guideStopPlanFirst.
  ///
  /// In en, this message translates to:
  /// **'Stop the plan to use these.'**
  String get guideStopPlanFirst;

  /// No description provided for @guideStopGuidingFirst.
  ///
  /// In en, this message translates to:
  /// **'Stop guiding to use these.'**
  String get guideStopGuidingFirst;

  /// No description provided for @guideTabsGroup.
  ///
  /// In en, this message translates to:
  /// **'Guider page'**
  String get guideTabsGroup;

  /// No description provided for @guideMinimumMoveName.
  ///
  /// In en, this message translates to:
  /// **'Minimum move'**
  String get guideMinimumMoveName;

  /// No description provided for @guideStopLoopFirst.
  ///
  /// In en, this message translates to:
  /// **'Stop the guide loop to recalibrate.'**
  String get guideStopLoopFirst;

  /// No description provided for @guideAssistantCalibrateFirst.
  ///
  /// In en, this message translates to:
  /// **'Calibrate first: press Recalibrate, then run the Guiding Assistant.'**
  String get guideAssistantCalibrateFirst;

  /// No description provided for @guideAssistantNoStar.
  ///
  /// In en, this message translates to:
  /// **'The guider found no star to measure. Check the guide camera, then try again.'**
  String get guideAssistantNoStar;

  /// No description provided for @focusNudgePlanRunning.
  ///
  /// In en, this message translates to:
  /// **'Stop the plan to move the focuser.'**
  String get focusNudgePlanRunning;

  /// No description provided for @focusNudgeAutofocusRunning.
  ///
  /// In en, this message translates to:
  /// **'Autofocus has the focuser. Wait for it to finish, or cancel it.'**
  String get focusNudgeAutofocusRunning;

  /// No description provided for @guideAssistantFailedNoReason.
  ///
  /// In en, this message translates to:
  /// **'the guider gave no reason'**
  String get guideAssistantFailedNoReason;

  /// No description provided for @guideToolRecalibrate.
  ///
  /// In en, this message translates to:
  /// **'Recalibrate'**
  String get guideToolRecalibrate;

  /// No description provided for @guideToolCalibration.
  ///
  /// In en, this message translates to:
  /// **'Calibration'**
  String get guideToolCalibration;

  /// No description provided for @guideToolCalibrationWhat.
  ///
  /// In en, this message translates to:
  /// **'Measures again how the mount moves the guide star. Redo it after turning the camera or moving far in Dec.'**
  String get guideToolCalibrationWhat;

  /// No description provided for @guideToolCalibratedAt.
  ///
  /// In en, this message translates to:
  /// **'Calibrated at Dec {dec}'**
  String guideToolCalibratedAt(String dec);

  /// No description provided for @guideToolCalibrated.
  ///
  /// In en, this message translates to:
  /// **'Calibrated'**
  String get guideToolCalibrated;

  /// No description provided for @guideToolNotCalibrated.
  ///
  /// In en, this message translates to:
  /// **'Not calibrated. The next start calibrates first.'**
  String get guideToolNotCalibrated;

  /// No description provided for @guideToolAssistant.
  ///
  /// In en, this message translates to:
  /// **'Guiding Assistant'**
  String get guideToolAssistant;

  /// No description provided for @guideToolAssistantWhat.
  ///
  /// In en, this message translates to:
  /// **'Watches the star drift with guiding off, then suggests settings for this mount and sky.'**
  String get guideToolAssistantWhat;

  /// No description provided for @guideToolRun.
  ///
  /// In en, this message translates to:
  /// **'Run'**
  String get guideToolRun;

  /// No description provided for @guideToolRunAgain.
  ///
  /// In en, this message translates to:
  /// **'Run again'**
  String get guideToolRunAgain;

  /// No description provided for @guideToolStop.
  ///
  /// In en, this message translates to:
  /// **'Stop'**
  String get guideToolStop;

  /// No description provided for @guideToolNotRun.
  ///
  /// In en, this message translates to:
  /// **'Not run yet'**
  String get guideToolNotRun;

  /// No description provided for @guideToolMeasuring.
  ///
  /// In en, this message translates to:
  /// **'Measuring drift…'**
  String get guideToolMeasuring;

  /// Simple Mode Guider panel, the calibration plot before the guider reports its first step
  ///
  /// In en, this message translates to:
  /// **'Waiting for the first step…'**
  String get guideCalibrationWaiting;

  /// Screen reader label of the Simple Mode Guider panel's calibration plot
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{Calibration plot, 1 step} other{Calibration plot, {count} steps}}'**
  String guideCalibrationPlot(int count);

  /// Colour key of the calibration plot: the RA legs, West and East
  ///
  /// In en, this message translates to:
  /// **'RA (W/E)'**
  String get guideCalibrationKeyRa;

  /// Colour key of the calibration plot: the Dec legs, North and South
  ///
  /// In en, this message translates to:
  /// **'Dec (N/S)'**
  String get guideCalibrationKeyDec;

  /// Simple Mode status pill, the Guiding Assistant measuring; time is a countdown such as 01:30
  ///
  /// In en, this message translates to:
  /// **'{time} left'**
  String guideAssistantLeft(String time);

  /// No description provided for @guideToolDone.
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =0{Done. Nothing to apply.} =1{Done. 1 suggestion to apply.} other{Done. {count} suggestions to apply.}}'**
  String guideToolDone(int count);

  /// No description provided for @guideToolFailed.
  ///
  /// In en, this message translates to:
  /// **'Last run failed: {reason}'**
  String guideToolFailed(String reason);

  /// No description provided for @guideToolCalibrateFirst.
  ///
  /// In en, this message translates to:
  /// **'Calibrate first: it needs a calibration to measure against.'**
  String get guideToolCalibrateFirst;

  /// No description provided for @guideSuggestExposure.
  ///
  /// In en, this message translates to:
  /// **'Guide exposure'**
  String get guideSuggestExposure;

  /// No description provided for @guideSuggestMinMoveRa.
  ///
  /// In en, this message translates to:
  /// **'RA minimum move'**
  String get guideSuggestMinMoveRa;

  /// No description provided for @guideSuggestMinMoveDec.
  ///
  /// In en, this message translates to:
  /// **'Dec minimum move'**
  String get guideSuggestMinMoveDec;

  /// No description provided for @guideSuggestDecBacklash.
  ///
  /// In en, this message translates to:
  /// **'Dec backlash compensation'**
  String get guideSuggestDecBacklash;

  /// No description provided for @guideSuggestDecAlgorithm.
  ///
  /// In en, this message translates to:
  /// **'Dec algorithm'**
  String get guideSuggestDecAlgorithm;

  /// No description provided for @focusNudgeIn.
  ///
  /// In en, this message translates to:
  /// **'Nudge in'**
  String get focusNudgeIn;

  /// No description provided for @focusNudgeOut.
  ///
  /// In en, this message translates to:
  /// **'Nudge out'**
  String get focusNudgeOut;

  /// No description provided for @focusNudgeStop.
  ///
  /// In en, this message translates to:
  /// **'Stop'**
  String get focusNudgeStop;

  /// No description provided for @focusNudgeSteps.
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{1 step} other{{count} steps}}'**
  String focusNudgeSteps(int count);

  /// No description provided for @focusMoveFailed.
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t move the focuser'**
  String get focusMoveFailed;

  /// No description provided for @focusStopFailed.
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t stop the focuser'**
  String get focusStopFailed;

  /// No description provided for @focusCalibrationSection.
  ///
  /// In en, this message translates to:
  /// **'Calibration'**
  String get focusCalibrationSection;

  /// No description provided for @focusCalibrationReset.
  ///
  /// In en, this message translates to:
  /// **'Reset focus calibration'**
  String get focusCalibrationReset;

  /// No description provided for @focusCalibrationResetHint.
  ///
  /// In en, this message translates to:
  /// **'The next autofocus runs a full sweep and calibrates again.'**
  String get focusCalibrationResetHint;

  /// No description provided for @focusCalibrationResetAsk.
  ///
  /// In en, this message translates to:
  /// **'Reset the focus calibration?'**
  String get focusCalibrationResetAsk;

  /// No description provided for @focusCalibrationResetCancel.
  ///
  /// In en, this message translates to:
  /// **'Keep it'**
  String get focusCalibrationResetCancel;

  /// No description provided for @focusCalibrationResetConfirm.
  ///
  /// In en, this message translates to:
  /// **'Reset'**
  String get focusCalibrationResetConfirm;

  /// No description provided for @focusCalibrationResetDone.
  ///
  /// In en, this message translates to:
  /// **'Focus calibration reset.'**
  String get focusCalibrationResetDone;

  /// No description provided for @focusCalibrationResetAction.
  ///
  /// In en, this message translates to:
  /// **'reset the focus calibration'**
  String get focusCalibrationResetAction;

  /// No description provided for @guideDecMode.
  ///
  /// In en, this message translates to:
  /// **'Dec guide mode'**
  String get guideDecMode;

  /// No description provided for @guideDecAuto.
  ///
  /// In en, this message translates to:
  /// **'Auto'**
  String get guideDecAuto;

  /// No description provided for @guideDecNorth.
  ///
  /// In en, this message translates to:
  /// **'North'**
  String get guideDecNorth;

  /// No description provided for @guideDecSouth.
  ///
  /// In en, this message translates to:
  /// **'South'**
  String get guideDecSouth;

  /// Guide exposure picker value when the profile leaves it to the guider
  ///
  /// In en, this message translates to:
  /// **'The guider\'s own'**
  String get guideExposureOwn;

  /// Why Focus's shutter is greyed out on Auto with no focuser
  ///
  /// In en, this message translates to:
  /// **'Connect a focuser to autofocus, or focus by hand on Manual.'**
  String get focusNeedsFocuser;

  /// Why a greyed shutter can't start autofocus, the Bahtinov readout or polar alignment
  ///
  /// In en, this message translates to:
  /// **'The main camera is busy: wait for its frame, or stop it.'**
  String get shellMainCameraBusy;

  /// Sky Album screen: app bar title
  ///
  /// In en, this message translates to:
  /// **'Sky Album'**
  String get albumTitle;

  /// Sky Album: Book switch segment for the Messier catalog
  ///
  /// In en, this message translates to:
  /// **'Messier'**
  String get albumBookMessier;

  /// Sky Album: Book switch segment for the Caldwell catalog
  ///
  /// In en, this message translates to:
  /// **'Caldwell'**
  String get albumBookCaldwell;

  /// Sky Album: Book switch segment for the RCW catalog of southern emission nebulae (Rodgers, Campbell and Whiteoak); the abbreviation is the name in every language
  ///
  /// In en, this message translates to:
  /// **'RCW'**
  String get albumBookRcw;

  /// Sky Album: Book switch segment for the NGC catalog (New General Catalogue); the name is the same in every language
  ///
  /// In en, this message translates to:
  /// **'NGC'**
  String get albumBookNgc;

  /// Sky Album: Book switch segment for the IC catalog (Index Catalogue); the name is the same in every language
  ///
  /// In en, this message translates to:
  /// **'IC'**
  String get albumBookIc;

  /// Sky Album: Book switch segment for the Sharpless Sh2 catalog of HII regions; the name is the same in every language
  ///
  /// In en, this message translates to:
  /// **'Sharpless (Sh2)'**
  String get albumBookSharpless;

  /// Deep-sky object type on a Sky Album card back's bio line
  ///
  /// In en, this message translates to:
  /// **'Galaxy'**
  String get dsoTypeGalaxy;

  /// Deep-sky object type on a Sky Album card back's bio line
  ///
  /// In en, this message translates to:
  /// **'Galaxy pair'**
  String get dsoTypeGalaxyPair;

  /// Deep-sky object type on a Sky Album card back's bio line
  ///
  /// In en, this message translates to:
  /// **'Galaxy triplet'**
  String get dsoTypeGalaxyTriplet;

  /// Deep-sky object type on a Sky Album card back's bio line
  ///
  /// In en, this message translates to:
  /// **'Galaxy group'**
  String get dsoTypeGalaxyGroup;

  /// Deep-sky object type on a Sky Album card back's bio line
  ///
  /// In en, this message translates to:
  /// **'Nebula'**
  String get dsoTypeNebula;

  /// Deep-sky object type on a Sky Album card back's bio line
  ///
  /// In en, this message translates to:
  /// **'Emission nebula'**
  String get dsoTypeEmissionNebula;

  /// Deep-sky object type on a Sky Album card back's bio line
  ///
  /// In en, this message translates to:
  /// **'Reflection nebula'**
  String get dsoTypeReflectionNebula;

  /// Deep-sky object type on a Sky Album card back's bio line
  ///
  /// In en, this message translates to:
  /// **'Planetary nebula'**
  String get dsoTypePlanetaryNebula;

  /// Deep-sky object type on a Sky Album card back's bio line
  ///
  /// In en, this message translates to:
  /// **'Supernova remnant'**
  String get dsoTypeSupernovaRemnant;

  /// Deep-sky object type on a Sky Album card back's bio line
  ///
  /// In en, this message translates to:
  /// **'Cluster'**
  String get dsoTypeCluster;

  /// Deep-sky object type on a Sky Album card back's bio line
  ///
  /// In en, this message translates to:
  /// **'Open cluster'**
  String get dsoTypeOpenCluster;

  /// Deep-sky object type on a Sky Album card back's bio line
  ///
  /// In en, this message translates to:
  /// **'Globular cluster'**
  String get dsoTypeGlobularCluster;

  /// Deep-sky object type on a Sky Album card back's bio line
  ///
  /// In en, this message translates to:
  /// **'Cluster + nebula'**
  String get dsoTypeClusterNebula;

  /// Deep-sky object type on a Sky Album card back's bio line
  ///
  /// In en, this message translates to:
  /// **'Object'**
  String get dsoTypeObject;

  /// Sky Album: label before the catalog filter chips (none selected shows every catalog)
  ///
  /// In en, this message translates to:
  /// **'Catalogs'**
  String get albumCatalogs;

  /// Sky Album: a catalog filter chip, the catalog's name and how many of its cards are collected, so a card in an unselected catalog still shows
  ///
  /// In en, this message translates to:
  /// **'{book} · {collected}'**
  String albumBookCount(String book, int collected);

  /// Sky Album header: cards collected (bronze or better) out of the Book's cards
  ///
  /// In en, this message translates to:
  /// **'{collected} / {total} collected'**
  String albumCollected(int collected, int total);

  /// Sky Album: bronze tier name (at least 15 min of lights)
  ///
  /// In en, this message translates to:
  /// **'Bronze'**
  String get albumTierBronze;

  /// Sky Album: silver tier name (1 h or more)
  ///
  /// In en, this message translates to:
  /// **'Silver'**
  String get albumTierSilver;

  /// Sky Album: gold tier name (5 h or more)
  ///
  /// In en, this message translates to:
  /// **'Gold'**
  String get albumTierGold;

  /// Sky Album header: per-tier count, e.g. 'Gold 3'
  ///
  /// In en, this message translates to:
  /// **'{tier} {count}'**
  String albumTierCount(String tier, int count);

  /// Sky Album: caption over the all-grey grid when no lights exist yet
  ///
  /// In en, this message translates to:
  /// **'Image a target to collect your first card'**
  String get albumEmpty;

  /// Sky Album: title of the shelf of missing cards Tonight's Sky ranks well tonight
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{1 missing card well placed tonight} other{{count} missing cards well placed tonight}}'**
  String albumShelfTitle(int count);

  /// Sky Album card back: shown instead of figures on a missing card
  ///
  /// In en, this message translates to:
  /// **'Not collected yet'**
  String get albumNotCollected;

  /// Sky Album card back: the card's catalog number over the Book's size, as on a trading card
  ///
  /// In en, this message translates to:
  /// **'{number} / {total}'**
  String albumSetNumber(int number, int total);

  /// Sky Album card back: stats table column, the observing night (evening date)
  ///
  /// In en, this message translates to:
  /// **'Night'**
  String get albumNight;

  /// Sky Album card back: stats table column, light frames that night
  ///
  /// In en, this message translates to:
  /// **'Lights'**
  String get albumLights;

  /// Sky Album card back: stats table column, the most common exposure length that night
  ///
  /// In en, this message translates to:
  /// **'Sub'**
  String get albumSub;

  /// Sky Album card back: stats table column, hours of exposure
  ///
  /// In en, this message translates to:
  /// **'Hours'**
  String get albumHours;

  /// Sky Album card back: the totals row's label with the full number of nights
  ///
  /// In en, this message translates to:
  /// **'Total · {nights, plural, =1{1 night} other{{nights} nights}}'**
  String albumNightsTotal(int nights);

  /// Sky Album card back: heading of the one-line fact about the object
  ///
  /// In en, this message translates to:
  /// **'Did you know?'**
  String get albumDidYouKnow;

  /// Sky Album card back: who made the card's picture; the licence name follows as a link. (cropped) marks it as changed, as CC BY asks
  ///
  /// In en, this message translates to:
  /// **'Art (cropped): {credit}'**
  String albumArtCredit(String credit);

  /// Sky Album card back: snackbar when the Art credit's licence link cannot be opened in a browser
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t open the license page'**
  String get albumArtCreditOpenFailed;

  /// Simple Mode left rail: opens the Sky Album
  ///
  /// In en, this message translates to:
  /// **'Sky Album'**
  String get shellAlbum;

  /// Sky Album card back: action on a card with no lights; hands the object to the planner
  ///
  /// In en, this message translates to:
  /// **'Image this target'**
  String get albumImageThis;

  /// Sky Album card back: action on a card with lights, collected or not; hands the object to the planner
  ///
  /// In en, this message translates to:
  /// **'Add more time'**
  String get albumAddTime;

  /// Sky Album: error pill when the album rows fail to load
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t load the album: {reason}'**
  String albumLoadFailed(String reason);

  /// Sky Album: pill when no rig is saved, so no frames can be read
  ///
  /// In en, this message translates to:
  /// **'Connect to a rig to fill the album'**
  String get albumNoServer;

  /// Sky Album: screen-reader label of a card: catalog label, object name and its tier or 'Not collected yet'
  ///
  /// In en, this message translates to:
  /// **'{label} {name}, {state}'**
  String albumCardSemantics(String label, String name, String state);

  /// Sky Album: filter segment showing every card of the Book
  ///
  /// In en, this message translates to:
  /// **'All'**
  String get albumFilterAll;

  /// Sky Album: filter segment showing only collected cards (bronze or better)
  ///
  /// In en, this message translates to:
  /// **'Collected'**
  String get albumFilterCollected;

  /// Sky Album: filter segment showing only cards not collected yet
  ///
  /// In en, this message translates to:
  /// **'Missing'**
  String get albumFilterMissing;

  /// Sky Album header: cards collected since the album was last opened
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{1 new card} other{{count} new cards}}'**
  String albumNewCards(int count);

  /// Sky Album header: cards that moved up a tier since the album was last opened
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{1 tier up} other{{count} tiers up}}'**
  String albumTiersUp(int count);

  /// Sky Album: said by screen readers after a new-shelf card's tier, for a card collected since the last open
  ///
  /// In en, this message translates to:
  /// **'new'**
  String get albumCardNew;

  /// Sky Album: said by screen readers after a new-shelf card's tier, for a card that moved up a tier since the last open
  ///
  /// In en, this message translates to:
  /// **'up a tier'**
  String get albumCardTierUp;

  /// Sky Album header after a sync brought cards; {changes} is albumNewCards and/or albumTiersUp joined by ' · '
  ///
  /// In en, this message translates to:
  /// **'{changes} since your last visit'**
  String albumSinceLastVisit(String changes);

  /// Setup wizard, Guiding step: shown under the Guide Camera picker (and holds Next) when it resolves to the same Alpaca device as the Main Camera
  ///
  /// In en, this message translates to:
  /// **'The Main Camera can\'t also be the Guide Camera. Guiding needs a separate Guide Camera, because two programs on one sensor break guiding. Pick another camera, or choose None.'**
  String get wizardGuideCameraIsMainCamera;

  /// Setup wizard finish note: the guider profile push failed, so the dark-library build was not started
  ///
  /// In en, this message translates to:
  /// **'Dark-library build skipped: the guider didn\'t take the new profile. Build it from the Guider panel once the profile push succeeds.'**
  String get wizardDarksSkippedPushFailed;

  /// Simple Mode Settings › Rig › Storage block: label above the disk's name
  ///
  /// In en, this message translates to:
  /// **'Frames are saved to'**
  String get shellStorageSavedTo;

  /// Simple Mode Settings › Rig › Storage block: name when no external disk is the store
  ///
  /// In en, this message translates to:
  /// **'Internal storage'**
  String get shellStorageInternal;

  /// Simple Mode Settings › Rig › Storage block: free and total space
  ///
  /// In en, this message translates to:
  /// **'{free} free of {total}'**
  String shellStorageFreeOf(String free, String total);

  /// Simple Mode Settings › Rig › Storage block: red line, no space figures
  ///
  /// In en, this message translates to:
  /// **'No disk is plugged in. Frames can\'t be saved.'**
  String get shellStorageNotPlugged;

  /// Simple Mode Settings › Rig › Storage block: red line, the read failed
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t read the rig\'s storage. Frames can\'t be saved.'**
  String get shellStorageUnreadable;

  /// Simple Mode Settings › Rig › Storage block: red line, no free space
  ///
  /// In en, this message translates to:
  /// **'The disk is full. Frames can\'t be saved.'**
  String get shellStorageFull;

  /// Simple Mode Settings › Rig › Storage block: button, pick the disk frames are saved to
  ///
  /// In en, this message translates to:
  /// **'Choose disk'**
  String get shellStorageChoose;

  /// Simple Mode Settings › Rig › Storage block: button, safely remove the disk
  ///
  /// In en, this message translates to:
  /// **'Eject'**
  String get shellStorageEject;

  /// Simple Mode Settings › Rig › Storage block: button, check the disk's filesystem
  ///
  /// In en, this message translates to:
  /// **'Check disk'**
  String get shellStorageCheck;

  /// Simple Mode search panel: a setting's name as a result; should match its name in Settings
  ///
  /// In en, this message translates to:
  /// **'Storage'**
  String get searchSettingStorage;

  /// Simple Mode search panel: other words for Storage
  ///
  /// In en, this message translates to:
  /// **'disk drive frames save space eject usb'**
  String get searchWordsStorage;

  /// Camera window: the heading of the cooling fan choice (#1700)
  ///
  /// In en, this message translates to:
  /// **'Cooling fan'**
  String get coolingFan;

  /// Cooling fan choice: no fan is chosen
  ///
  /// In en, this message translates to:
  /// **'None'**
  String get coolingFanNone;

  /// Cooling fan choice: the chosen switch isn't connected right now
  ///
  /// In en, this message translates to:
  /// **'Chosen fan (not connected)'**
  String get coolingFanNotConnected;

  /// Cooling fan caption when a fan is chosen
  ///
  /// In en, this message translates to:
  /// **'This fan starts with the cooler and cannot be turned off while the camera is cooling.'**
  String get coolingFanChosenNote;

  /// Cooling fan caption when no fan is chosen
  ///
  /// In en, this message translates to:
  /// **'No fan is chosen, so Ara does not run a fan with the cooler.'**
  String get coolingFanNoneNote;

  /// Error when the daemon didn't store the cooling fan choice
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t save the cooling fan'**
  String get coolingFanCouldntSave;

  /// Simple Mode pill asking once for the cooling fan
  ///
  /// In en, this message translates to:
  /// **'Choose your camera\'s cooling fan so it runs with the cooler.'**
  String get coolingFanAsk;

  /// Full UI one-time prompt for the cooling fan
  ///
  /// In en, this message translates to:
  /// **'Choose your camera\'s cooling fan in Settings › Camera so it runs with the cooler.'**
  String get coolingFanAskFullUi;

  /// Cooling fan pill's action: opens the Camera window
  ///
  /// In en, this message translates to:
  /// **'Choose'**
  String get coolingFanChoose;

  /// Cooling fan pill's dismiss action
  ///
  /// In en, this message translates to:
  /// **'Not now'**
  String get coolingFanNotNow;

  /// Plan sheet: the flats choice for a profile set to ask
  ///
  /// In en, this message translates to:
  /// **'Flats'**
  String get planFlats;

  /// Plan sheet flats choice: no flats this run
  ///
  /// In en, this message translates to:
  /// **'None'**
  String get planFlatsNone;

  /// Simple Mode Storage block: heading while the first read is out
  ///
  /// In en, this message translates to:
  /// **'Checking the disk…'**
  String get shellStorageReading;

  /// Simple Mode Storage block: heading when the read failed and no store is known
  ///
  /// In en, this message translates to:
  /// **'Unknown disk'**
  String get shellStorageUnknownDisk;

  /// Disk chooser title
  ///
  /// In en, this message translates to:
  /// **'Choose a disk for your frames'**
  String get storageDiskChooseTitle;

  /// Disk chooser: look for disks again
  ///
  /// In en, this message translates to:
  /// **'Rescan'**
  String get storageDiskRescan;

  /// Disk chooser: confirm the selected disk
  ///
  /// In en, this message translates to:
  /// **'Use Disk'**
  String get storageDiskUse;

  /// Disk chooser: confirm, erasing the disk
  ///
  /// In en, this message translates to:
  /// **'Erase and Use Disk'**
  String get storageDiskEraseUse;

  /// Disk chooser with no disks
  ///
  /// In en, this message translates to:
  /// **'No external disks found.\n\nConnect a USB drive or SSD to the server, then Rescan.'**
  String get storageDiskNoneFound;

  /// Disk chooser: the disk frames go to now
  ///
  /// In en, this message translates to:
  /// **'In use'**
  String get storageDiskInUse;

  /// Disk chooser: switch to reformat a working disk
  ///
  /// In en, this message translates to:
  /// **'Erase and change format'**
  String get storageDiskEraseChange;

  /// Disk chooser: under the reformat switch
  ///
  /// In en, this message translates to:
  /// **'This disk already works as a store ({fileSystem}). Turn this on only to reformat it — everything on it will be lost.'**
  String storageDiskEraseChangeNote(String fileSystem);

  /// Disk chooser: warning heading
  ///
  /// In en, this message translates to:
  /// **'This disk must be erased'**
  String get storageDiskMustErase;

  /// Disk chooser: why the disk must be erased
  ///
  /// In en, this message translates to:
  /// **'ARA saves frames in a format this disk doesn\'t use, so it has to be erased first. Everything on it will be lost.'**
  String get storageDiskMustEraseNote;

  /// Disk chooser: format option
  ///
  /// In en, this message translates to:
  /// **'exFAT — take the drive with you'**
  String get storageDiskExfat;

  /// Disk chooser: exFAT detail
  ///
  /// In en, this message translates to:
  /// **'Plugs straight into Windows and Mac at home. After a power cut, run Check disk.'**
  String get storageDiskExfatNote;

  /// Disk chooser: format option
  ///
  /// In en, this message translates to:
  /// **'ext4 — drive lives on the rig'**
  String get storageDiskExt4;

  /// Disk chooser: ext4 detail
  ///
  /// In en, this message translates to:
  /// **'Journaled (self-healing after power cuts). Readable on Linux computers; Windows and Mac can\'t read it — frames arrive there via the backup mirror.'**
  String get storageDiskExt4Note;

  /// Disk chooser: type the disk name before erasing
  ///
  /// In en, this message translates to:
  /// **'Type “{label}” to confirm:'**
  String storageDiskTypeLabel(String label);

  /// Disk chooser: the word ERASE is typed literally, keep it in English
  ///
  /// In en, this message translates to:
  /// **'This disk has no label — type ERASE to confirm:'**
  String get storageDiskTypeErase;

  /// Disk chooser: in progress
  ///
  /// In en, this message translates to:
  /// **'Erasing and connecting the disk…'**
  String get storageDiskErasing;

  /// Disk chooser: in progress
  ///
  /// In en, this message translates to:
  /// **'Connecting the disk…'**
  String get storageDiskConnecting;

  /// Disk chooser: done
  ///
  /// In en, this message translates to:
  /// **'{name} is now used for frames.'**
  String storageDiskNowUsed(String name);

  /// Disk chooser: after Couldn't
  ///
  /// In en, this message translates to:
  /// **'read the server\'s disks'**
  String get storageDiskReadAction;

  /// Use Disk failed
  ///
  /// In en, this message translates to:
  /// **'This disk needs to be erased before ARA can use it.'**
  String get storageConfigureNotExt4;

  /// Use Disk failed
  ///
  /// In en, this message translates to:
  /// **'The disk changed since the list was loaded. Rescan and try again.'**
  String get storageConfigureLabelMismatch;

  /// Use Disk failed
  ///
  /// In en, this message translates to:
  /// **'The disk frames are saved to now is in use, so ARA can\'t switch away from it. Close anything reading it, then try again.'**
  String get storageConfigurePreviousBusy;

  /// Use Disk failed
  ///
  /// In en, this message translates to:
  /// **'The disk is in use. Close anything reading it, then try again.'**
  String get storageConfigureBusy;

  /// Use Disk failed
  ///
  /// In en, this message translates to:
  /// **'That disk holds the server\'s operating system and can\'t be used.'**
  String get storageConfigureSystemDisk;

  /// Use Disk failed
  ///
  /// In en, this message translates to:
  /// **'The disk disconnected. Reconnect it and rescan.'**
  String get storageConfigureDisconnected;

  /// Use Disk failed
  ///
  /// In en, this message translates to:
  /// **'The server is missing part of its installation. Reinstall openastroara-server.'**
  String get storageConfigureHelperMissing;

  /// Use Disk failed
  ///
  /// In en, this message translates to:
  /// **'Erasing the disk failed. See the server log for details.'**
  String get storageConfigureMkfsFailed;

  /// Use Disk failed
  ///
  /// In en, this message translates to:
  /// **'The disk was prepared but could not be connected.'**
  String get storageConfigureMountFailed;

  /// Erase step: the frame count is loading
  ///
  /// In en, this message translates to:
  /// **'Checking what is on this disk…'**
  String get storageDiskContentsChecking;

  /// Erase step: what erasing would lose
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{This disk holds 1 frame, from {date}.} other{This disk holds {count} frames. The newest is from {date}.}}'**
  String storageDiskContentsFrames(int count, String date);

  /// Erase step: frames whose date the rig did not give
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{This disk holds 1 frame.} other{This disk holds {count} frames.}}'**
  String storageDiskContentsFramesUndated(int count);

  /// Erase step: the request for the disk's contents failed
  ///
  /// In en, this message translates to:
  /// **'ARA couldn\'t read what is on this disk. Erasing it loses everything on it.'**
  String get storageDiskContentsReadFailed;

  /// Erase step: a readable disk with no frames
  ///
  /// In en, this message translates to:
  /// **'There are no frames on this disk.'**
  String get storageDiskContentsNone;

  /// Erase step warning
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{1 frame has not been copied to another disk by ARA.} other{{count} frames have not been copied to another disk by ARA.}}'**
  String storageDiskContentsNotCopied(int count);

  /// Erase step: the disk's contents can't be read
  ///
  /// In en, this message translates to:
  /// **'ARA can\'t see what is on this disk while it isn\'t connected. Erasing it loses everything on it.'**
  String get storageDiskContentsUnknown;

  /// Use Disk or Erase failed: the server's kernel cannot mount exFAT
  ///
  /// In en, this message translates to:
  /// **'This server can\'t read exFAT drives. Erase the drive as ext4 and ARA can use it.'**
  String get storageConfigureExfatUnsupported;

  /// Use Disk failed
  ///
  /// In en, this message translates to:
  /// **'Could not use {name}.'**
  String storageConfigureFailed(String name);

  /// Check disk question
  ///
  /// In en, this message translates to:
  /// **'Check the store drive?'**
  String get storageCheckTitle;

  /// Check disk question
  ///
  /// In en, this message translates to:
  /// **'The drive is briefly taken offline while its filesystem is verified and repaired if needed. Takes seconds to a couple of minutes; capture must be idle.'**
  String get storageCheckBody;

  /// Eject refused
  ///
  /// In en, this message translates to:
  /// **'No store drive to eject.'**
  String get storageNoDriveEject;

  /// Check disk refused
  ///
  /// In en, this message translates to:
  /// **'No store drive to check — connect one first.'**
  String get storageNoDriveCheck;

  /// Device chooser title
  ///
  /// In en, this message translates to:
  /// **'Choose {type}'**
  String deviceChooserTitle(String type);

  /// Device chooser: tooltip
  ///
  /// In en, this message translates to:
  /// **'Re-scan'**
  String get deviceChooserRescan;

  /// Device chooser: the manual filter wheel row
  ///
  /// In en, this message translates to:
  /// **'No driver: you swap filters by hand and Ara asks for each one'**
  String get deviceChooserManualWheel;

  /// Device chooser: empty
  ///
  /// In en, this message translates to:
  /// **'No devices found'**
  String get deviceChooserNoneTitle;

  /// Device chooser: empty
  ///
  /// In en, this message translates to:
  /// **'No Alpaca responder answered on Ara\'s subnet — most often AlpacaBridge (ARA\'s equipment hub) is not installed or its service is stopped. On Ara host, check:'**
  String get deviceChooserNoneBody;

  /// Device chooser: empty
  ///
  /// In en, this message translates to:
  /// **'A driver on another host must be reachable from Ara\'s subnet (UDP 32227).'**
  String get deviceChooserOtherHost;

  /// Device chooser: error
  ///
  /// In en, this message translates to:
  /// **'Discovery failed'**
  String get deviceChooserFailed;

  /// Device chooser: error
  ///
  /// In en, this message translates to:
  /// **'Retry'**
  String get deviceChooserRetry;

  /// Device chooser: no rig
  ///
  /// In en, this message translates to:
  /// **'Not connected — connect to your rig to save this.'**
  String get deviceChooserNotConnected;

  /// Bug report review title
  ///
  /// In en, this message translates to:
  /// **'Before you share this bug report'**
  String get bugReviewTitle;

  /// Bug report review confirm
  ///
  /// In en, this message translates to:
  /// **'Download anyway'**
  String get bugReviewDownload;

  /// Bug report review
  ///
  /// In en, this message translates to:
  /// **'The bundle contains:'**
  String get bugReviewContains;

  /// Bug report review
  ///
  /// In en, this message translates to:
  /// **'• Ara\'s recent log files'**
  String get bugReviewNoSources;

  /// Bug report review
  ///
  /// In en, this message translates to:
  /// **'• your profile with coordinates, serial numbers, hostnames, usernames in paths, private network addresses and tokens scrubbed'**
  String get bugReviewProfileRedacted;

  /// Bug report review
  ///
  /// In en, this message translates to:
  /// **'• your profile with the site rounded to ~111 km — it may still include equipment credentials, API / notification tokens and network addresses'**
  String get bugReviewProfileCoarse;

  /// Bug report review
  ///
  /// In en, this message translates to:
  /// **'• your full profile — which may include equipment credentials, API / notification tokens, precise observatory coordinates, and network addresses'**
  String get bugReviewProfileFull;

  /// Bug report review
  ///
  /// In en, this message translates to:
  /// **'• system info, with this device\'s username scrubbed from its path'**
  String get bugReviewSystemRedacted;

  /// Bug report review
  ///
  /// In en, this message translates to:
  /// **'• system info, including this device\'s filesystem path'**
  String get bugReviewSystemFull;

  /// Bug report review
  ///
  /// In en, this message translates to:
  /// **'• this app\'s own error log, saved as a separate file beside the ZIP'**
  String get bugReviewClientLog;

  /// Bug report review
  ///
  /// In en, this message translates to:
  /// **'Approximate size: {size}.'**
  String bugReviewSize(String size);

  /// Bug report review
  ///
  /// In en, this message translates to:
  /// **'Only share it with people you trust (e.g. the developers) — treat it like a password.'**
  String get bugReviewTrust;

  /// Bug report review: a source with no files
  ///
  /// In en, this message translates to:
  /// **'none'**
  String get bugReviewNone;

  /// Bug report review
  ///
  /// In en, this message translates to:
  /// **'1 file'**
  String get bugReviewOneFile;

  /// Bug report review
  ///
  /// In en, this message translates to:
  /// **'{count} files'**
  String bugReviewFiles(String count);

  /// Bug report review
  ///
  /// In en, this message translates to:
  /// **', the largest cut to their newest part'**
  String get bugReviewCut;

  /// Bug report source
  ///
  /// In en, this message translates to:
  /// **'Ara\'s own logs'**
  String get bugSourceAra;

  /// Bug report source
  ///
  /// In en, this message translates to:
  /// **'Ara\'s NINA-layer logs'**
  String get bugSourceAraNina;

  /// Bug report source
  ///
  /// In en, this message translates to:
  /// **'AlpacaBridge logs (camera, mount, focuser, wheel)'**
  String get bugSourceAlpacaBridge;

  /// Bug report source
  ///
  /// In en, this message translates to:
  /// **'guider (PHD2) debug and guide logs'**
  String get bugSourceGuider;

  /// Bug report source
  ///
  /// In en, this message translates to:
  /// **'package install history'**
  String get bugSourceSystem;

  /// Bug report source
  ///
  /// In en, this message translates to:
  /// **'system journal (Ara, AlpacaBridge, the guider, the kernel)'**
  String get bugSourceJournal;

  /// Takeover request title
  ///
  /// In en, this message translates to:
  /// **'{name} wants to connect'**
  String takeoverWants(String name);

  /// Takeover request
  ///
  /// In en, this message translates to:
  /// **'Only one client can control the observatory at a time. Allowing hands control to the other client and disconnects this one; the running sequence continues either way.'**
  String get takeoverDetail;

  /// Takeover request answer
  ///
  /// In en, this message translates to:
  /// **'Keep me connected'**
  String get takeoverKeep;

  /// Takeover request answer
  ///
  /// In en, this message translates to:
  /// **'Allow'**
  String get takeoverAllow;

  /// Manual filter swap title
  ///
  /// In en, this message translates to:
  /// **'Install the {target} filter'**
  String filterSwapInstall(String target);

  /// Manual filter swap
  ///
  /// In en, this message translates to:
  /// **'Swap in {target}, then confirm. A run waiting on it continues when you do.'**
  String filterSwapDetail(String target);

  /// Manual filter swap
  ///
  /// In en, this message translates to:
  /// **'Take out {current} and swap in {target}, then confirm. A run waiting on it continues when you do.'**
  String filterSwapDetailFrom(String current, String target);

  /// Manual filter swap answer
  ///
  /// In en, this message translates to:
  /// **'Cancel swap'**
  String get filterSwapCancel;

  /// Manual filter swap answer
  ///
  /// In en, this message translates to:
  /// **'Later'**
  String get filterSwapLater;

  /// Manual filter swap answer
  ///
  /// In en, this message translates to:
  /// **'{target} is in'**
  String filterSwapIsIn(String target);

  /// Manual filter swap failure
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t confirm the filter'**
  String get filterSwapCouldntConfirm;

  /// Manual filter swap failure
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t cancel the swap'**
  String get filterSwapCouldntCancel;

  /// Bug report card title
  ///
  /// In en, this message translates to:
  /// **'Send me a bug report'**
  String get bugCardTitle;

  /// Bug report card
  ///
  /// In en, this message translates to:
  /// **'Bundles the rig\'s logs (Ara, AlpacaBridge, the guider, the system journal) + your profile + system info into a ZIP you can attach to a report.'**
  String get bugCardBody;

  /// Bug report card: progress
  ///
  /// In en, this message translates to:
  /// **'Packing the logs on the rig… {size} read'**
  String bugCardPacking(String size);

  /// Bug report card: label before the range
  ///
  /// In en, this message translates to:
  /// **'Covers: '**
  String get bugCardCovers;

  /// Bug report card: label before the sharing mode
  ///
  /// In en, this message translates to:
  /// **'Sharing: '**
  String get bugCardSharing;

  /// Bug report card: sharing option
  ///
  /// In en, this message translates to:
  /// **'This app\'s error log'**
  String get bugCardAppLog;

  /// Bug report card: action
  ///
  /// In en, this message translates to:
  /// **'Prepare & download'**
  String get bugCardPrepare;

  /// Bug report card: action
  ///
  /// In en, this message translates to:
  /// **'Save client log'**
  String get bugCardSaveLog;

  /// Bug report range
  ///
  /// In en, this message translates to:
  /// **'Last night (whole rig)'**
  String get bugRangeNight;

  /// Bug report range
  ///
  /// In en, this message translates to:
  /// **'Last 3 days (whole rig)'**
  String get bugRange3Days;

  /// Bug report range
  ///
  /// In en, this message translates to:
  /// **'Last 7 days (whole rig)'**
  String get bugRange7Days;

  /// Bug report range
  ///
  /// In en, this message translates to:
  /// **'Ara logs only'**
  String get bugRangeAraOnly;

  /// Bug report sharing mode
  ///
  /// In en, this message translates to:
  /// **'Include everything'**
  String get bugModeFull;

  /// Bug report sharing mode
  ///
  /// In en, this message translates to:
  /// **'best for debugging'**
  String get bugModeFullNote;

  /// Bug report sharing mode
  ///
  /// In en, this message translates to:
  /// **'Coarse GPS only'**
  String get bugModeCoarse;

  /// Bug report sharing mode
  ///
  /// In en, this message translates to:
  /// **'site rounded to ~111 km'**
  String get bugModeCoarseNote;

  /// Bug report sharing mode
  ///
  /// In en, this message translates to:
  /// **'Redact all sensitive items'**
  String get bugModeRedacted;

  /// Bug report sharing mode
  ///
  /// In en, this message translates to:
  /// **'coordinates, serials, hostnames, usernames in paths, private IPs, tokens'**
  String get bugModeRedactedNote;

  /// Bug report: file dialog title
  ///
  /// In en, this message translates to:
  /// **'Choose where to save the bug report'**
  String get bugPickTitle;

  /// Bug report saved
  ///
  /// In en, this message translates to:
  /// **'Saved {name}'**
  String bugSaved(String name);

  /// Bug report and client log saved
  ///
  /// In en, this message translates to:
  /// **'Saved {name} and {log}'**
  String bugSavedBoth(String name, String log);

  /// Bug report failed
  ///
  /// In en, this message translates to:
  /// **'Bug report failed: {reason}'**
  String bugFailed(String reason);

  /// Bug report failure reason
  ///
  /// In en, this message translates to:
  /// **'the rig is still packing the logs; try again in a moment'**
  String get bugFailStillPacking;

  /// Bug report failure reason
  ///
  /// In en, this message translates to:
  /// **'the rig could not pack the logs'**
  String get bugFailCouldNotPack;

  /// Bug report failure reason
  ///
  /// In en, this message translates to:
  /// **'Ara took too long to respond.'**
  String get bugFailTimeout;

  /// Bug report failure reason
  ///
  /// In en, this message translates to:
  /// **'could not reach Ara.'**
  String get bugFailUnreachable;

  /// Bug report failure reason
  ///
  /// In en, this message translates to:
  /// **'Ara returned an error ({status}).'**
  String bugFailStatus(String status);

  /// Bug report failure reason
  ///
  /// In en, this message translates to:
  /// **'something went wrong talking to Ara.'**
  String get bugFailTalking;

  /// Bug report failure reason
  ///
  /// In en, this message translates to:
  /// **'an unexpected error occurred.'**
  String get bugFailUnexpected;

  /// Client log: file dialog title
  ///
  /// In en, this message translates to:
  /// **'Choose where to save the client log'**
  String get clientLogPickTitle;

  /// Client log saved
  ///
  /// In en, this message translates to:
  /// **'Saved {path}'**
  String clientLogSaved(String path);

  /// After Couldn't
  ///
  /// In en, this message translates to:
  /// **'save the log'**
  String get clientLogSaveAction;

  /// Eject result
  ///
  /// In en, this message translates to:
  /// **'Ejected — safe to unplug the drive.'**
  String get storageEjected;

  /// Eject result
  ///
  /// In en, this message translates to:
  /// **'The drive is busy — wait for writes to finish and try again.'**
  String get storageEjectBusy;

  /// Eject result
  ///
  /// In en, this message translates to:
  /// **'Eject failed: {reason}'**
  String storageEjectFailed(String reason);

  /// After Couldn't
  ///
  /// In en, this message translates to:
  /// **'eject the drive'**
  String get storageEjectAction;

  /// Check disk result; the quoted name is a full UI button
  ///
  /// In en, this message translates to:
  /// **'Disk checked — repairs were made. Worth re-running \"Find frames on disk\".'**
  String get storageCheckRepaired;

  /// Check disk result
  ///
  /// In en, this message translates to:
  /// **'Disk checked — clean.'**
  String get storageCheckClean;

  /// Check disk result
  ///
  /// In en, this message translates to:
  /// **'Check failed: {reason}'**
  String storageCheckFailed(String reason);

  /// After Couldn't
  ///
  /// In en, this message translates to:
  /// **'check the disk'**
  String get storageCheckAction;

  /// Another client took control
  ///
  /// In en, this message translates to:
  /// **'Session transferred'**
  String get sessionTransferredTitle;

  /// Another client took control
  ///
  /// In en, this message translates to:
  /// **'Another client took control of the observatory, so this client was disconnected. The running sequence is unaffected — it continues on Ara.'**
  String get sessionTransferredDetail;

  /// Check disk result in Simple Mode, which has no Find frames on disk button
  ///
  /// In en, this message translates to:
  /// **'Disk checked — repairs were made.'**
  String get storageCheckRepairedSimple;

  /// Disk detail line
  ///
  /// In en, this message translates to:
  /// **'not formatted'**
  String get storageDiskNotFormatted;

  /// Disk detail line
  ///
  /// In en, this message translates to:
  /// **'needs erasing ({fileSystem})'**
  String storageDiskNeedsErasing(String fileSystem);

  /// Disk detail line
  ///
  /// In en, this message translates to:
  /// **'in use at {path}'**
  String storageDiskInUseAt(String path);

  /// After Couldn't
  ///
  /// In en, this message translates to:
  /// **'find devices'**
  String get deviceChooserFindAction;

  /// After Couldn't: the Stop Mount request failed
  ///
  /// In en, this message translates to:
  /// **'stop the mount'**
  String get stopMountAction;

  /// Message pill: Stop Mount could not be sent, no server
  ///
  /// In en, this message translates to:
  /// **'Stop Mount was not issued — no server connection.'**
  String get stopMountNoServer;

  /// Message pill: Stop Mount failed. reason is a whole sentence
  ///
  /// In en, this message translates to:
  /// **'{reason} Try Stop Mount again.'**
  String stopMountFailed(String reason);

  /// V-curve chart: autofocus is running, no probe yet
  ///
  /// In en, this message translates to:
  /// **'Waiting for the first probe…'**
  String get focusChartFirstProbe;

  /// V-curve chart: nothing measured yet
  ///
  /// In en, this message translates to:
  /// **'Run autofocus to see the V-curve.'**
  String get focusChartEmpty;

  /// V-curve chart: bottom axis name
  ///
  /// In en, this message translates to:
  /// **'Focuser position'**
  String get focusChartPosition;

  /// V-curve chart: marker at the fitted best position
  ///
  /// In en, this message translates to:
  /// **'fit {position}'**
  String focusChartFit(int position);

  /// V-curve chart: marker at the calibrated best position
  ///
  /// In en, this message translates to:
  /// **'calibrated {position}'**
  String focusChartCalibrated(int position);

  /// V-curve chart: marker at the final focus position
  ///
  /// In en, this message translates to:
  /// **'focus {position}'**
  String focusChartFocus(int position);

  /// Simple Mode warning pill while the daemon's guider.calibration.failed diagnostic is open
  ///
  /// In en, this message translates to:
  /// **'The guider could not calibrate, so it is guiding on its earlier calibration. Guiding may be less accurate tonight.'**
  String get simpleGuiderCalibrationFailed;

  /// Simple Mode warning pill and camera-window caption while a cooler the daemon switched on does not respond (#2076)
  ///
  /// In en, this message translates to:
  /// **'Cooler not responding: is the camera\'s 12 V connected?'**
  String get simpleCoolerNotResponding;

  /// Simple Mode warning pill while the daemon's dew_formation diagnostic is open
  ///
  /// In en, this message translates to:
  /// **'Dew may be forming: stars are swelling at high humidity. Wipe the optics or turn on the dew heaters. Scheduled autofocus waits until it clears.'**
  String get simpleDewWarning;

  /// Simple Mode warning pill when a profile device goes into an error state (#1559); device is its type, e.g. Mount
  ///
  /// In en, this message translates to:
  /// **'{device} didn\'t connect. Check it\'s powered and plugged in, then Reconnect.'**
  String simpleDeviceDidNotConnect(String device);

  /// Title of Simple Mode's card while the rig link is down (#1559)
  ///
  /// In en, this message translates to:
  /// **'Connect to your rig'**
  String get simpleRigConnectTitle;

  /// Title of the same card during offline planning
  ///
  /// In en, this message translates to:
  /// **'Go online'**
  String get simpleRigGoOnline;

  /// Under the card title; rig is the session's rig name
  ///
  /// In en, this message translates to:
  /// **'The link to {rig} dropped. A running plan keeps going on the rig.'**
  String simpleRigDropped(String rig);

  /// Under the card title during offline planning
  ///
  /// In en, this message translates to:
  /// **'You are planning offline. Connect a rig to image.'**
  String get simpleRigOfflineHint;

  /// Card heading over the session's rig
  ///
  /// In en, this message translates to:
  /// **'This session'**
  String get simpleRigThisSession;

  /// Card heading over the other rigs
  ///
  /// In en, this message translates to:
  /// **'Other rigs'**
  String get simpleRigOthers;

  /// Card heading over the rigs when there is no session rig
  ///
  /// In en, this message translates to:
  /// **'Rigs'**
  String get simpleRigRigs;

  /// A rig row: found on the network now
  ///
  /// In en, this message translates to:
  /// **'Found'**
  String get simpleRigFound;

  /// A rig row: saved this session but not found on the network now
  ///
  /// In en, this message translates to:
  /// **'Saved, not seen now'**
  String get simpleRigSavedNotSeen;

  /// A rig row's button while its handshake runs
  ///
  /// In en, this message translates to:
  /// **'Connecting…'**
  String get simpleRigConnecting;

  /// A rig row's button
  ///
  /// In en, this message translates to:
  /// **'Connect'**
  String get simpleRigConnect;

  /// The card while discovery has found nothing
  ///
  /// In en, this message translates to:
  /// **'Looking for rigs on this network…'**
  String get simpleRigLooking;

  /// The card when a rig did not answer; address is host:port
  ///
  /// In en, this message translates to:
  /// **'No rig answered at {address}. Check the rig is on and on this network, or try its name.'**
  String simpleRigNoAnswer(String address);

  /// The card's button and field label for a rig discovery cannot see
  ///
  /// In en, this message translates to:
  /// **'Add by address'**
  String get simpleRigAddByAddress;

  /// Hint in the card's address field; made-up example address
  ///
  /// In en, this message translates to:
  /// **'astro-pi.local or 192.0.2.24'**
  String get simpleRigAddressHint;

  /// Error under the card's address field when the typed address cannot be read (#1559); made-up example address
  ///
  /// In en, this message translates to:
  /// **'Type the rig\'s name or address, for example astro-pi.local or 192.0.2.24:5555.'**
  String get simpleRigAddressInvalid;

  /// Mount window row: Parked / Unparked / Home (#1649)
  ///
  /// In en, this message translates to:
  /// **'Position'**
  String get deviceMountPosition;

  /// Mount window row: the mount's drive rate (#1649)
  ///
  /// In en, this message translates to:
  /// **'Tracking speed'**
  String get deviceTrackingSpeed;

  /// Tracking speed segment: the stars' rate
  ///
  /// In en, this message translates to:
  /// **'Sidereal'**
  String get trackingRateSidereal;

  /// Tracking speed segment: the Moon's rate
  ///
  /// In en, this message translates to:
  /// **'Lunar'**
  String get trackingRateLunar;

  /// Tracking speed segment: the Sun's rate
  ///
  /// In en, this message translates to:
  /// **'Solar'**
  String get trackingRateSolar;

  /// Tracking speed segment: the King rate (sidereal corrected for refraction)
  ///
  /// In en, this message translates to:
  /// **'King'**
  String get trackingRateKing;

  /// Mount window caption under Tracking speed when it is not Sidereal; rate is lower case (lunar, solar, King)
  ///
  /// In en, this message translates to:
  /// **'Tracking at the {rate} rate. Back to Sidereal for stars and galaxies.'**
  String deviceTrackingOffSidereal(String rate);

  /// Message pill when the mount refused a tracking speed
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t change the tracking speed'**
  String get deviceCouldntSetTrackingSpeed;

  /// Direction pad speed: 5 % of the mount's top speed
  ///
  /// In en, this message translates to:
  /// **'Slow'**
  String get padSlow;

  /// Direction pad speed: 25 % of the mount's top speed
  ///
  /// In en, this message translates to:
  /// **'Medium'**
  String get padMedium;

  /// Direction pad speed: the mount's top speed
  ///
  /// In en, this message translates to:
  /// **'Fast'**
  String get padFast;

  /// Direction pad: what the speed segments choose (screen readers)
  ///
  /// In en, this message translates to:
  /// **'Speed'**
  String get padSpeed;

  /// Direction pad button: north
  ///
  /// In en, this message translates to:
  /// **'N'**
  String get padNorth;

  /// Direction pad button: south
  ///
  /// In en, this message translates to:
  /// **'S'**
  String get padSouth;

  /// Direction pad button: east
  ///
  /// In en, this message translates to:
  /// **'E'**
  String get padEast;

  /// Direction pad button: west
  ///
  /// In en, this message translates to:
  /// **'W'**
  String get padWest;

  /// Direction pad button (screen readers)
  ///
  /// In en, this message translates to:
  /// **'Move north'**
  String get padMoveNorth;

  /// Direction pad button (screen readers)
  ///
  /// In en, this message translates to:
  /// **'Move south'**
  String get padMoveSouth;

  /// Direction pad button (screen readers)
  ///
  /// In en, this message translates to:
  /// **'Move east'**
  String get padMoveEast;

  /// Direction pad button (screen readers)
  ///
  /// In en, this message translates to:
  /// **'Move west'**
  String get padMoveWest;

  /// Direction pad button (screen readers)
  ///
  /// In en, this message translates to:
  /// **'Move north-east'**
  String get padMoveNorthEast;

  /// Direction pad button (screen readers)
  ///
  /// In en, this message translates to:
  /// **'Move north-west'**
  String get padMoveNorthWest;

  /// Direction pad button (screen readers)
  ///
  /// In en, this message translates to:
  /// **'Move south-east'**
  String get padMoveSouthEast;

  /// Direction pad button (screen readers)
  ///
  /// In en, this message translates to:
  /// **'Move south-west'**
  String get padMoveSouthWest;

  /// Direction pad centre button (screen readers)
  ///
  /// In en, this message translates to:
  /// **'Stop the mount'**
  String get padStopMount;

  /// Direction pad: why it is off while the mount is parked
  ///
  /// In en, this message translates to:
  /// **'Unpark to move the mount'**
  String get padUnparkToMove;

  /// Direction pad: why it is off while the plan is imaging
  ///
  /// In en, this message translates to:
  /// **'Pause the plan to move the mount'**
  String get padPausePlan;

  /// Direction pad and Check: why they are off while polar alignment runs
  ///
  /// In en, this message translates to:
  /// **'Polar alignment is running'**
  String get padPolarRunning;

  /// Direction pad: why it is off while the Guider guides
  ///
  /// In en, this message translates to:
  /// **'Stop guiding to move the mount'**
  String get padStopGuiding;

  /// Direction pad: directions are off during a GoTo, Stop still works
  ///
  /// In en, this message translates to:
  /// **'Moving to a target: Stop ends it'**
  String get padSlewing;

  /// Mount window: the mount reports no manual rates
  ///
  /// In en, this message translates to:
  /// **'This mount can\'t be moved by hand'**
  String get padNoRates;

  /// Mount window: caption over the mount's RA/Dec (#1650)
  ///
  /// In en, this message translates to:
  /// **'Pointing'**
  String get pointingLabel;

  /// Mount window: caption over the solved RA/Dec after Check
  ///
  /// In en, this message translates to:
  /// **'Pointing (solved)'**
  String get pointingSolved;

  /// Mount window button: take a quick frame and plate-solve it
  ///
  /// In en, this message translates to:
  /// **'Check'**
  String get pointingCheck;

  /// Screen-reader hint of the Check button
  ///
  /// In en, this message translates to:
  /// **'Take a quick frame and plate-solve it'**
  String get pointingCheckHint;

  /// Mount window: while Check runs
  ///
  /// In en, this message translates to:
  /// **'Taking a frame and solving…'**
  String get pointingSolving;

  /// Mount window: how far the solve is from the Target ("2.4′ from M 31")
  ///
  /// In en, this message translates to:
  /// **'{separation} from {target}'**
  String pointingFrom(String separation, String target);

  /// Mount window: how far the solve is from where the mount was sent, no Target matched
  ///
  /// In en, this message translates to:
  /// **'{separation} from the last go-to'**
  String pointingFromLastGoto(String separation);

  /// Mount window: the solve is within 1′ of the Target
  ///
  /// In en, this message translates to:
  /// **'On target'**
  String get pointingOnTarget;

  /// Mount window caption: the solve against the mount's own position
  ///
  /// In en, this message translates to:
  /// **'Mount reports {separation} off'**
  String pointingMountOff(String separation);

  /// Mount window caption: Check synced the rotator's sky angle
  ///
  /// In en, this message translates to:
  /// **'Rotator synced to {angle}'**
  String pointingRotatorSynced(String angle);

  /// Mount window: Check's solve failed
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t solve this frame'**
  String get pointingCouldntSolve;

  /// Mount window button: slew and center on the Target by repeated solves
  ///
  /// In en, this message translates to:
  /// **'Center'**
  String get pointingCentre;

  /// Mount window: while Center runs
  ///
  /// In en, this message translates to:
  /// **'Centering…'**
  String get pointingCentring;

  /// Mount window button: sync the mount to the solved position
  ///
  /// In en, this message translates to:
  /// **'Sync'**
  String get pointingSync;

  /// Mount window: Sync done, until the next Check
  ///
  /// In en, this message translates to:
  /// **'Synced'**
  String get pointingSynced;

  /// Result pill after Sync
  ///
  /// In en, this message translates to:
  /// **'The mount is synced to the solved position'**
  String get pointingSyncedPill;

  /// Error pill when Sync was refused
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t sync the mount'**
  String get pointingCouldntSync;

  /// Mount window: Center failed
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t center'**
  String get pointingCouldntCentre;

  /// Mount window button after a solver error
  ///
  /// In en, this message translates to:
  /// **'Plate solving settings'**
  String get pointingOpenPlateSolving;

  /// Mount window button after an optics error
  ///
  /// In en, this message translates to:
  /// **'Optics settings'**
  String get pointingOpenOptics;

  /// Mount window button when no camera can take the frame
  ///
  /// In en, this message translates to:
  /// **'Camera settings'**
  String get pointingOpenCamera;

  /// Mount window Check failure: the main camera is busy with an exposure; no settings fix it, so no button
  ///
  /// In en, this message translates to:
  /// **'The camera is taking another frame. Check again once it finishes.'**
  String get pointingCameraBusy;

  /// Mount window: asked when Home is tapped while a plan runs
  ///
  /// In en, this message translates to:
  /// **'A plan is running. Send the mount home anyway?'**
  String get devicePlanRunningHomeAnyway;

  /// Mount window button: send the mount home even though a plan runs
  ///
  /// In en, this message translates to:
  /// **'Home anyway'**
  String get deviceHomeAnyway;

  /// Mount window: why Check is off with no main camera
  ///
  /// In en, this message translates to:
  /// **'Connect the main camera to check where it points'**
  String get pointingNoCamera;

  /// Simple Mode's full-screen read-only view of the Session (#1724); left rail item
  ///
  /// In en, this message translates to:
  /// **'Monitor'**
  String get monitorTitle;

  /// Monitor: screen-reader label of this device's battery
  ///
  /// In en, this message translates to:
  /// **'This device\'s battery: {level} %'**
  String monitorBattery(int level);

  /// Monitor tile status word while the link to the rig is lost
  ///
  /// In en, this message translates to:
  /// **'Last known'**
  String get monitorLastKnown;

  /// Monitor top line: the link to the rig is lost, with how long ago
  ///
  /// In en, this message translates to:
  /// **'Lost contact with the rig · {age} ago'**
  String monitorLinkLost(String age);

  /// Monitor top line reason while the link is lost
  ///
  /// In en, this message translates to:
  /// **'A running plan carries on by itself on the rig.'**
  String get monitorLinkLostReason;

  /// Monitor top line: a device in the profile has dropped
  ///
  /// In en, this message translates to:
  /// **'{device} disconnected'**
  String monitorDeviceDropped(String device);

  /// Monitor top line reason: a device dropped
  ///
  /// In en, this message translates to:
  /// **'Reconnect it from its window. Tap its tile to open it.'**
  String get monitorDeviceDroppedReason;

  /// Monitor top line reason while Unsafe
  ///
  /// In en, this message translates to:
  /// **'The rig reacts as your safety settings say.'**
  String get monitorUnsafeReason;

  /// Monitor top line: the guide star is lost
  ///
  /// In en, this message translates to:
  /// **'Guide star lost'**
  String get monitorStarLost;

  /// Monitor top line reason: guide star lost
  ///
  /// In en, this message translates to:
  /// **'Often clouds passing. Ara holds the next frame until guiding is back.'**
  String get monitorStarLostReason;

  /// Monitor Guider tile status word
  ///
  /// In en, this message translates to:
  /// **'Star lost'**
  String get monitorStarLostWord;

  /// Monitor top line: the Queue won't fit on the disk
  ///
  /// In en, this message translates to:
  /// **'Low disk space · {free} free'**
  String monitorLowDisk(String free);

  /// Monitor top line reason: low disk
  ///
  /// In en, this message translates to:
  /// **'The queue\'s frames won\'t all fit. Free space or shorten the queue.'**
  String get monitorLowDiskReason;

  /// Monitor top line: the cooler can't hold its target
  ///
  /// In en, this message translates to:
  /// **'Cooler can\'t reach {target} · sensor at {sensor}'**
  String monitorCooler(String target, String sensor);

  /// Monitor top line reason: cooler
  ///
  /// In en, this message translates to:
  /// **'Frames still save, but they won\'t match your darks.'**
  String get monitorCoolerReason;

  /// Monitor top line reason during a meridian flip
  ///
  /// In en, this message translates to:
  /// **'Imaging carries on by itself when it is done.'**
  String get monitorFlippingReason;

  /// Monitor top line reason while waiting for the meridian flip
  ///
  /// In en, this message translates to:
  /// **'No frames until the mount flips.'**
  String get monitorWaitingForFlipReason;

  /// Monitor: the plan is paused (by the user or by Ara; the run state does not say which)
  ///
  /// In en, this message translates to:
  /// **'Paused'**
  String get monitorPaused;

  /// Monitor top line: the plan is paused until the user acts
  ///
  /// In en, this message translates to:
  /// **'Plan paused · waiting for you'**
  String get monitorWaitingForYou;

  /// Monitor Now tile status word: paused until the user acts
  ///
  /// In en, this message translates to:
  /// **'Waiting for you'**
  String get monitorWaitingForYouWord;

  /// Monitor top line: a scheduled plan waits for its start
  ///
  /// In en, this message translates to:
  /// **'Waiting for dark · plan starts at {time}'**
  String monitorWaitingForDark(String time);

  /// Monitor top line reason: waiting for dark
  ///
  /// In en, this message translates to:
  /// **'Imaging starts on its own.'**
  String get monitorWaitingForDarkReason;

  /// Monitor top line: no plan
  ///
  /// In en, this message translates to:
  /// **'Rig ready · no plan running'**
  String get monitorNoPlan;

  /// Monitor top line reason: no plan
  ///
  /// In en, this message translates to:
  /// **'Start a plan and this screen fills in.'**
  String get monitorNoPlanReason;

  /// Monitor Now tile: no plan, in place of a countdown
  ///
  /// In en, this message translates to:
  /// **'No plan'**
  String get monitorNoPlanBig;

  /// Monitor Meridian flip tile with no active run
  ///
  /// In en, this message translates to:
  /// **'No plan running'**
  String get monitorNoPlanRunning;

  /// Monitor top line: all well
  ///
  /// In en, this message translates to:
  /// **'All good · imaging {target}'**
  String monitorAllGood(String target);

  /// Monitor top line reason: all well
  ///
  /// In en, this message translates to:
  /// **'Nothing needs you.'**
  String get monitorAllGoodReason;

  /// Monitor top line reason: all well, with the park time
  ///
  /// In en, this message translates to:
  /// **'Nothing needs you. The plan ends at {time}, then the mount parks.'**
  String monitorAllGoodParks(String time);

  /// Monitor tile: what the rig is doing
  ///
  /// In en, this message translates to:
  /// **'Now'**
  String get monitorNow;

  /// Monitor status word: working normally, not exposing
  ///
  /// In en, this message translates to:
  /// **'Running'**
  String get monitorRunning;

  /// Monitor Now tile status word during a meridian flip
  ///
  /// In en, this message translates to:
  /// **'Flipping'**
  String get monitorFlipping;

  /// Monitor Now tile status word: waiting to start
  ///
  /// In en, this message translates to:
  /// **'Waiting'**
  String get monitorWaiting;

  /// Monitor status word: nothing running
  ///
  /// In en, this message translates to:
  /// **'Idle'**
  String get monitorIdle;

  /// Monitor Now tile with no plan
  ///
  /// In en, this message translates to:
  /// **'Nothing running'**
  String get monitorNothingRunning;

  /// Monitor Now tile caption under the frame countdown
  ///
  /// In en, this message translates to:
  /// **'left in this frame'**
  String get monitorLeftInFrame;

  /// Monitor Now tile caption under a countdown to the plan's start
  ///
  /// In en, this message translates to:
  /// **'until the plan starts'**
  String get monitorUntilStart;

  /// Monitor Now tile caption under the meridian wait countdown
  ///
  /// In en, this message translates to:
  /// **'until the flip'**
  String get monitorUntilFlip;

  /// Monitor Now tile: progress on the Target
  ///
  /// In en, this message translates to:
  /// **'Frame {frame} of {frames} · about {left} left'**
  String monitorFrameOf(int frame, int frames, String left);

  /// Monitor Now tile: the upcoming steps
  ///
  /// In en, this message translates to:
  /// **'Up next'**
  String get monitorUpNext;

  /// Monitor Up next row: the next frame (the filter follows when the camera has one)
  ///
  /// In en, this message translates to:
  /// **'Frame {frame}'**
  String monitorUpFrame(int frame);

  /// Monitor Up next row: the park at the end
  ///
  /// In en, this message translates to:
  /// **'Mount parks'**
  String get monitorUpPark;

  /// Monitor Up next row whose time isn't known
  ///
  /// In en, this message translates to:
  /// **'then'**
  String get monitorThen;

  /// Monitor tile: the Target
  ///
  /// In en, this message translates to:
  /// **'Target'**
  String get monitorTarget;

  /// Monitor Target tile with no Target
  ///
  /// In en, this message translates to:
  /// **'No target picked'**
  String get monitorNoTarget;

  /// Monitor Target tile: when imaging this Target ends
  ///
  /// In en, this message translates to:
  /// **'Imaging to {time}'**
  String monitorImagingTo(String time);

  /// Monitor Target tile: maximum altitude tonight
  ///
  /// In en, this message translates to:
  /// **'max {alt}°'**
  String monitorMaxAltitude(String alt);

  /// Monitor Target tile: transit time
  ///
  /// In en, this message translates to:
  /// **'Transits {time}'**
  String monitorTransits(String time);

  /// Monitor Target tile: the Moon's illumination
  ///
  /// In en, this message translates to:
  /// **'Moon {pct} %'**
  String monitorMoon(int pct);

  /// Monitor Target tile status word
  ///
  /// In en, this message translates to:
  /// **'Tracking'**
  String get monitorTracking;

  /// Monitor Target tile status word
  ///
  /// In en, this message translates to:
  /// **'Queued'**
  String get monitorQueued;

  /// Monitor Target tile status word
  ///
  /// In en, this message translates to:
  /// **'Parked'**
  String get monitorParked;

  /// Monitor Meridian flip tile status word: a flip is due
  ///
  /// In en, this message translates to:
  /// **'Automatic'**
  String get monitorFlipAutomatic;

  /// Monitor Meridian flip tile status word: waiting for the frame
  ///
  /// In en, this message translates to:
  /// **'Coming up'**
  String get monitorFlipComingUp;

  /// Monitor Meridian flip tile: when the flip is due
  ///
  /// In en, this message translates to:
  /// **'At {time}'**
  String monitorFlipAt(String time);

  /// Monitor Meridian flip tile: waiting for the exposure
  ///
  /// In en, this message translates to:
  /// **'Waits for the frame in hand, then flips'**
  String get monitorFlipWaitsForFrame;

  /// Monitor Meridian flip tile: auto flip off
  ///
  /// In en, this message translates to:
  /// **'Automatic flip is off'**
  String get monitorFlipOff;

  /// Monitor Meridian flip tile: none due tonight
  ///
  /// In en, this message translates to:
  /// **'No flip tonight'**
  String get monitorFlipNone;

  /// Monitor tile status word: the device dropped
  ///
  /// In en, this message translates to:
  /// **'Disconnected'**
  String get monitorDisconnected;

  /// Monitor Safety strip: since when, and for how long
  ///
  /// In en, this message translates to:
  /// **'Since {time} · {duration}'**
  String monitorSince(String time, String duration);

  /// Monitor Conditions tile status word
  ///
  /// In en, this message translates to:
  /// **'Past limit'**
  String get monitorPastLimit;

  /// Monitor Weather cell
  ///
  /// In en, this message translates to:
  /// **'Cloud'**
  String get monitorCloud;

  /// Monitor Weather cell
  ///
  /// In en, this message translates to:
  /// **'Sky quality'**
  String get monitorSkyQuality;

  /// Monitor Weather cell: air temperature
  ///
  /// In en, this message translates to:
  /// **'Air'**
  String get monitorAir;

  /// Monitor Weather cell: the margin over the dew point
  ///
  /// In en, this message translates to:
  /// **'Above dew point'**
  String get monitorDewMargin;

  /// Monitor Weather cell
  ///
  /// In en, this message translates to:
  /// **'Humidity'**
  String get monitorHumidity;

  /// Monitor Weather cell
  ///
  /// In en, this message translates to:
  /// **'Rain'**
  String get monitorRain;

  /// Monitor Weather cell
  ///
  /// In en, this message translates to:
  /// **'Wind'**
  String get monitorWind;

  /// Monitor Weather wind cell: the gusts
  ///
  /// In en, this message translates to:
  /// **'gusts {kmh}'**
  String monitorGusts(String kmh);

  /// Monitor Weather cell past its limit: the limit
  ///
  /// In en, this message translates to:
  /// **'limit {value}'**
  String monitorLimit(String value);

  /// Monitor Guider tile: the last dither's time
  ///
  /// In en, this message translates to:
  /// **'Last dither {time}'**
  String monitorLastDither(String time);

  /// Monitor tile: the last saved frame's quality
  ///
  /// In en, this message translates to:
  /// **'Last frame'**
  String get monitorLastFrame;

  /// Monitor Last frame reading
  ///
  /// In en, this message translates to:
  /// **'HFR'**
  String get monitorHfr;

  /// Monitor Last frame reading
  ///
  /// In en, this message translates to:
  /// **'Stars'**
  String get monitorStars;

  /// Monitor Last frame reading: the frame's median in ADU
  ///
  /// In en, this message translates to:
  /// **'Sky background'**
  String get monitorSkyBackground;

  /// Monitor Last frame reading: the share of clipped stars
  ///
  /// In en, this message translates to:
  /// **'Clipped stars'**
  String get monitorClippedStars;

  /// Monitor Conditions: label under the focuser's signed temperature change since the last autofocus, which ran at the given clock time
  ///
  /// In en, this message translates to:
  /// **'Autofocus {time}'**
  String monitorAutofocusAt(String time);

  /// Monitor Now tile: frames saved so far
  ///
  /// In en, this message translates to:
  /// **'{n} saved'**
  String monitorSaved(int n);

  /// Monitor Last frame tile: the HFR the session started at
  ///
  /// In en, this message translates to:
  /// **'session start {value}'**
  String monitorStartedAt(String value);

  /// Monitor deviation flag: a reading's change against the session's own start
  ///
  /// In en, this message translates to:
  /// **'{metric} {pct} % since session start'**
  String monitorChangeSinceStart(String metric, String pct);

  /// Monitor Last frame tile status word: a reading moved 15 % or more the wrong way since session start
  ///
  /// In en, this message translates to:
  /// **'Drifting'**
  String get monitorDrifting;

  /// Monitor tile: weather with the camera and focuser temperatures
  ///
  /// In en, this message translates to:
  /// **'Conditions'**
  String get monitorConditions;

  /// Monitor Conditions tile: section heading
  ///
  /// In en, this message translates to:
  /// **'Camera and focuser'**
  String get monitorCameraAndFocuser;

  /// Monitor Conditions tile: the camera sensor's temperature
  ///
  /// In en, this message translates to:
  /// **'Sensor'**
  String get monitorSensor;

  /// Monitor Conditions tile: the cooler's power
  ///
  /// In en, this message translates to:
  /// **'Cooler power'**
  String get monitorCoolerPower;

  /// Monitor Conditions tile: the focuser's temperature
  ///
  /// In en, this message translates to:
  /// **'Focuser'**
  String get monitorFocuserTemp;

  /// Monitor altitude graph key: the plan's imaging span
  ///
  /// In en, this message translates to:
  /// **'Imaging {start} to {end}'**
  String monitorImagingSpan(String start, String end);

  /// Monitor stars graph key: the sky background line has its own scale
  ///
  /// In en, this message translates to:
  /// **'Sky (own scale)'**
  String get monitorSkyOwnScale;

  /// Monitor guider tile: the session's guiding RMS graph
  ///
  /// In en, this message translates to:
  /// **'RMS per saved frame'**
  String get monitorRmsPerFrame;

  /// Monitor guider tile key: the live trace's fixed scale
  ///
  /// In en, this message translates to:
  /// **'fixed ±2.5″'**
  String get monitorFixedScale;

  /// Monitor Tonight timeline: the meridian flip's label
  ///
  /// In en, this message translates to:
  /// **'flip {time}'**
  String monitorFlipPin(String time);

  /// Monitor Tonight timeline: the mount park's label
  ///
  /// In en, this message translates to:
  /// **'park {time}'**
  String monitorParkPin(String time);

  /// Monitor tile: tonight's timeline
  ///
  /// In en, this message translates to:
  /// **'Tonight'**
  String get monitorTonight;

  /// Monitor Tonight tile: the dark and the time imaged so far
  ///
  /// In en, this message translates to:
  /// **'Dark {start} to {end} · {imaged} imaged'**
  String monitorDarkSpan(String start, String end, String imaged);

  /// Monitor Tonight tile with no site set
  ///
  /// In en, this message translates to:
  /// **'Set the site to see tonight\'s dark'**
  String get monitorNoDark;

  /// Monitor: what a screen reader announces when the link to the rig is lost (no age)
  ///
  /// In en, this message translates to:
  /// **'Lost contact with the rig'**
  String get monitorLinkLostShort;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'abort the capture'**
  String get actionAbortCapture;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'apply that to the selected frames'**
  String get actionApplyToFrames;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'build the calibration'**
  String get actionBuildCalibration;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'cancel the copy'**
  String get actionCancelCopy;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'check the disk for frames'**
  String get actionCheckDisk;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'clean the preview cache'**
  String get actionCleanPreviewCache;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'clean the sky photo cache'**
  String get actionCleanSkyPhotoCache;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'clear the ceiling'**
  String get actionClearCeiling;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'confirm the frame arrived'**
  String get actionConfirmFrame;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'connect'**
  String get actionConnect;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'download that'**
  String get actionDownload;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'export those frames'**
  String get actionExportFrames;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'export the stats'**
  String get actionExportStats;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'generate the calibration'**
  String get actionGenerateCalibration;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'list the folders'**
  String get actionListFolders;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'load the dark library'**
  String get actionLoadDarkLibrary;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'load the fault history'**
  String get actionLoadFaults;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'load the frames'**
  String get actionLoadFrames;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'load your library'**
  String get actionLoadLibrary;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'load the logs'**
  String get actionLoadLogs;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'load your notifications'**
  String get actionLoadNotifications;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'load older logs'**
  String get actionLoadOlderLogs;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'load the preview'**
  String get actionLoadPreview;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'load the sessions'**
  String get actionLoadSessions;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'load your saved settings'**
  String get actionLoadSettings;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'look for equipment'**
  String get actionLookForEquipment;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'push the guider profile'**
  String get actionPushGuiderProfile;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'push the time'**
  String get actionPushTime;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'rate the frame'**
  String get actionRateFrame;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'read the camera'**
  String get actionReadCamera;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'read the server clock state'**
  String get actionReadClock;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'read the server\'s disks'**
  String get actionReadServerDisks;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'re-arm safety'**
  String get actionRearmSafety;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'refit the sky model'**
  String get actionRefitSkyModel;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'restart your rig'**
  String get actionRestartRig;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'resume the sequence'**
  String get actionResumeSequence;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'save that'**
  String get actionSave;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'save the horizon skyline'**
  String get actionSaveHorizon;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'save the slot labels'**
  String get actionSaveSlotLabels;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'send the calibration choice'**
  String get actionSendCalibrationChoice;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'do that sequence action'**
  String get actionSequenceAction;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'solve the frame'**
  String get actionSolveFrame;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'work out which star database this rig needs'**
  String get actionStarDatabase;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'start the copy'**
  String get actionStartCopy;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'take that exposure'**
  String get actionTakeExposure;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'take the verification image'**
  String get actionTakeVerification;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'update the tag'**
  String get actionUpdateTag;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'back up that entry'**
  String get actionBackUpEntry;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'keep the backup stream going'**
  String get actionKeepBackupGoing;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'push the GPS fix'**
  String get actionPushGpsFix;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'rebuild the stats catalog'**
  String get actionRebuildStats;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'start copying frames'**
  String get actionStartCopyingFrames;

  /// Backup stream panel: transient transport trouble; reason is the already-readable error sentence
  ///
  /// In en, this message translates to:
  /// **'Backup stream hiccup: {reason}'**
  String backupStreamHiccup(String reason);

  /// Backup stream panel: one frame keeps failing; id is the frame id, reason the readable cause
  ///
  /// In en, this message translates to:
  /// **'Backup of {id} failing: {reason}'**
  String backupEntryFailing(String id, String reason);

  /// GPS on this computer: the receiver gave a fix but the rig rejected the push; reason is the readable error sentence
  ///
  /// In en, this message translates to:
  /// **'Fix read, but the rig refused it: {reason}'**
  String gpsFixRefused(String reason);

  /// GPS on this computer: appended to the refused-push message when the rig answers 422 (daemon too old); starts with a dash and a space
  ///
  /// In en, this message translates to:
  /// **' — update Ara Server on the rig; this version predates GPS on this computer.'**
  String get gpsDaemonTooOld;

  /// Night done / Calibration: dark frames are missing
  ///
  /// In en, this message translates to:
  /// **'Darks'**
  String get calibrationDarks;

  /// Night done / Calibration: bias frames are missing
  ///
  /// In en, this message translates to:
  /// **'Bias'**
  String get calibrationBias;

  /// Night done button: shoot the missing flats
  ///
  /// In en, this message translates to:
  /// **'Take flats'**
  String get calibrationTakeFlats;

  /// Night done button: shoot the missing darks and bias (asks for the lens cap first)
  ///
  /// In en, this message translates to:
  /// **'Take darks'**
  String get calibrationTakeDarks;

  /// Night done: the calibration run in progress, frames taken of planned
  ///
  /// In en, this message translates to:
  /// **'Calibration frames · {done} of {total}'**
  String calibrationTaking(int done, int total);

  /// Night done: the calibration run was stopped part-way, frames taken of planned
  ///
  /// In en, this message translates to:
  /// **'Stopped · {done} of {total}'**
  String calibrationStopped(int done, int total);

  /// Night done: the calibration run finished
  ///
  /// In en, this message translates to:
  /// **'Calibration frames done'**
  String get calibrationDone;

  /// Simple Mode, after Couldn't: generating or starting the calibration run failed
  ///
  /// In en, this message translates to:
  /// **'start the calibration frames'**
  String get calibrationActionShoot;

  /// Simple Mode, after Couldn't: stopping the calibration run failed
  ///
  /// In en, this message translates to:
  /// **'stop the calibration frames'**
  String get calibrationActionStop;

  /// Calibration mode (#1757): flats, darks and bias for the main camera and the Guide Camera's dark library; the mode grid's tile and the rail's name
  ///
  /// In en, this message translates to:
  /// **'Calibration'**
  String get shellModeCalibration;

  /// Calibration panel: the screen reader's name for the row of tabs
  ///
  /// In en, this message translates to:
  /// **'Calibration tabs'**
  String get calibrationTabsGroup;

  /// Calibration panel tab: flats per filter
  ///
  /// In en, this message translates to:
  /// **'Flats'**
  String get calibrationTabFlats;

  /// Calibration panel tab: darks and bias (no filters)
  ///
  /// In en, this message translates to:
  /// **'Darks & bias'**
  String get calibrationTabDarks;

  /// Calibration panel tab: the Guider's dark library and bad-pixel map
  ///
  /// In en, this message translates to:
  /// **'Guide Camera'**
  String get calibrationTabGuide;

  /// Calibration settings chip: how many calibration sets the latest plan's lights lack
  ///
  /// In en, this message translates to:
  /// **'{count} missing'**
  String calibrationChipMissing(int count);

  /// Calibration settings chip: the latest plan's lights have every calibration set
  ///
  /// In en, this message translates to:
  /// **'Ready'**
  String get calibrationChipReady;

  /// Calibration settings chip: no plan has taken lights yet
  ///
  /// In en, this message translates to:
  /// **'No lights'**
  String get calibrationChipNoLights;

  /// Calibration panel: no plan has taken lights, so there is nothing to match; Darks & bias builds a dark library instead
  ///
  /// In en, this message translates to:
  /// **'No plan lights yet. Build a dark library to reuse for weeks.'**
  String get calibrationNoLightsYet;

  /// Calibration panel: this set has matching calibration frames
  ///
  /// In en, this message translates to:
  /// **'Covered'**
  String get calibrationCovered;

  /// Calibration panel: this set has no matching calibration frames
  ///
  /// In en, this message translates to:
  /// **'Missing'**
  String get calibrationMissing;

  /// Calibration panel, Flats: when the flats that count for this filter were shot
  ///
  /// In en, this message translates to:
  /// **'Flats from {date}'**
  String calibrationFlatsFrom(String date);

  /// Calibration: put the darks and bias in the Queue as one Calibration block after the last target
  ///
  /// In en, this message translates to:
  /// **'Add to queue'**
  String get calibrationAddToQueue;

  /// The dark library run's first step: the question its pill asks
  ///
  /// In en, this message translates to:
  /// **'Put the lens cap on the scope, then press Resume to take the darks.'**
  String get calibrationLensCapPrompt;

  /// Calibration: shooting asked while the Plan's run is going; the rig runs one at a time
  ///
  /// In en, this message translates to:
  /// **'A plan is running. Calibration would have to wait for it: add the darks and bias to the queue instead?'**
  String get calibrationPlanRunning;

  /// Calibration panel: shown while the Plan's run is going
  ///
  /// In en, this message translates to:
  /// **'A plan is running: calibration frames go in the queue.'**
  String get calibrationPlanRunningNote;

  /// Calibration question pill: leave it
  ///
  /// In en, this message translates to:
  /// **'Not now'**
  String get calibrationNotNow;

  /// Calibration shutter: why it is off with no main camera
  ///
  /// In en, this message translates to:
  /// **'Connect the camera first.'**
  String get calibrationConnectCamera;

  /// Calibration shutter's spoken label while its run goes
  ///
  /// In en, this message translates to:
  /// **'Stop calibration'**
  String get calibrationShutterStop;

  /// Calibration shutter: why it is off when every set is covered
  ///
  /// In en, this message translates to:
  /// **'Nothing missing.'**
  String get calibrationNothingMissing;

  /// Calibration panel, Guide Camera: the Guider isn't connected
  ///
  /// In en, this message translates to:
  /// **'Connect Camera (Guide) in Settings › Devices to build its dark library and bad-pixel map.'**
  String get calibrationGuideNoGuider;

  /// Calibration panel, Guide Camera: the Guider's dark library
  ///
  /// In en, this message translates to:
  /// **'Dark library'**
  String get calibrationGuideDarks;

  /// Calibration panel, Guide Camera: the guide exposures the loaded dark library covers
  ///
  /// In en, this message translates to:
  /// **'Covers {min} to {max}'**
  String calibrationGuideCovers(String min, String max);

  /// Calibration panel, Guide Camera: no dark library or bad-pixel map yet
  ///
  /// In en, this message translates to:
  /// **'Not built'**
  String get calibrationGuideNotBuilt;

  /// Calibration › Guide Camera: the Guider's file status is being read
  ///
  /// In en, this message translates to:
  /// **'Checking the Guider\'s files…'**
  String get calibrationGuideChecking;

  /// Calibration › Guide Camera: the rig could not read the Guider's dark library and bad-pixel map status
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t read the Guider\'s files'**
  String get calibrationGuideUnread;

  /// Calibration › Guide Camera: under the unread line, why and what happens next
  ///
  /// In en, this message translates to:
  /// **'The Guider may be restarting. Ara reads them again when it is back.'**
  String get calibrationGuideUnreadNote;

  /// Calibration › Guide Camera: reads the Guider's file status again
  ///
  /// In en, this message translates to:
  /// **'Try again'**
  String get calibrationGuideRetry;

  /// Calibration panel, Guide Camera: the Guider subtracts the darks / skips the bad pixels
  ///
  /// In en, this message translates to:
  /// **'Use while guiding'**
  String get calibrationGuideUse;

  /// Calibration panel, Guide Camera: build it again (after a gain or temperature change)
  ///
  /// In en, this message translates to:
  /// **'Rebuild'**
  String get calibrationGuideRebuild;

  /// Calibration panel, Guide Camera: delete the dark library
  ///
  /// In en, this message translates to:
  /// **'Delete'**
  String get calibrationGuideDelete;

  /// Calibration, Guide Camera: the inline question before Delete removes the guider's dark library
  ///
  /// In en, this message translates to:
  /// **'Delete the dark library?'**
  String get calibrationGuideDeleteDarksTitle;

  /// Calibration, Guide Camera: the inline question before Delete removes the guider's bad-pixel map
  ///
  /// In en, this message translates to:
  /// **'Delete the bad-pixel map?'**
  String get calibrationGuideDeleteDefectsTitle;

  /// Calibration, Guide Camera: why Delete asks first
  ///
  /// In en, this message translates to:
  /// **'Building it again takes several minutes of frames with the guide scope covered.'**
  String get calibrationGuideDeleteBody;

  /// Calibration panel, Guide Camera: the Guider's defect map, so hot pixels aren't picked as guide stars
  ///
  /// In en, this message translates to:
  /// **'Bad-pixel map'**
  String get calibrationGuideDefect;

  /// Calibration, Guide Camera: the question before a build (the frames must be dark)
  ///
  /// In en, this message translates to:
  /// **'Cover the guide scope, then build.'**
  String get calibrationGuideCover;

  /// Calibration, Guide Camera question: the guide scope is covered; build
  ///
  /// In en, this message translates to:
  /// **'Covered, build'**
  String get calibrationGuideCovered;

  /// Calibration panel, Guide Camera: builds are refused while guiding
  ///
  /// In en, this message translates to:
  /// **'Stop guiding to build: a plan would lose its guide star.'**
  String get calibrationGuideStopFirst;

  /// Calibration, Add to queue with no queue
  ///
  /// In en, this message translates to:
  /// **'Queue a target first: the darks and bias run after it.'**
  String get calibrationNoQueue;

  /// Calibration, Add to queue: the queue holds one Calibration block
  ///
  /// In en, this message translates to:
  /// **'The queue already has its calibration frames.'**
  String get calibrationAlreadyQueued;

  /// Simple Mode, after Couldn't: queuing the Calibration block failed
  ///
  /// In en, this message translates to:
  /// **'add the calibration frames to the queue'**
  String get calibrationActionQueue;

  /// Simple Mode, after Couldn't: building the dark library failed
  ///
  /// In en, this message translates to:
  /// **'build the dark library'**
  String get calibrationActionBuildLibrary;

  /// Simple Mode, after Couldn't: a Guide Camera tab action failed
  ///
  /// In en, this message translates to:
  /// **'change the guide camera\'s calibration'**
  String get calibrationActionGuide;

  /// Simple Mode search: words (never shown) that find the Calibration mode; space-separated, lower case
  ///
  /// In en, this message translates to:
  /// **'flats darks bias calibration frames dark library'**
  String get searchCalibrationWords;

  /// Simple Focus panel: the third routine beside Auto and Manual, which measures each filter's focus offset (keep it short, it shares a row)
  ///
  /// In en, this message translates to:
  /// **'Offsets'**
  String get focusOffsets;

  /// Simple Mode: the offset calibration routine's name, on its status pill and its search result
  ///
  /// In en, this message translates to:
  /// **'Focus offsets'**
  String get focusOffsetsTitle;

  /// Simple Mode status pill, Focus offsets: the filter being measured and which autofocus run of how many (e.g. OIII · 4 of 7)
  ///
  /// In en, this message translates to:
  /// **'{filter} · {index} of {count}'**
  String focusOffsetsProgress(String filter, int index, int count);

  /// Simple Focus panel, Offsets: what the routine does, before it starts
  ///
  /// In en, this message translates to:
  /// **'Point at a star field, pick the filters, then Start. Each filter is focused once, the reference before and after.'**
  String get focusOffsetsIntro;

  /// Simple Focus panel, Offsets: which filter the offsets are measured from, before the run
  ///
  /// In en, this message translates to:
  /// **'Offsets are measured from {filter}, the reference.'**
  String focusOffsetsReference(String filter);

  /// Simple Focus panel, Offsets filter picker: the reference filter's chip, always measured
  ///
  /// In en, this message translates to:
  /// **'{filter} · reference'**
  String focusOffsetsReferenceChip(String filter);

  /// Simple Focus panel, Offsets: the filter picker's caption
  ///
  /// In en, this message translates to:
  /// **'Filters to measure'**
  String get focusOffsetsPicker;

  /// Simple Mode shutter in Focus, Offsets: starts the calibration (screen-reader label)
  ///
  /// In en, this message translates to:
  /// **'Calibrate focus offsets'**
  String get focusOffsetsStart;

  /// Simple Mode shutter in Focus, Offsets while it runs: the red square stops it (screen-reader label)
  ///
  /// In en, this message translates to:
  /// **'Stop calibrating focus offsets'**
  String get focusOffsetsCancel;

  /// Simple Focus panel, Offsets: the run's headline while it measures a filter
  ///
  /// In en, this message translates to:
  /// **'Measuring {filter} · {index} of {count}'**
  String focusOffsetsMeasuring(String filter, int index, int count);

  /// Simple Focus panel, Offsets: the headline before the first measurement starts
  ///
  /// In en, this message translates to:
  /// **'Starting…'**
  String get focusOffsetsStarting;

  /// Simple Focus panel, Offsets: why it can't start while a plan runs
  ///
  /// In en, this message translates to:
  /// **'Wait for the plan to finish: calibrating changes filters and focus.'**
  String get focusOffsetsPlanRunning;

  /// Simple Focus panel, Offsets: why it can't start while autofocus, the Bahtinov readout or guide-camera focus runs
  ///
  /// In en, this message translates to:
  /// **'Wait for the other focus routine to finish.'**
  String get focusOffsetsFocusBusy;

  /// Simple Focus panel, Offsets: why it can't start, no camera connected
  ///
  /// In en, this message translates to:
  /// **'Connect the camera to calibrate focus offsets.'**
  String get focusOffsetsNeedsCamera;

  /// Simple Focus panel, Offsets: why it can't start, no focuser connected
  ///
  /// In en, this message translates to:
  /// **'Connect the focuser to calibrate focus offsets.'**
  String get focusOffsetsNeedsFocuser;

  /// Simple Focus panel, Offsets: why it can't start, no filter wheel connected
  ///
  /// In en, this message translates to:
  /// **'Connect the filter wheel to calibrate focus offsets.'**
  String get focusOffsetsNeedsWheel;

  /// Simple Focus panel, Offsets: why it can't start with the manual filter wheel
  ///
  /// In en, this message translates to:
  /// **'Calibrating needs a motorised filter wheel: a manual one can\'t change filters by itself.'**
  String get focusOffsetsNeedsMotorisedWheel;

  /// Simple Focus panel, Offsets: why it can't start with a relative focuser
  ///
  /// In en, this message translates to:
  /// **'Calibrating needs an absolute focuser: a relative one has no positions to compare.'**
  String get focusOffsetsNeedsAbsoluteFocuser;

  /// Simple Focus panel, Offsets: why it can't start with only the reference picked
  ///
  /// In en, this message translates to:
  /// **'Pick at least one filter besides the reference.'**
  String get focusOffsetsPickOne;

  /// Simple Focus panel, Offsets: the finished run's headline
  ///
  /// In en, this message translates to:
  /// **'Offsets measured'**
  String get focusOffsetsResults;

  /// Simple Focus panel, Offsets: the headline after the run was stopped
  ///
  /// In en, this message translates to:
  /// **'Stopped. Your saved offsets are unchanged.'**
  String get focusOffsetsStopped;

  /// Simple Focus panel, Offsets: the headline after the run failed (the reason follows)
  ///
  /// In en, this message translates to:
  /// **'The calibration failed. Your saved offsets are unchanged.'**
  String get focusOffsetsFailed;

  /// Simple Focus panel, Offsets results table: the filter column
  ///
  /// In en, this message translates to:
  /// **'Filter'**
  String get focusOffsetsColFilter;

  /// Simple Focus panel, Offsets results table: the offset column, in focuser steps
  ///
  /// In en, this message translates to:
  /// **'Offset'**
  String get focusOffsetsColOffset;

  /// Simple Focus panel, Offsets results table: the offset saved before this run
  ///
  /// In en, this message translates to:
  /// **'Before'**
  String get focusOffsetsColPrevious;

  /// Simple Focus panel, Offsets results table: a filter whose autofocus failed (the reason follows)
  ///
  /// In en, this message translates to:
  /// **'Failed'**
  String get focusOffsetsRowFailed;

  /// Simple Focus panel, Offsets results table: a filter the run ended before
  ///
  /// In en, this message translates to:
  /// **'Not measured'**
  String get focusOffsetsRowSkipped;

  /// Simple Focus panel, Offsets results table: a filter not reached yet
  ///
  /// In en, this message translates to:
  /// **'Waiting'**
  String get focusOffsetsRowPending;

  /// Simple Focus panel, Offsets results table: a filter whose fit was weak, in the full UI's weak-fit words
  ///
  /// In en, this message translates to:
  /// **'Weak curve fit (R² {r2}): run again if the sky has settled.'**
  String focusOffsetsWeakFit(String r2);

  /// Simple Focus panel, Offsets results: the drift warning
  ///
  /// In en, this message translates to:
  /// **'The reference moved {steps} steps between its two runs, more than its critical focus zone ({cfz} steps). Run it again for steadier numbers.'**
  String focusOffsetsDrift(int steps, int cfz);

  /// Simple Focus panel, Offsets results: the reference's second run failed, so drift was not checked
  ///
  /// In en, this message translates to:
  /// **'Drift not checked: the reference\'s second run didn\'t measure.'**
  String get focusOffsetsDriftNotChecked;

  /// Simple Focus panel, Offsets results: keeps the measured offsets
  ///
  /// In en, this message translates to:
  /// **'Save offsets'**
  String get focusOffsetsSave;

  /// Simple Focus panel, Offsets results: throws the results away, the saved offsets stay
  ///
  /// In en, this message translates to:
  /// **'Discard'**
  String get focusOffsetsDiscard;

  /// Simple Focus panel, Offsets: the hint after Save
  ///
  /// In en, this message translates to:
  /// **'Offsets saved. Autofocus after a filter change can now be turned off.'**
  String get focusOffsetsSaved;

  /// Simple Filter wheel window: the filter in place's saved focus offset, signed (e.g. Offset +42 steps)
  ///
  /// In en, this message translates to:
  /// **'Offset {offset} steps'**
  String deviceFocusOffset(String offset);

  /// Simple Mode search: words (never shown) that find the Focus offsets routine; space-separated, lower case
  ///
  /// In en, this message translates to:
  /// **'focus offsets filter offsets calibrate'**
  String get searchFocusOffsetsWords;

  /// Calibration panel, Flats: where the flats' light comes from (screen reader name of the two segments)
  ///
  /// In en, this message translates to:
  /// **'Light'**
  String get calibrationLightGroup;

  /// Calibration panel, Flats: an even light in front of the scope: a flat panel, a tablet screen or a light box
  ///
  /// In en, this message translates to:
  /// **'Panel or screen'**
  String get calibrationLightFront;

  /// Calibration panel, Flats: the flats wait for twilight and use the sky
  ///
  /// In en, this message translates to:
  /// **'Twilight sky'**
  String get calibrationLightSky;

  /// Calibration panel, Flats: a flat panel is connected, so the run turns it on
  ///
  /// In en, this message translates to:
  /// **'The flat panel lights itself.'**
  String get calibrationLightPanelNote;

  /// Calibration panel, Flats: no panel device, so a hand-held light (a tablet, a light box) is asked for before the flats
  ///
  /// In en, this message translates to:
  /// **'No flat panel connected: the run asks you to put a screen or light box over the scope first.'**
  String get calibrationLightScreenNote;

  /// Calibration panel, Flats: twilight sky flats
  ///
  /// In en, this message translates to:
  /// **'The flats wait for twilight and use the sky.'**
  String get calibrationLightSkyNote;

  /// The calibration run's first step with no flat panel: the question its pill asks before the flats
  ///
  /// In en, this message translates to:
  /// **'Put the screen or light box over the scope, then press Resume to take the flats.'**
  String get calibrationLightPrompt;

  /// Calibration status pill: frames taken of planned in the calibration run
  ///
  /// In en, this message translates to:
  /// **'{done} of {total}'**
  String calibrationPillProgress(int done, int total);

  /// Status pill: the Guide Camera's dark library or bad-pixel map is being built, before the rig reports frames
  ///
  /// In en, this message translates to:
  /// **'Building…'**
  String get calibrationGuideBuilding;

  /// Status pill: the Guide Camera's dark library or bad-pixel map build, frames taken of planned
  ///
  /// In en, this message translates to:
  /// **'Building · {done} of {total}'**
  String calibrationGuideBuildProgress(int done, int total);

  /// Error pill: a Guide Camera build failed on the rig; the guider's reason follows. {what} is the Dark library or the Bad-pixel map
  ///
  /// In en, this message translates to:
  /// **'{what} wasn\'t built'**
  String calibrationGuideBuildFailed(String what);

  /// Result pill: a Guide Camera build finished on the rig
  ///
  /// In en, this message translates to:
  /// **'{what} built'**
  String calibrationGuideBuilt(String what);

  /// Calibration panel, Flats: the section heading over the filter rows, naming the latest plan's target
  ///
  /// In en, this message translates to:
  /// **'Tonight\'s filters · {target}'**
  String calibrationFlatsForSession(String target);

  /// Calibration panel, Guide Camera: the outlined pill of a dark library or bad-pixel map that exists
  ///
  /// In en, this message translates to:
  /// **'On file'**
  String get calibrationOnFileTag;

  /// Calibration panel, Guide Camera: build the Guider's dark library
  ///
  /// In en, this message translates to:
  /// **'Build dark library'**
  String get calibrationGuideBuildDarks;

  /// Calibration panel, Guide Camera: build the Guider's bad-pixel map
  ///
  /// In en, this message translates to:
  /// **'Build bad-pixel map'**
  String get calibrationGuideBuildDefects;

  /// Calibration panel, Guide Camera: the line under the build buttons
  ///
  /// In en, this message translates to:
  /// **'Building asks you to cover the guide scope first.'**
  String get calibrationGuideCoverNote;

  /// Calibration panel, Guide Camera: the dark library or bad-pixel map exists (its row, beside the On file pill)
  ///
  /// In en, this message translates to:
  /// **'Built'**
  String get calibrationGuideReady;

  /// Night done: tonight's lights lack flats; the filters follow under it
  ///
  /// In en, this message translates to:
  /// **'Flats missing'**
  String get calibrationFlatsMissing;

  /// Night done: every filter of tonight's lights has flats
  ///
  /// In en, this message translates to:
  /// **'Flats on file'**
  String get calibrationFlatsOnFile;

  /// Night done: the camera's library has no darks (or bias) matching tonight's lights, e.g. a new exposure, gain or temperature
  ///
  /// In en, this message translates to:
  /// **'Darks missing from the library'**
  String get calibrationDarksMissingFromLibrary;

  /// Calibration chip on Darks & bias: no plan rows to say which darks are needed
  ///
  /// In en, this message translates to:
  /// **'No plan'**
  String get calibrationChipNoPlan;

  /// Calibration chip on Darks & bias: dark sets the plan needs that are older than six months
  ///
  /// In en, this message translates to:
  /// **'{count} old'**
  String calibrationChipOld(int count);

  /// Calibration chip on Guide Camera: the Guider isn't connected
  ///
  /// In en, this message translates to:
  /// **'No guider'**
  String get calibrationChipNoGuider;

  /// Calibration shutter caption on Flats: what the shutter does
  ///
  /// In en, this message translates to:
  /// **'Shoot flats'**
  String get calibrationCapShootFlats;

  /// Calibration shutter caption and button on Darks & bias: builds the darks (by exposure) and/or bias the plan needs
  ///
  /// In en, this message translates to:
  /// **'Build {what}'**
  String calibrationCapBuild(String what);

  /// Calibration shutter caption and button on Darks & bias: shoots old dark sets again
  ///
  /// In en, this message translates to:
  /// **'Refresh {what}'**
  String calibrationCapRefresh(String what);

  /// Calibration panel, Darks & bias: no plan rows, so coverage can't be judged
  ///
  /// In en, this message translates to:
  /// **'Queue a target to see which darks its plan needs.'**
  String get calibrationLibraryNoPlan;

  /// Calibration panel, Flats: shoot the filters that have no flats
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{Shoot 1 missing flat set} other{Shoot {count} missing flat sets}}'**
  String calibrationShootMissingFlats(int count);

  /// Calibration panel, Flats: every filter is covered; shoot all of them again
  ///
  /// In en, this message translates to:
  /// **'Reshoot all'**
  String get calibrationReshootAll;

  /// Calibration panel, Flats: the line under Reshoot all
  ///
  /// In en, this message translates to:
  /// **'Reshoot all keeps what you have and shoots every filter again.'**
  String get calibrationReshootAllNote;

  /// Calibration library: how old a set is, in days, with 'old' (or 'taken today')
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =0{taken today} =1{1 day old} other{{count} days old}}'**
  String calibrationAgeDays(int count);

  /// Calibration library: how old a set is, in weeks
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{1 week old} other{{count} weeks old}}'**
  String calibrationAgeWeeks(int count);

  /// Calibration library: how old a set is, in months
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{1 month old} other{{count} months old}}'**
  String calibrationAgeMonths(int count);

  /// Calibration library: a set's camera gain
  ///
  /// In en, this message translates to:
  /// **'gain {gain}'**
  String calibrationGain(int gain);

  /// Calibration library: a bias set's camera offset
  ///
  /// In en, this message translates to:
  /// **'offset {offset}'**
  String calibrationOffset(int offset);

  /// Calibration panel, Darks & bias: the heading naming the camera the library belongs to
  ///
  /// In en, this message translates to:
  /// **'{camera} · reused for months'**
  String calibrationLibraryCamera(String camera);

  /// Calibration panel, Darks & bias: the library has darks and bias for every exposure the queued plan uses
  ///
  /// In en, this message translates to:
  /// **'Covers your plan'**
  String get calibrationLibraryCovers;

  /// Calibration panel, Darks & bias: the darks (by exposure) and/or bias the library lacks for the queued plan
  ///
  /// In en, this message translates to:
  /// **'Your plan needs {what}'**
  String calibrationLibraryNeeds(String what);

  /// Calibration panel, Darks & bias: the plan is covered by dark sets older than six months
  ///
  /// In en, this message translates to:
  /// **'Covers your plan, but the {what} darks are old'**
  String calibrationLibraryOld(String what);

  /// Calibration panel, Darks & bias: under the old-darks line
  ///
  /// In en, this message translates to:
  /// **'Darks drift as a sensor ages. Refresh them when you can.'**
  String get calibrationLibraryOldNote;

  /// Calibration panel, Darks & bias: the camera's library is empty
  ///
  /// In en, this message translates to:
  /// **'No darks or bias for this camera yet'**
  String get calibrationLibraryEmpty;

  /// Calibration panel, Darks & bias: under the empty-library line
  ///
  /// In en, this message translates to:
  /// **'Build them once on a cloudy night; they last months.'**
  String get calibrationLibraryEmptyNote;

  /// Calibration panel, Darks & bias: no darks (or no bias) in the library
  ///
  /// In en, this message translates to:
  /// **'None yet.'**
  String get calibrationLibraryNone;

  /// Calibration › Darks & bias: how many darks a gain and temperature group holds
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{1 exposure} other{{count} exposures}}'**
  String calibrationDarkGroupCount(int count);

  /// Calibration library row: how old the set is and how many frames it holds
  ///
  /// In en, this message translates to:
  /// **'{age} · {frames, plural, =1{1 frame} other{{frames} frames}}'**
  String calibrationLibraryAge(String age, int frames);

  /// Calibration library: the outlined pill of a dark set older than six months
  ///
  /// In en, this message translates to:
  /// **'Old'**
  String get calibrationOld;

  /// Calibration panel, Darks & bias: the heading over the exposure chips
  ///
  /// In en, this message translates to:
  /// **'Build or refresh'**
  String get calibrationLibraryBuildSection;

  /// Calibration panel, Darks & bias: the settings the library build uses
  ///
  /// In en, this message translates to:
  /// **'At {settings}, from the plan. Bias at the same gain. The run asks for the lens cap first.'**
  String calibrationLibraryNote(String settings);

  /// Calibration panel, Darks & bias: build darks for the exposures picked by hand
  ///
  /// In en, this message translates to:
  /// **'Build picked'**
  String get calibrationBuildPicked;

  /// Plan sheet, Flats row: the target's filters the flats are for
  ///
  /// In en, this message translates to:
  /// **'For {filters}'**
  String planFlatsFor(String filters);

  /// Plan sheet, Flats row: flats for this target's filters right after its last frame (#1757)
  ///
  /// In en, this message translates to:
  /// **'After target'**
  String get planFlatsAfterTarget;

  /// Plan sheet, Flats row: flats for this target's filters after the queue's last target (#1757)
  ///
  /// In en, this message translates to:
  /// **'End of night'**
  String get planFlatsEndOfNight;

  /// Plan sheet, Flats row: what the two choices do
  ///
  /// In en, this message translates to:
  /// **'After target: right after its last frame, at its rotator angle. End of night: after the last target. With no panel the run asks for a screen first.'**
  String get planFlatsHint;

  /// Calibration library row: a set from before the rig recorded which camera took it; it counts for any camera
  ///
  /// In en, this message translates to:
  /// **'camera not recorded'**
  String get calibrationLibraryNoCamera;

  /// Question pill: calibration was added to a queue whose targets haven't started
  ///
  /// In en, this message translates to:
  /// **'The calibration frames are in the queue after your targets. Start the queue now?'**
  String get calibrationStartQueue;

  /// Question pill action: start the queue (targets first, then the calibration frames)
  ///
  /// In en, this message translates to:
  /// **'Start queue'**
  String get calibrationStartQueueNow;

  /// Settings, Imaging defaults: caption above the two Tonight's Sky mosaic size rows; min and max are the allowed tiles per side
  ///
  /// In en, this message translates to:
  /// **'Ranks Tonight\'s Sky framing for a mosaic of this many tiles ({min}–{max} each way). 1×1 is a single frame.'**
  String imagingMosaicHelp(int min, int max);

  /// Settings, Imaging defaults: row label, how many mosaic tiles wide Tonight's Sky ranks the framing for
  ///
  /// In en, this message translates to:
  /// **'Tonight\'s Sky mosaic tiles across'**
  String get imagingMosaicTilesAcross;

  /// Settings, Imaging defaults: row label, how many mosaic tiles tall Tonight's Sky ranks the framing for
  ///
  /// In en, this message translates to:
  /// **'Tonight\'s Sky mosaic tiles down'**
  String get imagingMosaicTilesDown;

  /// Tonight's Sky panel: line under the header when the ranking assumes a mosaic, e.g. Framing for a 2×3 mosaic
  ///
  /// In en, this message translates to:
  /// **'Framing for a {across}×{down} mosaic'**
  String tonightSkyMosaicFraming(int across, int down);

  /// Image library, Tag selected frames dialog: helper text under the Add tags and Remove tags fields (type several tags separated by commas)
  ///
  /// In en, this message translates to:
  /// **'Comma-separated'**
  String get libraryTagsCommaSeparated;

  /// Simple Mode: title of the first-time Rig setup sheet and of the Rig setup header in Settings › Rig
  ///
  /// In en, this message translates to:
  /// **'Rig setup'**
  String get rigSetupTitle;

  /// Simple Mode: one line under the Rig setup title
  ///
  /// In en, this message translates to:
  /// **'Before a first night, Ara needs a few facts about this rig.'**
  String get rigSetupIntro;

  /// Rig setup line: where the rig stands
  ///
  /// In en, this message translates to:
  /// **'Site'**
  String get rigSetupSite;

  /// Rig setup line: what is missing for the site
  ///
  /// In en, this message translates to:
  /// **'Latitude and longitude are not set.'**
  String get rigSetupSiteMissing;

  /// Rig setup: button that opens the place where latitude and longitude are set
  ///
  /// In en, this message translates to:
  /// **'Set site'**
  String get rigSetupSiteAction;

  /// Rig setup line: the sensor and the focal length
  ///
  /// In en, this message translates to:
  /// **'Main camera and telescope'**
  String get rigSetupOptics;

  /// Rig setup line: what is missing for the main camera and telescope
  ///
  /// In en, this message translates to:
  /// **'The sensor or the focal length is not set.'**
  String get rigSetupOpticsMissing;

  /// Rig setup: button that opens the optics panel
  ///
  /// In en, this message translates to:
  /// **'Set up optics'**
  String get rigSetupOpticsAction;

  /// Rig setup line: the guide focal length and the Guide Camera's pixel size
  ///
  /// In en, this message translates to:
  /// **'Guide scope'**
  String get rigSetupGuideScope;

  /// Rig setup line: what is missing for the guide scope
  ///
  /// In en, this message translates to:
  /// **'The guide focal length or the Guide Camera\'s pixel size is not set.'**
  String get rigSetupGuideScopeMissing;

  /// Rig setup: button that opens the guide focal length and pixel size
  ///
  /// In en, this message translates to:
  /// **'Set guide scope'**
  String get rigSetupGuideScopeAction;

  /// Rig setup line: the Guider's calibration
  ///
  /// In en, this message translates to:
  /// **'Guider calibration'**
  String get rigSetupCalibration;

  /// Rig setup line: what is missing for the Guider calibration
  ///
  /// In en, this message translates to:
  /// **'Not calibrated yet, or Ara says to calibrate again.'**
  String get rigSetupCalibrationMissing;

  /// Rig setup line: the Guider is not connected, so its calibration cannot be checked
  ///
  /// In en, this message translates to:
  /// **'Connect Camera (Guide) in Settings › Devices to check the guider calibration.'**
  String get rigSetupCalibrationConnect;

  /// Rig setup: button that closes the sheet and shows Guider mode
  ///
  /// In en, this message translates to:
  /// **'Open Guider mode'**
  String get rigSetupCalibrationAction;

  /// Rig setup: what a screen reader says for a line with a tick
  ///
  /// In en, this message translates to:
  /// **'Done'**
  String get rigSetupItemDone;

  /// Rig setup: closes the sheet and keeps it closed for this rig on this device
  ///
  /// In en, this message translates to:
  /// **'Not now'**
  String get rigSetupNotNow;

  /// Rig setup sheet footer and Settings › Rig header: how many of the items are done
  ///
  /// In en, this message translates to:
  /// **'{done} of {total} done'**
  String rigSetupProgress(int done, int total);

  /// Settings › Rig header: the next missing Rig setup item, by its line's name (Site, Guide scope)
  ///
  /// In en, this message translates to:
  /// **'Next: {item}'**
  String rigSetupNext(String item);

  /// Simple Mode result pill when the last Rig setup item is done
  ///
  /// In en, this message translates to:
  /// **'Rig set up'**
  String get rigSetupDonePill;

  /// Fault log: how many times one device dropped within one run of faults, shown on the collapsed row
  ///
  /// In en, this message translates to:
  /// **'{count} times'**
  String faultGroupCount(int count);

  /// Fault log: first and last time (clock strings) of the collapsed row
  ///
  /// In en, this message translates to:
  /// **'First {first} · last {last}'**
  String faultGroupSpan(String first, String last);

  /// Fault log: how many rows of the collapsed row are not resolved yet
  ///
  /// In en, this message translates to:
  /// **'{count} unresolved'**
  String faultGroupUnresolved(int count);

  /// Fault log: every row of the collapsed row is resolved and the newest one recovered
  ///
  /// In en, this message translates to:
  /// **'recovered'**
  String get faultGroupRecovered;

  /// Fault log: every row of the collapsed row is resolved and the newest one was released because the user disconnected the device by hand, so it is not recovered
  ///
  /// In en, this message translates to:
  /// **'released — disconnected by hand'**
  String get faultGroupReleased;

  /// Settings › Focuser: title of the section with Set zero and Calibrate travel
  ///
  /// In en, this message translates to:
  /// **'Position'**
  String get focuserPosition;

  /// Settings › Focuser: the row and confirm button that make the focuser's current position 0
  ///
  /// In en, this message translates to:
  /// **'Set zero'**
  String get focuserSetZero;

  /// Settings › Focuser: button that asks before making the current position 0
  ///
  /// In en, this message translates to:
  /// **'Set zero…'**
  String get focuserSetZeroButton;

  /// Settings › Focuser: what Set zero does
  ///
  /// In en, this message translates to:
  /// **'Make where it is now 0. The focuser doesn\'t move.'**
  String get focuserSetZeroHint;

  /// Settings › Focuser: the question before Set zero
  ///
  /// In en, this message translates to:
  /// **'Make {position} the new 0? The last autofocus position and plan steps that move to a set position will be off by {position} steps until the next autofocus. Filter offsets stay as they are.'**
  String focuserSetZeroAsk(String position);

  /// Settings › Focuser: the row that runs the focuser to both stops to find its travel
  ///
  /// In en, this message translates to:
  /// **'Calibrate travel'**
  String get focuserCalibrate;

  /// Settings › Focuser: button that starts a travel calibration
  ///
  /// In en, this message translates to:
  /// **'Calibrate'**
  String get focuserCalibrateButton;

  /// Settings › Focuser: what Calibrate travel does, before it runs
  ///
  /// In en, this message translates to:
  /// **'The focuser runs to both stops and sets 0 at the inner one. Take off anything that could touch first.'**
  String get focuserCalibrateHint;

  /// Settings › Focuser: a travel calibration is running
  ///
  /// In en, this message translates to:
  /// **'Running to both stops…'**
  String get focuserCalibrating;

  /// Settings › Focuser: the focuser's travel is calibrated
  ///
  /// In en, this message translates to:
  /// **'Calibrated'**
  String get focuserCalibrated;

  /// Error when the focuser refused Set zero
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t set the focuser\'s zero'**
  String get focuserCouldntSetZero;

  /// Error when the focuser refused to start or stop a calibration
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t calibrate the focuser'**
  String get focuserCouldntCalibrate;

  /// Simple Mode: shown while the Sky Map draws its first sky after it was asked for
  ///
  /// In en, this message translates to:
  /// **'Loading the sky…'**
  String get shellLoadingSky;

  /// Stats dashboard: under the total integration
  ///
  /// In en, this message translates to:
  /// **'{lights} light frames in {sessions} sessions'**
  String statsIntegrationCaption(int lights, int sessions);

  /// Stats: targets counted from frames that recorded a target name
  ///
  /// In en, this message translates to:
  /// **'Named targets'**
  String get statsNamedTargets;

  /// Stats: caption under a badge the user has earned
  ///
  /// In en, this message translates to:
  /// **'Earned'**
  String get statsBadgeEarned;

  /// Settings panel: a rig-held section (ADR 0015) is still loading
  ///
  /// In en, this message translates to:
  /// **'Loading these settings from your rig…'**
  String get settingsSectionLoading;

  /// Settings panel: a rig-held section failed to load; a friendlyError sentence follows
  ///
  /// In en, this message translates to:
  /// **'Not loaded — these settings are on your rig and it did not answer.'**
  String get settingsSectionNotLoaded;

  /// Settings panel: button that loads a rig-held section again
  ///
  /// In en, this message translates to:
  /// **'Retry'**
  String get settingsSectionRetry;

  /// Settings panel: a save was refused because another device saved a newer copy
  ///
  /// In en, this message translates to:
  /// **'These settings were changed on another device. Nothing was saved. Reload to see the new values (your edits here will be replaced).'**
  String get settingsSectionConflict;

  /// Settings panel: button that replaces local edits with the rig's newer copy
  ///
  /// In en, this message translates to:
  /// **'Reload'**
  String get settingsSectionReload;

  /// Companion Mode question pill: offers the phone's location and time to a rig with no recent time sync
  ///
  /// In en, this message translates to:
  /// **'The rig has no clock or site yet. Send this phone\'s location and time?'**
  String get companionTimeOfferQuestion;

  /// Companion Mode time offer pill: decline until the next session
  ///
  /// In en, this message translates to:
  /// **'Not now'**
  String get companionTimeOfferNotNow;

  /// Companion Mode time offer pill: send the phone's location and time
  ///
  /// In en, this message translates to:
  /// **'Send'**
  String get companionTimeOfferSend;

  /// Companion Mode result pill: the phone's location and time reached the rig
  ///
  /// In en, this message translates to:
  /// **'Sent this phone\'s location and time'**
  String get companionTimeOfferSent;

  /// Companion Mode result pill title: no location permission or fix on the phone; the detail says why
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t get this phone\'s location'**
  String get companionTimeOfferNoGps;

  /// Safety alarm on a phone: says the alarm uses sound and vibration
  ///
  /// In en, this message translates to:
  /// **'This phone sounds and vibrates until you silence the alarm.'**
  String get safetyAlarmPhoneNote;

  /// Safety alarm on a phone: the hold-to-confirm abort button, same hold and fill as STOP
  ///
  /// In en, this message translates to:
  /// **'Emergency Abort'**
  String get safetyAlarmEmergencyAbort;

  /// First-run screen: app bar title
  ///
  /// In en, this message translates to:
  /// **'Connect to OpenAstro Ara'**
  String get firstRunTitle;

  /// First-run screen: tooltip of the refresh button
  ///
  /// In en, this message translates to:
  /// **'Rescan for servers'**
  String get firstRunRescanTooltip;

  /// First-run screen: heading above the discovered servers
  ///
  /// In en, this message translates to:
  /// **'Rigs found on your network'**
  String get firstRunRigsFound;

  /// First-run screen: shown while no server is discovered yet
  ///
  /// In en, this message translates to:
  /// **'Looking for servers… rescanning every few seconds.\nIf yours doesn’t appear, pull down, tap ⟳ (top right) or add it manually below.'**
  String get firstRunLooking;

  /// First-run screen: heading above the manual host and port fields
  ///
  /// In en, this message translates to:
  /// **'Or add manually:'**
  String get firstRunAddManually;

  /// First-run screen: manual host field label
  ///
  /// In en, this message translates to:
  /// **'Hostname or IP'**
  String get firstRunHostLabel;

  /// First-run screen: manual port field label
  ///
  /// In en, this message translates to:
  /// **'Port'**
  String get firstRunPortLabel;

  /// First-run screen: button that picks the manually typed server
  ///
  /// In en, this message translates to:
  /// **'Use'**
  String get firstRunUse;

  /// First-run screen: button that enters the app without a server
  ///
  /// In en, this message translates to:
  /// **'Plan offline — set up your night without a server'**
  String get firstRunPlanOffline;

  /// First-run screen: server handshake succeeded
  ///
  /// In en, this message translates to:
  /// **'Connected to {name}'**
  String firstRunConnectedTo(String name);

  /// First-run screen: server version line
  ///
  /// In en, this message translates to:
  /// **'Server version: {version}'**
  String firstRunServerVersion(String version);

  /// First-run screen: API version line
  ///
  /// In en, this message translates to:
  /// **'API version: {version}'**
  String firstRunApiVersion(String version);

  /// First-run screen: server address line
  ///
  /// In en, this message translates to:
  /// **'Address: {address}'**
  String firstRunAddress(String address);

  /// First-run screen: button that confirms the connected server
  ///
  /// In en, this message translates to:
  /// **'Continue'**
  String get firstRunContinue;

  /// First-run screen: handshake in progress
  ///
  /// In en, this message translates to:
  /// **'Connecting to {address}…'**
  String firstRunConnecting(String address);

  /// First-run screen: handshake failed
  ///
  /// In en, this message translates to:
  /// **'Connection failed: {address}'**
  String firstRunConnectionFailed(String address);

  /// Launch profile screen: heading (product name)
  ///
  /// In en, this message translates to:
  /// **'OpenAstro Ara'**
  String get launchTitle;

  /// Launch profile screen: names the chosen rig
  ///
  /// In en, this message translates to:
  /// **'Rig: {name}'**
  String launchRig(String name);

  /// Launch profile screen: button back to the server chooser
  ///
  /// In en, this message translates to:
  /// **'Choose a different rig'**
  String get launchChooseRig;

  /// Launch profile screen: reload the profile list
  ///
  /// In en, this message translates to:
  /// **'Retry'**
  String get launchRetry;

  /// Launch profile screen: divider between the profile list and Add / Import
  ///
  /// In en, this message translates to:
  /// **'or'**
  String get launchOr;

  /// Launch profile screen: opens the setup wizard
  ///
  /// In en, this message translates to:
  /// **'Add a Profile'**
  String get launchAddProfile;

  /// Launch profile screen: imports a profile file
  ///
  /// In en, this message translates to:
  /// **'Import Profile'**
  String get launchImportProfile;

  /// Launch profile screen: shown when the rig has no profile
  ///
  /// In en, this message translates to:
  /// **'No profiles yet — add or import one to get started.'**
  String get launchNoProfiles;

  /// Launch profile screen: label above the profile dropdown
  ///
  /// In en, this message translates to:
  /// **'Active profile:'**
  String get launchActiveProfile;

  /// Launch profile screen: tooltip of the delete button
  ///
  /// In en, this message translates to:
  /// **'Delete \"{name}\"'**
  String launchDeleteProfile(String name);

  /// Launch profile screen: button that opens the shell with the picked profile
  ///
  /// In en, this message translates to:
  /// **'Image'**
  String get launchImage;

  /// Offline launch screen: heading (product name)
  ///
  /// In en, this message translates to:
  /// **'OpenAstro Ara'**
  String get offlineTitle;

  /// Offline launch screen: subtitle
  ///
  /// In en, this message translates to:
  /// **'Planning offline — no server connected'**
  String get offlineSubtitle;

  /// Offline launch screen: shown when no profile is cached
  ///
  /// In en, this message translates to:
  /// **'No profiles are cached on this device — offline planning needs one. Connect to your server once and set up a profile first.'**
  String get offlineNoProfiles;

  /// Offline launch screen: button that leaves offline mode
  ///
  /// In en, this message translates to:
  /// **'Back to server setup'**
  String get offlineBackToSetup;

  /// Offline launch screen: label above the profile dropdown
  ///
  /// In en, this message translates to:
  /// **'Plan with profile:'**
  String get offlinePlanWithProfile;

  /// Offline launch screen: dropdown entry for a profile with an empty name
  ///
  /// In en, this message translates to:
  /// **'(unnamed profile)'**
  String get offlineUnnamedProfile;

  /// Offline launch screen: the picked profile has no cached gear sections
  ///
  /// In en, this message translates to:
  /// **'This profile\'s gear settings haven\'t been cached yet (it was never active while connected) — planning will use defaults.'**
  String get offlineNotCached;

  /// Offline launch screen: when the gear settings were cached; age is one of the offlineAge* phrases
  ///
  /// In en, this message translates to:
  /// **'Gear cached {age} — reconnect to refresh if your setup changed.'**
  String offlineGearCached(String age);

  /// Offline launch screen: cache age under an hour, fills {age}
  ///
  /// In en, this message translates to:
  /// **'just now'**
  String get offlineAgeJustNow;

  /// Offline launch screen: cache age in hours, fills {age}
  ///
  /// In en, this message translates to:
  /// **'{hours} h ago'**
  String offlineAgeHours(int hours);

  /// Offline launch screen: cache age in days, fills {age}
  ///
  /// In en, this message translates to:
  /// **'{days} days ago'**
  String offlineAgeDays(int days);

  /// Offline launch screen: button that enters the shell with the picked profile
  ///
  /// In en, this message translates to:
  /// **'Plan'**
  String get offlinePlan;

  /// App shell: navigation tab
  ///
  /// In en, this message translates to:
  /// **'Planning'**
  String get appShellTabPlanning;

  /// App shell: navigation tab
  ///
  /// In en, this message translates to:
  /// **'Checklist'**
  String get appShellTabChecklist;

  /// App shell: navigation tab
  ///
  /// In en, this message translates to:
  /// **'Sequence'**
  String get appShellTabSequence;

  /// App shell: navigation tab
  ///
  /// In en, this message translates to:
  /// **'Live'**
  String get appShellTabLive;

  /// App shell: navigation tab and the top-bar gear
  ///
  /// In en, this message translates to:
  /// **'Settings'**
  String get appShellTabSettings;

  /// App shell: tooltip of the search button; shortcut is the key combination
  ///
  /// In en, this message translates to:
  /// **'Search settings ({shortcut})'**
  String appShellSearchSettings(String shortcut);

  /// App shell: tooltip of the night mode button while on
  ///
  /// In en, this message translates to:
  /// **'Night mode on ({shortcut})'**
  String appShellNightModeOnTip(String shortcut);

  /// App shell: tooltip of the night mode button while off
  ///
  /// In en, this message translates to:
  /// **'Night mode ({shortcut})'**
  String appShellNightModeTip(String shortcut);

  /// App shell: tooltip of the help button
  ///
  /// In en, this message translates to:
  /// **'Help & report a bug'**
  String get appShellHelp;

  /// App shell: status bar launcher
  ///
  /// In en, this message translates to:
  /// **'Image Library'**
  String get appShellLauncherLibrary;

  /// App shell: status bar launcher
  ///
  /// In en, this message translates to:
  /// **'Calibration'**
  String get appShellLauncherCalibration;

  /// App shell: status bar launcher that returns to the launch screen
  ///
  /// In en, this message translates to:
  /// **'Launchpad'**
  String get appShellLauncherLaunchpad;

  /// App shell: status bar launcher that opens the profile wizard
  ///
  /// In en, this message translates to:
  /// **'Run profile wizard'**
  String get appShellLauncherWizard;

  /// Wizard: developer fallback when a step has no screen
  ///
  /// In en, this message translates to:
  /// **'Missing screen builder for step {step}'**
  String wizardMissingScreen(int step);

  /// Wizard: app bar title
  ///
  /// In en, this message translates to:
  /// **'Set up profile · Step {step} of {total}'**
  String wizardTitle(int step, int total);

  /// Wizard: app bar button that saves a partial profile and leaves
  ///
  /// In en, this message translates to:
  /// **'Save & Exit'**
  String get wizardSaveExit;

  /// Wizard: snack bar after saving when some follow-ups degraded; notes are the joined follow-up sentences
  ///
  /// In en, this message translates to:
  /// **'Profile saved. {notes}'**
  String wizardSavedWithNotes(String notes);

  /// Wizard: snack bar after saving a profile offline
  ///
  /// In en, this message translates to:
  /// **'\"{name}\" is saved on this device. It will be applied to your rig the next time you connect.'**
  String wizardSavedLocally(String name);

  /// Wizard: last-step button
  ///
  /// In en, this message translates to:
  /// **'Save Profile'**
  String get wizardSaveProfile;

  /// Wizard: next-step button
  ///
  /// In en, this message translates to:
  /// **'Next'**
  String get wizardNext;

  /// Wizard: previous-step button
  ///
  /// In en, this message translates to:
  /// **'Back'**
  String get wizardBack;

  /// Wizard: button that skips a step and keeps defaults
  ///
  /// In en, this message translates to:
  /// **'Skip — use defaults'**
  String get wizardSkipDefaults;

  /// Wizard: tooltip on the disabled Skip button
  ///
  /// In en, this message translates to:
  /// **'Resolve the issue on this screen to continue.'**
  String get wizardSkipBlocked;

  /// Wizard: tooltip on the disabled Next button
  ///
  /// In en, this message translates to:
  /// **'Fix the highlighted field to continue.'**
  String get wizardNextBlocked;

  /// Wizard done view: app bar title
  ///
  /// In en, this message translates to:
  /// **'Profile saved'**
  String get wizardDoneTitle;

  /// Wizard done view: heading
  ///
  /// In en, this message translates to:
  /// **'You\'re all set'**
  String get wizardDoneHeading;

  /// Wizard done view: explains the dark library build
  ///
  /// In en, this message translates to:
  /// **'Your profile is saved and active. The dark library is shooting in the background — it finishes on the server even if you leave now.'**
  String get wizardDoneBody;

  /// Wizard done view: closes the wizard
  ///
  /// In en, this message translates to:
  /// **'Finish'**
  String get wizardDoneFinish;

  /// Wizard: stage line above each step; label is the stage name
  ///
  /// In en, this message translates to:
  /// **'Stage {stage} — {label}'**
  String wizardKitStage(int stage, String label);

  /// Wizard: banner on a skipped step
  ///
  /// In en, this message translates to:
  /// **'This screen was skipped — defaults will fill in until you review it in Settings.'**
  String get wizardKitSkipped;

  /// Wizard: tooltip of the clear button on an optional field
  ///
  /// In en, this message translates to:
  /// **'Reset to default on Save'**
  String get wizardKitResetOnSave;

  /// Wizard step 1: introduction
  ///
  /// In en, this message translates to:
  /// **'Name this profile and (optionally) set your observing site. Everything here can be changed later in Settings.'**
  String get wizardBasicsIntro;

  /// Wizard step 1: heads-up about powering on and configuring gear first
  ///
  /// In en, this message translates to:
  /// **'Before you begin: power on your mount and the rest of your gear, make sure they\'re reachable on the same local network as the server, and that every device is already configured in AlpacaBridge — the wizard discovers equipment live in the next steps and can only find what AlpacaBridge exposes.'**
  String get wizardBasicsBefore;

  /// Wizard step 1: field label
  ///
  /// In en, this message translates to:
  /// **'Profile name'**
  String get wizardBasicsProfileName;

  /// Wizard step 1: example for the profile name field
  ///
  /// In en, this message translates to:
  /// **'e.g. Backyard New Mexico'**
  String get wizardBasicsProfileNameHint;

  /// Wizard step 1: field label
  ///
  /// In en, this message translates to:
  /// **'Site name'**
  String get wizardBasicsSiteName;

  /// Wizard step 1: example for the site name field
  ///
  /// In en, this message translates to:
  /// **'e.g. New Mexico USA'**
  String get wizardBasicsSiteNameHint;

  /// Wizard step 1: section header
  ///
  /// In en, this message translates to:
  /// **'Location (optional)'**
  String get wizardBasicsLocation;

  /// Wizard step 1: button that fills the site from a GPS
  ///
  /// In en, this message translates to:
  /// **'Fill from GPS'**
  String get wizardBasicsFillGps;

  /// Wizard step 1: how to get a GPS fix
  ///
  /// In en, this message translates to:
  /// **'Tip: plug a USB GPS dongle into this computer and turn on Settings → Site → GPS on this computer. No mount GPS needed.'**
  String get wizardBasicsGpsTip;

  /// Wizard step 1: GPS result discarded because the user typed meanwhile
  ///
  /// In en, this message translates to:
  /// **'A location was found, but you edited the site fields while it was being looked up — not overwritten. Press Fill from GPS again to apply it.'**
  String get wizardBasicsGpsEdited;

  /// Wizard step 1: GPS fill done; source names where the fix came from
  ///
  /// In en, this message translates to:
  /// **'Filled from {source}.'**
  String wizardBasicsGpsFilled(String source);

  /// Wizard step 1: field label
  ///
  /// In en, this message translates to:
  /// **'Latitude (°)'**
  String get wizardBasicsLatitude;

  /// Wizard step 1: latitude helper
  ///
  /// In en, this message translates to:
  /// **'North positive.'**
  String get wizardBasicsNorthPositive;

  /// Wizard step 1: field label
  ///
  /// In en, this message translates to:
  /// **'Longitude (°)'**
  String get wizardBasicsLongitude;

  /// Wizard step 1: longitude helper
  ///
  /// In en, this message translates to:
  /// **'East positive.'**
  String get wizardBasicsEastPositive;

  /// Wizard step 1: field label
  ///
  /// In en, this message translates to:
  /// **'Altitude (m)'**
  String get wizardBasicsAltitude;

  /// Wizard step 1: field label
  ///
  /// In en, this message translates to:
  /// **'Bortle class (1-9)'**
  String get wizardBasicsBortle;

  /// Wizard step 1: Bortle helper
  ///
  /// In en, this message translates to:
  /// **'Sky darkness: 1 darkest, 9 inner city. Used as the sky brightness.'**
  String get wizardBasicsBortleHelper;

  /// Wizard step 1: Bortle validation error
  ///
  /// In en, this message translates to:
  /// **'Enter a whole number from 1 to 9.'**
  String get wizardBasicsBortleError;

  /// Wizard step 1: field label
  ///
  /// In en, this message translates to:
  /// **'SQM reading (mag/arcsec²)'**
  String get wizardBasicsSqm;

  /// Wizard step 1: SQM helper
  ///
  /// In en, this message translates to:
  /// **'Sky brightness from a meter or lightpollutionmap, 16 to 22.5. Derived Bortle class: {bortle}.'**
  String wizardBasicsSqmHelper(int bortle);

  /// Wizard step 1 and Settings › Safety › Site: sky brightness mode selector, Bortle
  ///
  /// In en, this message translates to:
  /// **'Bortle class'**
  String get wizardBasicsSkyBortle;

  /// Wizard step 1 and Settings › Safety › Site: sky brightness mode selector, SQM
  ///
  /// In en, this message translates to:
  /// **'SQM reading'**
  String get wizardBasicsSkySqm;

  /// Settings › Safety › Site: the Bortle class derived from the SQM reading, on a line under the SQM field
  ///
  /// In en, this message translates to:
  /// **'Derived Bortle class: {bortle}'**
  String sitePanelDerivedBortle(int bortle);

  /// Wizard step 1: SQM validation error
  ///
  /// In en, this message translates to:
  /// **'Enter a value from 16 to 22.5.'**
  String get wizardBasicsSqmError;

  /// Wizard step 1: field label
  ///
  /// In en, this message translates to:
  /// **'Timezone'**
  String get wizardBasicsTimezone;

  /// Wizard step 1: example for the timezone field
  ///
  /// In en, this message translates to:
  /// **'e.g. America/Denver'**
  String get wizardBasicsTimezoneHint;

  /// Wizard step 1: timezone helper
  ///
  /// In en, this message translates to:
  /// **'Filled in automatically from the coordinates — only change it to view a remote rig in another zone.'**
  String get wizardBasicsTimezoneHelper;

  /// Wizard step 5: introduction
  ///
  /// In en, this message translates to:
  /// **'Review your profile. Tap Edit on any section to jump back and change it, then choose Save Profile below to finish. Anything left \"Not set\" keeps the built-in default — you can adjust it later in Settings.'**
  String get wizardReviewIntro;

  /// Wizard step 5: review section title
  ///
  /// In en, this message translates to:
  /// **'Profile basics'**
  String get wizardReviewSecBasics;

  /// Wizard step 5: review section title
  ///
  /// In en, this message translates to:
  /// **'Connection'**
  String get wizardReviewSecConnection;

  /// Wizard step 5: review section title
  ///
  /// In en, this message translates to:
  /// **'Equipment'**
  String get wizardReviewSecEquipment;

  /// Wizard step 5: review section title
  ///
  /// In en, this message translates to:
  /// **'Telescope'**
  String get wizardReviewSecTelescope;

  /// Wizard step 5: review section title
  ///
  /// In en, this message translates to:
  /// **'Camera'**
  String get wizardReviewSecCamera;

  /// Wizard step 5: review section title
  ///
  /// In en, this message translates to:
  /// **'Filter wheel'**
  String get wizardReviewSecFilterWheel;

  /// Wizard step 5: review section title
  ///
  /// In en, this message translates to:
  /// **'Focuser'**
  String get wizardReviewSecFocuser;

  /// Wizard step 5: review section title
  ///
  /// In en, this message translates to:
  /// **'Mount'**
  String get wizardReviewSecMount;

  /// Wizard step 5: review section title
  ///
  /// In en, this message translates to:
  /// **'Rotator'**
  String get wizardReviewSecRotator;

  /// Wizard step 5: review section title
  ///
  /// In en, this message translates to:
  /// **'OpenAstro Guider'**
  String get wizardReviewSecGuider;

  /// Wizard step 5: review section title
  ///
  /// In en, this message translates to:
  /// **'Plate solving'**
  String get wizardReviewSecPlateSolving;

  /// Wizard step 5: review section title
  ///
  /// In en, this message translates to:
  /// **'Autofocus'**
  String get wizardReviewSecAutofocus;

  /// Wizard step 5: review section title
  ///
  /// In en, this message translates to:
  /// **'File saving'**
  String get wizardReviewSecFileSaving;

  /// Wizard step 5: review section title
  ///
  /// In en, this message translates to:
  /// **'Imaging defaults'**
  String get wizardReviewSecImaging;

  /// Wizard step 5: review section title
  ///
  /// In en, this message translates to:
  /// **'Safety'**
  String get wizardReviewSecSafety;

  /// Wizard step 5: review section title
  ///
  /// In en, this message translates to:
  /// **'Site preferences'**
  String get wizardReviewSecSite;

  /// Wizard step 5: button that jumps back to a section
  ///
  /// In en, this message translates to:
  /// **'Edit'**
  String get wizardReviewEdit;

  /// Wizard step 5: shown instead of Edit for sections with no wizard screen
  ///
  /// In en, this message translates to:
  /// **'Defaults · edit later in Settings'**
  String get wizardReviewDefaultsLater;

  /// Wizard step 5: a skipped section
  ///
  /// In en, this message translates to:
  /// **'Skipped — defaults will apply.'**
  String get wizardReviewSkipped;

  /// Wizard step 5: solver database block title
  ///
  /// In en, this message translates to:
  /// **'Plate-solver star database'**
  String get wizardReviewSolverTitle;

  /// Wizard step 5: reloads the solver database recommendation
  ///
  /// In en, this message translates to:
  /// **'Recheck'**
  String get wizardReviewRecheck;

  /// Wizard step 5: solver database loading
  ///
  /// In en, this message translates to:
  /// **'Working out the field of view…'**
  String get wizardReviewSolverWorking;

  /// Wizard step 5: checkbox to queue the solver database download
  ///
  /// In en, this message translates to:
  /// **'Download after setup ({size}, the rig needs internet once)'**
  String wizardReviewSolverDownload(String size);

  /// Wizard step 5: explains the solver database download
  ///
  /// In en, this message translates to:
  /// **'Runs on the rig in the background; progress in Settings → Sky data.'**
  String get wizardReviewSolverDownloadNote;

  /// Wizard step 5: a camera whose field is too narrow; field is the field-of-view phrase, reason comes from the server
  ///
  /// In en, this message translates to:
  /// **'{field} — cannot plate-solve. {reason}'**
  String wizardReviewSolverTooNarrow(String field, String reason);

  /// Wizard step 5: solver database row role
  ///
  /// In en, this message translates to:
  /// **'Guide camera'**
  String get wizardReviewRoleGuideCamera;

  /// Wizard step 5: solver database row role
  ///
  /// In en, this message translates to:
  /// **'Camera'**
  String get wizardReviewRoleCamera;

  /// Wizard step 5: solver database row field of view; band is the server's band name
  ///
  /// In en, this message translates to:
  /// **'{role}: {fov}° field ({band})'**
  String wizardReviewSolverField(String role, String fov, String band);

  /// Wizard step 5: the solver database package is installed
  ///
  /// In en, this message translates to:
  /// **'already on the rig'**
  String get wizardReviewAlreadyOnRig;

  /// Wizard step 2: introduction
  ///
  /// In en, this message translates to:
  /// **'ARA speaks ASCOM Alpaca only. INDI/INDIGO users connect through a bridge (AlpacaPi, INDIGO Sky\'s -A Alpaca server). Leave the address blank to let Ara auto-discover devices over UDP.'**
  String get wizardDiscoveryIntro;

  /// Wizard step 2: field label
  ///
  /// In en, this message translates to:
  /// **'AlpacaBridge address'**
  String get wizardDiscoveryAddressLabel;

  /// Wizard step 2: hint of the address field
  ///
  /// In en, this message translates to:
  /// **'auto-discover (UDP 32227) — or host:port to override'**
  String get wizardDiscoveryAddressHint;

  /// Wizard step 2: button label while detecting
  ///
  /// In en, this message translates to:
  /// **'Detecting…'**
  String get wizardDiscoveryDetecting;

  /// Wizard step 2: button that runs the detection again
  ///
  /// In en, this message translates to:
  /// **'Retry detection'**
  String get wizardDiscoveryRetryDetection;

  /// Wizard step 2: failure heading
  ///
  /// In en, this message translates to:
  /// **'AlpacaBridge not detected.'**
  String get wizardDiscoveryNotDetected;

  /// Wizard step 2: failure explanation, followed by an install command
  ///
  /// In en, this message translates to:
  /// **'AlpacaBridge is ARA\'s equipment hub. It should have been installed alongside ARA Core via apt. If it wasn\'t, install it on Ara host, then retry:'**
  String get wizardDiscoveryNotDetectedBody;

  /// Wizard step 2: raw detection result
  ///
  /// In en, this message translates to:
  /// **'Details: {details}'**
  String wizardDiscoveryDetails(String details);

  /// Wizard step 2: tooltip of the skip button when an address is typed
  ///
  /// In en, this message translates to:
  /// **'Continue with the address above; detection is skipped.'**
  String get wizardDiscoverySkipTipOn;

  /// Wizard step 2: tooltip of the skip button when no address is typed
  ///
  /// In en, this message translates to:
  /// **'Enter your bridge\'s host:port above to enable.'**
  String get wizardDiscoverySkipTipOff;

  /// Wizard step 2: skip button label after the skip
  ///
  /// In en, this message translates to:
  /// **'Continuing with the address override.'**
  String get wizardDiscoverySkipped;

  /// Wizard step 2: skip button label
  ///
  /// In en, this message translates to:
  /// **'Skip — I\'m using a non-standard bridge address'**
  String get wizardDiscoverySkip;

  /// Device sheet: title; slot is the device type name
  ///
  /// In en, this message translates to:
  /// **'Choose {slot}'**
  String wizardDiscoveryChoose(String slot);

  /// Device sheet: refresh button
  ///
  /// In en, this message translates to:
  /// **'Re-scan'**
  String get wizardDiscoveryRescan;

  /// Device sheet: entry that clears the device
  ///
  /// In en, this message translates to:
  /// **'— None'**
  String get wizardDiscoveryNone;

  /// Device sheet: explains the None entry
  ///
  /// In en, this message translates to:
  /// **'Don\'t use this device type'**
  String get wizardDiscoveryNoneNote;

  /// Device sheet: error title
  ///
  /// In en, this message translates to:
  /// **'Discovery failed'**
  String get wizardDiscoveryFailed;

  /// Device sheet: empty title
  ///
  /// In en, this message translates to:
  /// **'No devices found'**
  String get wizardDiscoveryNoDevices;

  /// Device sheet: empty explanation
  ///
  /// In en, this message translates to:
  /// **'Make sure the driver is running and reachable on Ara\'s subnet, then re-scan.'**
  String get wizardDiscoveryNoDevicesDetail;

  /// Device sheet: second line of a discovered device; address is scheme://host:port
  ///
  /// In en, this message translates to:
  /// **'{address} ·  device #{number}\nAdvertised — connectivity is verified when you connect'**
  String wizardDiscoveryDeviceLine(String address, int number);

  /// Plan sheet caption above the last targets imaged or searched (uppercase)
  ///
  /// In en, this message translates to:
  /// **'RECENT'**
  String get planRecent;

  /// Recent target that never clears 30° tonight (second line, lower case)
  ///
  /// In en, this message translates to:
  /// **'not up tonight'**
  String get planNotUpTonight;

  /// Picked target card: the target never reaches 30° before dawn
  ///
  /// In en, this message translates to:
  /// **'Not above 30° tonight'**
  String get planNotAbove30;

  /// Picked target card: hours from now to dawn the target is above 30°
  ///
  /// In en, this message translates to:
  /// **'{hours} h left above 30°'**
  String planHoursLeft(String hours);

  /// Plan warning: only this many of the typed frames fit before the target drops below 30° or dawn
  ///
  /// In en, this message translates to:
  /// **'Fits {fits} of them before 30°/dawn'**
  String planFramesFit(int fits);

  /// Simple Mode Polar align stepper: the rough step
  ///
  /// In en, this message translates to:
  /// **'Rough'**
  String get polarStepRough;

  /// Simple Mode Polar align stepper: the fine step
  ///
  /// In en, this message translates to:
  /// **'Fine'**
  String get polarStepFine;

  /// Simple Mode Polar align stepper: the verify step
  ///
  /// In en, this message translates to:
  /// **'Check'**
  String get polarStepVerify;

  /// Simple Mode Polar align panel: heading over the routine's settings
  ///
  /// In en, this message translates to:
  /// **'Setup'**
  String get polarSetup;

  /// Simple Mode Polar align panel: heading over the bullseye and the turns
  ///
  /// In en, this message translates to:
  /// **'Error'**
  String get polarErrorHeading;

  /// Simple Mode Polar align rough step: caption once the error is inside the profile's tolerance
  ///
  /// In en, this message translates to:
  /// **'Inside your {tolerance}′ tolerance. Done keeps this alignment; stop and start again to refine it with the guide camera.'**
  String polarInsideTolerance(String tolerance);

  /// Simple Mode Polar align rough step: caption while the error is outside the profile's tolerance
  ///
  /// In en, this message translates to:
  /// **'Outside your {tolerance}′ tolerance: stop with the shutter, then start again to refine it with the guide camera.'**
  String polarOutsideTolerance(String tolerance);

  /// Polar align (#2082): caption with the Check button, how far it turns the mount (the profile's check step, whole degrees) before it moves
  ///
  /// In en, this message translates to:
  /// **'Check turns the mount 2 × {step}° and takes three frames to measure the alignment.'**
  String polarCheckCaption(String step);

  /// Polar align (#2082): after the guide camera could not solve the pole field, a start that measures with the main camera first (it goes through the confirm step)
  ///
  /// In en, this message translates to:
  /// **'Rough first (main camera)'**
  String get polarRoughFirst;

  /// Polar align (#2082): caption with the Rough first button, how far it turns the mount (the profile's turn, whole degrees) before it moves
  ///
  /// In en, this message translates to:
  /// **'Rough first turns the mount 2 × {step}° and measures with the main camera.'**
  String polarRoughFirstCaption(String step);

  /// Polar align (#2082): caution under the readout or the check's verdict when a point's frames stayed scattered after their retake
  ///
  /// In en, this message translates to:
  /// **'Noisy measurement: the solves at one point scattered more than 3″, even after a retake. Check focus and seeing, or take more frames per point.'**
  String get polarNoisyMeasurement;

  /// Settings > Imaging > Polar align (#2082): which camera measures the axis and runs the live loop
  ///
  /// In en, this message translates to:
  /// **'Camera'**
  String get polarKnobCamera;

  /// Settings > Imaging > Polar align (#2082): camera choice, the guide camera (the default)
  ///
  /// In en, this message translates to:
  /// **'Guide camera'**
  String get polarKnobCameraGuide;

  /// Settings > Imaging > Polar align (#2082): camera choice, the main camera with its own solver
  ///
  /// In en, this message translates to:
  /// **'Main camera'**
  String get polarKnobCameraMain;

  /// Settings > Imaging > Polar align (#2082): how many frames are averaged at each of the three points, with its accepted range
  ///
  /// In en, this message translates to:
  /// **'Frames per point (1 to 5)'**
  String get polarKnobFramesPerPoint;

  /// Settings > Imaging > Polar align (#2082): hint under frames per point
  ///
  /// In en, this message translates to:
  /// **'Averaged; a point scattered over 3″ is retaken once'**
  String get polarKnobFramesPerPointHint;

  /// Polar align start confirm dialog (#2082): shown when the run measures with the main camera (Rough first, or the profile's camera)
  ///
  /// In en, this message translates to:
  /// **'This measurement uses the main camera.'**
  String get polarConfirmMainCamera;

  /// Simple Mode Polar align stepper: what a screen reader says for a step with a tick
  ///
  /// In en, this message translates to:
  /// **'Done'**
  String get polarStepDone;

  /// Settings → Imaging → Polar align: section heading over the routine's knobs
  ///
  /// In en, this message translates to:
  /// **'Polar align'**
  String get polarKnobSection;

  /// Settings → Imaging → Polar align: tolerance field label with its accepted range
  ///
  /// In en, this message translates to:
  /// **'Tolerance (arcmin, 0.1 to 10)'**
  String get polarKnobTolerance;

  /// Settings → Imaging → Polar align: RA turn field label with its accepted range
  ///
  /// In en, this message translates to:
  /// **'Turn (°, 5 to 30)'**
  String get polarKnobTurn;

  /// Settings → Imaging → Polar align: Verify step field label with its accepted range
  ///
  /// In en, this message translates to:
  /// **'Check step (°, 5 to 30)'**
  String get polarKnobVerifyStep;

  /// Settings → Imaging → Polar align: rough step exposure field label with its accepted range
  ///
  /// In en, this message translates to:
  /// **'Rough step exposure (s, 0.1 to 60)'**
  String get polarKnobRoughExposure;

  /// Imaging > Polar align settings panel hint
  ///
  /// In en, this message translates to:
  /// **'Done turns on once the error is inside it'**
  String get polarKnobToleranceHint;

  /// Imaging > Polar align settings panel hint
  ///
  /// In en, this message translates to:
  /// **'Kept between 5° and 30°'**
  String get polarKnobTurnHint;

  /// Imaging > Polar align settings panel hint
  ///
  /// In en, this message translates to:
  /// **'Kept between 5° and 30°'**
  String get polarKnobVerifyStepHint;

  /// The Polar Aligner (glossary term): a motorised device that turns the mount's polar axis. Block heading in Polar align, row in Settings > Devices, status line
  ///
  /// In en, this message translates to:
  /// **'Polar Aligner'**
  String get polarAligner;

  /// Polar Aligner block before the routine starts
  ///
  /// In en, this message translates to:
  /// **'Tap the shutter to start. Ara measures, makes a small test move on each axis, then turns the motors until the error is inside your {tolerance}′ tolerance.'**
  String polarAlignerIdle(String tolerance);

  /// Polar Aligner block when Turned by is By hand
  ///
  /// In en, this message translates to:
  /// **'Motors off. Turn the bolts by hand.'**
  String get polarAlignerByHand;

  /// Polar Aligner block and status line: the test move, e.g. 'Test move · azimuth 2.0′ east'
  ///
  /// In en, this message translates to:
  /// **'Test move · {axis} {amount}′ {direction}'**
  String polarAlignerTestMove(String axis, String amount, String direction);

  /// Polar Aligner block and status line: a correction, e.g. 'Moving azimuth 6.1′ east'
  ///
  /// In en, this message translates to:
  /// **'Moving {axis} {amount}′ {direction}'**
  String polarAlignerMoving(String axis, String amount, String direction);

  /// Polar Aligner block and status line: waiting the device's settle time
  ///
  /// In en, this message translates to:
  /// **'Letting the mount settle · {seconds} s'**
  String polarAlignerSettling(String seconds);

  /// Polar Aligner block and status line: the frame after a move
  ///
  /// In en, this message translates to:
  /// **'Measuring the new error…'**
  String get polarAlignerMeasuring;

  /// Polar Aligner block: the device is going home
  ///
  /// In en, this message translates to:
  /// **'Going back to its zero…'**
  String get polarAlignerHoming;

  /// Polar Aligner block: the motors rest inside the tolerance
  ///
  /// In en, this message translates to:
  /// **'{moves, plural, =1{Inside your {tolerance}′ tolerance after 1 move.} other{Inside your {tolerance}′ tolerance after {moves} moves.}} Finish with Check or Done. If the error drifts out, the motors correct again.'**
  String polarAlignerConverged(int moves, String tolerance);

  /// Polar Aligner block: the motors rest inside the tolerance, while the polar-align settings (and so the tolerance in arcminutes) are not loaded from the rig
  ///
  /// In en, this message translates to:
  /// **'{moves, plural, =1{Inside your tolerance after 1 move.} other{Inside your tolerance after {moves} moves.}} Check and Done wait for your polar-align settings to load. If the error drifts out, the motors correct again.'**
  String polarAlignerConvergedPlain(int moves);

  /// Fills polarAlignerTestMove / polarAlignerMoving and the stop sentences: the azimuth axis, lower case
  ///
  /// In en, this message translates to:
  /// **'azimuth'**
  String get polarAlignerAxisAzimuth;

  /// Fills polarAlignerTestMove / polarAlignerMoving and the stop sentences: the altitude axis, lower case
  ///
  /// In en, this message translates to:
  /// **'altitude'**
  String get polarAlignerAxisAltitude;

  /// Fills polarAlignerTestMove / polarAlignerMoving: an azimuth move toward east
  ///
  /// In en, this message translates to:
  /// **'east'**
  String get polarAlignerEast;

  /// Fills polarAlignerTestMove / polarAlignerMoving: an azimuth move toward west
  ///
  /// In en, this message translates to:
  /// **'west'**
  String get polarAlignerWest;

  /// Fills polarAlignerTestMove / polarAlignerMoving: an altitude move up
  ///
  /// In en, this message translates to:
  /// **'up'**
  String get polarAlignerUp;

  /// Fills polarAlignerTestMove / polarAlignerMoving: an altitude move down
  ///
  /// In en, this message translates to:
  /// **'down'**
  String get polarAlignerDown;

  /// Polar Aligner block: the device's position on each axis, signed arcminutes such as +12.4′ (— for an axis it does not report)
  ///
  /// In en, this message translates to:
  /// **'From its zero: azimuth {azimuth} · altitude {altitude}'**
  String polarAlignerPosition(String azimuth, String altitude);

  /// Polar Aligner block: the device reports no position
  ///
  /// In en, this message translates to:
  /// **'This device doesn\'t report its position.'**
  String get polarAlignerNoPosition;

  /// Polar Aligner block after the motors stopped
  ///
  /// In en, this message translates to:
  /// **'Carry on by hand: the arrows show which bolt to turn.'**
  String get polarAlignerCarryOn;

  /// Polar Aligner block: red button that stops the motors
  ///
  /// In en, this message translates to:
  /// **'Stop motors'**
  String get polarAlignerStopMotors;

  /// Polar Aligner block: button that sends the device to its zero
  ///
  /// In en, this message translates to:
  /// **'Home'**
  String get polarAlignerHome;

  /// Polar Aligner block: green button that corrects with the motors again after a stop
  ///
  /// In en, this message translates to:
  /// **'Resume'**
  String get polarAlignerResume;

  /// Polar Aligner block: why Resume and Home are off after the link was lost
  ///
  /// In en, this message translates to:
  /// **'Resume once it answers again. Home comes back then.'**
  String get polarAlignerLinkLostCaption;

  /// Polar Aligner block and Settings > Devices: the device only offers its own loop
  ///
  /// In en, this message translates to:
  /// **'Ara can\'t move this Polar Aligner yet: it only runs its own alignment.'**
  String get polarAlignerNotDrivableLoop;

  /// Polar Aligner block and Settings > Devices: the device's Info could not be read
  ///
  /// In en, this message translates to:
  /// **'Ara couldn\'t read what this Polar Aligner can do, so it won\'t move it.'**
  String get polarAlignerNotDrivableInfo;

  /// Polar Aligner block: follows a not-drivable sentence
  ///
  /// In en, this message translates to:
  /// **'Turn the bolts by hand.'**
  String get polarAlignerTurnByHand;

  /// Polar Aligner stop cause wrong_direction
  ///
  /// In en, this message translates to:
  /// **'The test move on {axis} went the wrong way, so Ara stopped the motors. The Polar Aligner may be set up reversed on that axis.'**
  String polarAlignerStopWrongDirection(String axis);

  /// Polar Aligner stop cause wrong_scale
  ///
  /// In en, this message translates to:
  /// **'The test move on {axis} didn\'t go the distance asked, so Ara stopped the motors. Check the Polar Aligner\'s step setting.'**
  String polarAlignerStopWrongScale(String axis);

  /// Polar Aligner stop cause device_fault, with the device's own text
  ///
  /// In en, this message translates to:
  /// **'The Polar Aligner reported a fault: “{text}”.'**
  String polarAlignerStopDeviceFault(String text);

  /// Polar Aligner stop cause not_calibrated, with the device's own text
  ///
  /// In en, this message translates to:
  /// **'The Polar Aligner isn\'t calibrated, so it refused to move: “{text}”. Calibrate it in its own app, then Resume.'**
  String polarAlignerStopNotCalibrated(String text);

  /// Polar Aligner stop cause device_refused, with the device's own text
  ///
  /// In en, this message translates to:
  /// **'The Polar Aligner refused the move: “{text}”.'**
  String polarAlignerStopDeviceRefused(String text);

  /// Polar Aligner stop cause diverging
  ///
  /// In en, this message translates to:
  /// **'The error grew after two corrections in a row, so Ara stopped the motors.'**
  String get polarAlignerStopDiverging;

  /// Polar Aligner stop cause timeout
  ///
  /// In en, this message translates to:
  /// **'The Polar Aligner was still moving after 60 s, so Ara stopped it.'**
  String get polarAlignerStopTimeout;

  /// Polar Aligner stop cause link_lost
  ///
  /// In en, this message translates to:
  /// **'Lost the link to the Polar Aligner. Ara stops its motors as soon as it answers again.'**
  String get polarAlignerStopLinkLost;

  /// Polar Aligner stop cause mount_tracking
  ///
  /// In en, this message translates to:
  /// **'The Polar Aligner can\'t move while the mount tracks. Turn tracking off in the Mount window, then Resume.'**
  String get polarAlignerStopMountTracking;

  /// Polar Aligner stop cause mount_slewing
  ///
  /// In en, this message translates to:
  /// **'The Polar Aligner can\'t move while the mount slews. Resume once the mount stops.'**
  String get polarAlignerStopMountSlewing;

  /// Polar Aligner stop cause stopped_by_user
  ///
  /// In en, this message translates to:
  /// **'You stopped the motors.'**
  String get polarAlignerStopByUser;

  /// Polar Aligner stop with a cause this app does not know
  ///
  /// In en, this message translates to:
  /// **'Ara stopped the motors.'**
  String get polarAlignerStopUnknown;

  /// Polar Aligner stop with a cause this app does not know, with the device's own text
  ///
  /// In en, this message translates to:
  /// **'Ara stopped the motors: “{text}”.'**
  String polarAlignerStopUnknownText(String text);

  /// Status line: the Polar Aligner's motors stopped
  ///
  /// In en, this message translates to:
  /// **'Stopped'**
  String get polarAlignerStopped;

  /// Settings > Devices: the Polar Aligner can be driven and is still
  ///
  /// In en, this message translates to:
  /// **'Ready'**
  String get polarAlignerReady;

  /// Settings > Devices: a Polar Aligner Ara cannot drive yet
  ///
  /// In en, this message translates to:
  /// **'Can\'t drive yet'**
  String get polarAlignerCantDrive;

  /// Settings > Devices: a Polar Aligner whose Info could not be read
  ///
  /// In en, this message translates to:
  /// **'Can\'t read it'**
  String get polarAlignerCantRead;

  /// Settings > Devices: no Polar Aligner found
  ///
  /// In en, this message translates to:
  /// **'None'**
  String get polarAlignerNone;

  /// Settings > Devices: caption with no Polar Aligner
  ///
  /// In en, this message translates to:
  /// **'No connected device offers polar-alignment moves. Polar align shows the turns and you make them.'**
  String get polarAlignerFoundNone;

  /// Settings > Devices: caption when the mount carries the Polar Aligner
  ///
  /// In en, this message translates to:
  /// **'Found on the mount: its motors are built in.'**
  String get polarAlignerFoundMount;

  /// Settings > Devices: caption when a separate base (a Switch) carries the Polar Aligner
  ///
  /// In en, this message translates to:
  /// **'Found on the polar base.'**
  String get polarAlignerFoundBase;

  /// Settings > Devices, under the Polar Aligner: Motors or By hand
  ///
  /// In en, this message translates to:
  /// **'Turned by'**
  String get polarAlignerTurnedBy;

  /// Settings > Devices, Turned by: the routine turns the motors
  ///
  /// In en, this message translates to:
  /// **'Motors'**
  String get polarAlignerMotors;

  /// Settings > Devices, Turned by: the person turns the bolts
  ///
  /// In en, this message translates to:
  /// **'By hand'**
  String get polarAlignerByHandOption;

  /// Settings > Devices, Turned by Motors: caption
  ///
  /// In en, this message translates to:
  /// **'Polar align turns the motors after each measurement.'**
  String get polarAlignerMotorsCaption;

  /// Settings > Devices, Turned by By hand: caption
  ///
  /// In en, this message translates to:
  /// **'Polar align shows the turns and you make them.'**
  String get polarAlignerHandCaption;

  /// Completes 'Couldn't …': Stop motors failed
  ///
  /// In en, this message translates to:
  /// **'stop the motors'**
  String get polarAlignerActionStop;

  /// Completes 'Couldn't …': Resume failed
  ///
  /// In en, this message translates to:
  /// **'resume the motors'**
  String get polarAlignerActionResume;

  /// Completes 'Couldn't …': Home failed
  ///
  /// In en, this message translates to:
  /// **'send the Polar Aligner home'**
  String get polarAlignerActionHome;

  /// Imaging > Polar align settings panel: switch for the profile's polar_aligner_auto
  ///
  /// In en, this message translates to:
  /// **'Correct with the Polar Aligner\'s motors'**
  String get polarKnobAlignerAuto;

  /// Imaging > Polar align settings panel hint
  ///
  /// In en, this message translates to:
  /// **'On a rig with a Polar Aligner, Polar align turns its motors after each measurement'**
  String get polarKnobAlignerAutoOn;

  /// Imaging > Polar align settings panel hint
  ///
  /// In en, this message translates to:
  /// **'Off: Polar align shows the turns and you make them'**
  String get polarKnobAlignerAutoOff;

  /// Imaging > Polar align settings panel: switch for the profile's stop_tracking_when_done (NINA's StopTrackingWhenDone)
  ///
  /// In en, this message translates to:
  /// **'Stop tracking when polar align ends'**
  String get polarKnobStopTracking;

  /// Imaging > Polar align settings panel hint while stop_tracking_when_done is on
  ///
  /// In en, this message translates to:
  /// **'Every end of a run turns tracking off'**
  String get polarKnobStopTrackingOn;

  /// Imaging > Polar align settings panel hint while stop_tracking_when_done is off
  ///
  /// In en, this message translates to:
  /// **'Off: a run that finishes gives back the tracking it found. A Stop always leaves tracking off'**
  String get polarKnobStopTrackingOff;

  /// Advanced Mode Polar Align Start card: the row that opens the routine's four knobs
  ///
  /// In en, this message translates to:
  /// **'Advanced'**
  String get polarAdvancedTitle;

  /// Advanced Mode Polar Align Start card: the collapsed Advanced row's one-line summary of the seed turn, the Verify step and the rough exposure
  ///
  /// In en, this message translates to:
  /// **'{turn}° turn · {step}° step · {exposure} s'**
  String polarAdvancedSummary(String turn, String step, String exposure);

  /// Advanced Mode Polar Align Start card: the range a knob accepts, as it follows 'Use '
  ///
  /// In en, this message translates to:
  /// **'{min} to {max}'**
  String polarAdvancedRange(String min, String max);

  /// Advanced Mode Polar Align Start card: under a knob field that holds a value the rig would refuse; range is the accepted range
  ///
  /// In en, this message translates to:
  /// **'Out of range: use {range}'**
  String polarAdvancedOutOfRange(String range);

  /// Advanced Mode Polar Align Start card: a knob save failed; reason is the sentence saying why
  ///
  /// In en, this message translates to:
  /// **'{reason} The field is back to the rig\'s value.'**
  String polarAdvancedSaveFailed(String reason);

  /// Emergency stop: the Polar Aligner's stop failed
  ///
  /// In en, this message translates to:
  /// **'POLAR ALIGNER MAY STILL BE MOVING (stop failed)'**
  String get estopFailPolarAligner;

  /// Rail and deck word for Tonight's Best
  ///
  /// In en, this message translates to:
  /// **'Best'**
  String get bestNav;

  /// Tonight's Best screen title
  ///
  /// In en, this message translates to:
  /// **'Tonight\'s Best'**
  String get bestTitle;

  /// Subtitle: when the list was ranked (HH:MM)
  ///
  /// In en, this message translates to:
  /// **'for the rest of tonight · updated {time}'**
  String bestSubtitle(String time);

  /// Plan sheet button that opens Tonight's Best
  ///
  /// In en, this message translates to:
  /// **'See all'**
  String get bestSeeAll;

  /// Tonight's Best: back button
  ///
  /// In en, this message translates to:
  /// **'Back'**
  String get bestBack;

  /// Tonight's Best: rank again
  ///
  /// In en, this message translates to:
  /// **'Refresh'**
  String get bestRefresh;

  /// Sort control label
  ///
  /// In en, this message translates to:
  /// **'Sort'**
  String get bestSort;

  /// Sort by score
  ///
  /// In en, this message translates to:
  /// **'Score'**
  String get bestSortScore;

  /// Sort by transit time; also the transit label
  ///
  /// In en, this message translates to:
  /// **'Transit'**
  String get bestSortTransit;

  /// Sort by altitude now
  ///
  /// In en, this message translates to:
  /// **'Altitude now'**
  String get bestSortAltitude;

  /// Type chips label
  ///
  /// In en, this message translates to:
  /// **'Type'**
  String get bestType;

  /// Type chip: every type
  ///
  /// In en, this message translates to:
  /// **'All'**
  String get bestTypeAll;

  /// Type chip
  ///
  /// In en, this message translates to:
  /// **'Galaxy'**
  String get bestTypeGalaxy;

  /// Type chip
  ///
  /// In en, this message translates to:
  /// **'Nebula'**
  String get bestTypeNebula;

  /// Type chip
  ///
  /// In en, this message translates to:
  /// **'Cluster'**
  String get bestTypeCluster;

  /// Type chip
  ///
  /// In en, this message translates to:
  /// **'Planetary nebula'**
  String get bestTypePlanetary;

  /// Type chip
  ///
  /// In en, this message translates to:
  /// **'Other'**
  String get bestTypeOther;

  /// Show control label
  ///
  /// In en, this message translates to:
  /// **'Show'**
  String get bestShow;

  /// Show: the whole night
  ///
  /// In en, this message translates to:
  /// **'All night'**
  String get bestShowAll;

  /// Show: only Targets up now
  ///
  /// In en, this message translates to:
  /// **'Up now'**
  String get bestShowUp;

  /// Peak altitude control label
  ///
  /// In en, this message translates to:
  /// **'Peak altitude (at transit)'**
  String get bestPeak;

  /// Peak altitude: no minimum
  ///
  /// In en, this message translates to:
  /// **'Any'**
  String get bestPeakAny;

  /// Peak altitude minimum
  ///
  /// In en, this message translates to:
  /// **'{deg}°'**
  String bestPeakDeg(int deg);

  /// Fits my field control label
  ///
  /// In en, this message translates to:
  /// **'Field'**
  String get bestField;

  /// Only Targets that fit the field
  ///
  /// In en, this message translates to:
  /// **'Fits my field'**
  String get bestFitsOn;

  /// Every Target, whatever its fit
  ///
  /// In en, this message translates to:
  /// **'All fields'**
  String get bestFitsOff;

  /// Why Fits my field is off
  ///
  /// In en, this message translates to:
  /// **'Field not known. Set your camera and telescope in Settings > Rig.'**
  String get bestFieldUnknown;

  /// Count line
  ///
  /// In en, this message translates to:
  /// **'{shown} of {total} Targets'**
  String bestCount(int shown, int total);

  /// Note under the controls
  ///
  /// In en, this message translates to:
  /// **'Sort and filters are kept on this device.'**
  String get bestKept;

  /// Framing fit
  ///
  /// In en, this message translates to:
  /// **'Too small'**
  String get bestFitTooSmall;

  /// Framing fit
  ///
  /// In en, this message translates to:
  /// **'Fills the frame'**
  String get bestFitFills;

  /// Framing fit
  ///
  /// In en, this message translates to:
  /// **'Needs a mosaic'**
  String get bestFitMosaic;

  /// Best dark window, e.g. 20:40-03:20
  ///
  /// In en, this message translates to:
  /// **'Best {range}'**
  String bestWindow(String range);

  /// Transit time
  ///
  /// In en, this message translates to:
  /// **'Transit {time}'**
  String bestTransitAt(String time);

  /// Peak altitude of the Target
  ///
  /// In en, this message translates to:
  /// **'Highest {deg}°'**
  String bestHighest(int deg);

  /// Altitude now
  ///
  /// In en, this message translates to:
  /// **'Now {deg}°'**
  String bestNow(int deg);

  /// Altitude line of a Target under the horizon
  ///
  /// In en, this message translates to:
  /// **'Now: below the horizon'**
  String get bestNowBelow;

  /// Moon distance on a Target the Moon affects
  ///
  /// In en, this message translates to:
  /// **'Moon {deg}° away: brighter sky'**
  String bestMoon(int deg);

  /// Marker and disabled button
  ///
  /// In en, this message translates to:
  /// **'In Queue'**
  String get bestInQueue;

  /// Marker: already imaged
  ///
  /// In en, this message translates to:
  /// **'Collected'**
  String get bestCollected;

  /// Opens the plan sheet on the Target
  ///
  /// In en, this message translates to:
  /// **'Image this'**
  String get bestImage;

  /// Adds the Target while a plan runs
  ///
  /// In en, this message translates to:
  /// **'Add to Queue'**
  String get bestAddToQueue;

  /// Pill after the add
  ///
  /// In en, this message translates to:
  /// **'{name} added to the Queue'**
  String bestAdded(String name);

  /// Empty state
  ///
  /// In en, this message translates to:
  /// **'Nothing well placed tonight'**
  String get bestNoneTitle;

  /// Empty state text
  ///
  /// In en, this message translates to:
  /// **'No Target stays above 30° long enough for this rig tonight. Try again tomorrow, or search for a Target by name.'**
  String get bestNoneBody;

  /// Empty state action
  ///
  /// In en, this message translates to:
  /// **'Search'**
  String get bestSearch;

  /// Empty state
  ///
  /// In en, this message translates to:
  /// **'Set your site'**
  String get bestNoSiteTitle;

  /// Empty state text
  ///
  /// In en, this message translates to:
  /// **'Tonight\'s Best ranks by where you are. Add your site and the list fills in.'**
  String get bestNoSiteBody;

  /// Empty state action
  ///
  /// In en, this message translates to:
  /// **'Set site'**
  String get bestSetSite;

  /// Filters hide every Target
  ///
  /// In en, this message translates to:
  /// **'No Targets match'**
  String get bestNoMatchTitle;

  /// Empty state text
  ///
  /// In en, this message translates to:
  /// **'Your filters hide every Target.'**
  String get bestNoMatchBody;

  /// Resets the four filters
  ///
  /// In en, this message translates to:
  /// **'Clear filters'**
  String get bestClearFilters;

  /// Strip text
  ///
  /// In en, this message translates to:
  /// **'Rig not connected'**
  String get bestRigOff;

  /// Note when the rig is not connected
  ///
  /// In en, this message translates to:
  /// **'Planning offline: ranked from your saved site and telescope. Image this plans now; the rig runs it later.'**
  String get bestOffline;

  /// Error text
  ///
  /// In en, this message translates to:
  /// **'Could not rank tonight\'s Targets.'**
  String get bestLoadFailed;

  /// Error action
  ///
  /// In en, this message translates to:
  /// **'Try again'**
  String get bestRetry;

  /// Refusal (no-camera): the daemon has no camera to take the frame with
  ///
  /// In en, this message translates to:
  /// **'No camera is connected. Connect one in Camera settings, then try again.'**
  String get refusalNoCamera;

  /// Refusal (camera-busy): another exposure holds the camera
  ///
  /// In en, this message translates to:
  /// **'The camera is busy with an exposure. Try again once it has read out.'**
  String get refusalCameraBusy;

  /// Refusal (gate-stuck): the camera's in-flight gate never released
  ///
  /// In en, this message translates to:
  /// **'The camera is stuck mid-capture. Restart Ara on your rig to clear it.'**
  String get refusalGateStuck;

  /// Refusal (center-in-progress): a centring job for another target is running
  ///
  /// In en, this message translates to:
  /// **'Another centering is already running. Wait for it, or cancel it, before centering on a different target.'**
  String get refusalCenterInProgress;

  /// Refusal (optics-not-set): the optics a plate solve needs; missing is focal_length, pixel_size or anything else for both
  ///
  /// In en, this message translates to:
  /// **'{missing, select, focal_length{The telescope focal length is not set. Set it in Optics.} pixel_size{The camera pixel size is not set. Set it in Optics.} other{The telescope focal length and camera pixel size are not set. Set them in Optics.}}'**
  String refusalOpticsNotSet(String missing);

  /// Refusal (solver-not-set): the plate solver is missing, wrong or too old
  ///
  /// In en, this message translates to:
  /// **'The plate solver is not set up. Check its path in Plate solving settings.'**
  String get refusalSolverNotSet;

  /// Refusal (capture-settings-unsupported): the exposure or binning is outside the camera's range
  ///
  /// In en, this message translates to:
  /// **'The camera cannot take that exposure or binning. Try a different one.'**
  String get refusalCaptureSettingsUnsupported;

  /// Refusal (storage-unavailable): the disk frames are saved to is gone
  ///
  /// In en, this message translates to:
  /// **'The storage disk is missing. Plug it back in, then try again.'**
  String get refusalStorageUnavailable;

  /// Refusal (below-horizon-limit): a GoTo to a target under the profile's horizon; altitude and limit are degrees with one decimal
  ///
  /// In en, this message translates to:
  /// **'That target is at {altitude}°, below your {limit}° horizon limit. Pick a higher target, or lower the horizon limit.'**
  String refusalBelowHorizonLimit(String altitude, String limit);

  /// Refusal (counterweight-limit): a GoTo to a target below the pole that would turn the counterweight past the limit; hourAngle is hours with one decimal, angle and limit are whole degrees
  ///
  /// In en, this message translates to:
  /// **'That target is below the pole, at hour angle {hourAngle} h: the counterweight would swing {angle}° up from hanging straight down, past the {limit}° limit. Pick another target, or wait until this one is higher.'**
  String refusalCounterweightLimit(
    String hourAngle,
    String angle,
    String limit,
  );

  /// Refusal (above-clearance-limit, #1589): a GoTo to a target at or above the profile's clearance limit; altitude and limit are degrees with one decimal
  ///
  /// In en, this message translates to:
  /// **'That target is at {altitude}°, at or above your {limit}° clearance limit, where the camera can reach the mount. Pick a lower target, or try again when it is lower.'**
  String refusalAboveClearanceLimit(String altitude, String limit);

  /// Refusal (end-night-in-progress): End Night was started while it is already running
  ///
  /// In en, this message translates to:
  /// **'End Night is already running. Wait for it to finish, or cancel it, before starting it again.'**
  String get refusalEndNightInProgress;

  /// Refusal (mount-position-unverified): GoTo, Park, Home and Polar Align are blocked because the mount's reported position is not trusted after a power loss
  ///
  /// In en, this message translates to:
  /// **'The mount\'s position is unverified: it may have lost power, and it reports Home wherever it points. Run the mount\'s zero search from its hand controller or app, or confirm it is physically at Home.'**
  String get refusalMountPositionUnverified;

  /// Refusal (mount-position-unverified, not yet read): a move right after the mount connected, before its first position read
  ///
  /// In en, this message translates to:
  /// **'The mount\'s position has not been read since it connected. Try again in a few seconds.'**
  String get refusalMountPositionNotReadYet;

  /// Refusal (mount-position-unverified, reason stop_unconfirmed, #2066): GoTo, Park, Home and Polar Align are blocked because a polar alignment's stop of the mount was not confirmed, or the mount reconnected during it
  ///
  /// In en, this message translates to:
  /// **'The mount\'s position is unverified: Ara could not confirm that the mount stopped after polar alignment moved it, or the mount reconnected while polar alignment ran. Run the mount\'s zero search from its hand controller or app, or confirm it is physically at Home.'**
  String get refusalMountPositionStopUnconfirmed;

  /// Mount window: heading of the warning shown when the rig does not trust the mount's reported position (it may have lost power)
  ///
  /// In en, this message translates to:
  /// **'Mount position unverified'**
  String get mountPositionUnverifiedTitle;

  /// Mount window: what the unverified-position warning means and what still works
  ///
  /// In en, this message translates to:
  /// **'The mount may have lost power: it reports Home wherever it points. GoTo, Park, Home and Polar Align are blocked. Stop, the direction pad and tracking still work.'**
  String get mountPositionUnverifiedBody;

  /// Mount window: the unverified-position fix while the mount does not report Home (the confirm button shows once it does)
  ///
  /// In en, this message translates to:
  /// **'Run the mount\'s zero search from its hand controller or app. When it reports Home, confirm it here.'**
  String get mountPositionUnverifiedRunZeroSearch;

  /// Mount window: what the unverified-position warning means when polar alignment's stop was not confirmed (reason stop_unconfirmed, #2066), and what still works
  ///
  /// In en, this message translates to:
  /// **'Ara could not confirm that the mount stopped after polar alignment moved it, or the mount reconnected while polar alignment ran, so it may have moved on. GoTo, Park, Home and Polar Align are blocked. Stop, the direction pad and tracking still work.'**
  String get mountPositionStopUnconfirmedBody;

  /// Mount window: button that confirms the mount is physically at Home, so its position is trusted again
  ///
  /// In en, this message translates to:
  /// **'The mount IS at Home'**
  String get mountConfirmAtHome;

  /// Mount window: the question asked before confirming Home
  ///
  /// In en, this message translates to:
  /// **'Is the mount physically at Home, counterweights down and the tube along the polar axis? Confirm only if you checked it by eye.'**
  String get mountConfirmAtHomeQuestion;

  /// Mount window: the confirm button of the Home question
  ///
  /// In en, this message translates to:
  /// **'Yes, it is at Home'**
  String get mountConfirmAtHomeYes;

  /// Mount window: notice when the rig refused the Home confirmation
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t confirm Home'**
  String get mountCouldntConfirmHome;

  /// Refusal (polar-align-mount-parked): polar alignment was started on a parked mount; it never unparks the mount itself
  ///
  /// In en, this message translates to:
  /// **'The mount is parked. Unpark it and point it near the pole first.'**
  String get refusalPolarAlignMountParked;

  /// Refusal (polar-align-unconfirmed): a start reached the rig without the confirm step, or the turn direction changed since the user confirmed it
  ///
  /// In en, this message translates to:
  /// **'Polar alignment moves the mount only after you confirm it. Start it again and confirm.'**
  String get refusalPolarAlignUnconfirmed;

  /// Refusal (polar-align-sweep-limit, #2089): the RA axis has turned too far from where polar alignment started this session; offset is whole degrees
  ///
  /// In en, this message translates to:
  /// **'The mount has turned {offset}° from where Polar Align started. Slew it back near the pole, counterweights down, and start again.'**
  String refusalPolarAlignSweepLimit(String offset);

  /// Refusal (polar-align-moving-mount, #2066): a GoTo, Center, Home or Park was refused because polar alignment holds the mount for its whole run (its turns, solves and adjust loop); a direction-pad move only while it turns the RA motor; Stop Mount ends polar alignment too
  ///
  /// In en, this message translates to:
  /// **'Polar alignment is using the mount. Stop polar alignment first, or tap Stop Mount.'**
  String get refusalPolarAlignMovingMount;

  /// Polar Align card (both modes): why the run ended when a stop it did not send ended it (polar_align.error slew_aborted_externally, #2066, #2107); {by} is the stop's stopped_by token
  ///
  /// In en, this message translates to:
  /// **'{by, select, user_request{The mount was stopped from outside polar alignment (Stop Mount). Polar alignment ended. Start again when ready.} emergency_stop{Emergency Stop stopped the mount. Polar alignment ended. Start again when ready.} watchdog{The meridian-flip watchdog stopped the mount. Polar alignment ended. Check the mount, then start again.} park{The mount was parked from outside polar alignment. Polar alignment ended. Unpark it and start again when ready.} findhome{The mount was sent Home from outside polar alignment. Polar alignment ended. Start again when ready.} pad_stop{The mount was stopped from the direction pad. Polar alignment ended. Start again when ready.} fault_reaction{The equipment-fault reaction stopped the mount (its park was refused because the mount\'s position is unverified). Polar alignment ended. Check the rig before you start again.} pose_unverified{The safety reaction stopped the mount (conditions turned unsafe, and the park was refused because the mount\'s position is unverified). Polar alignment ended. Check the rig before you start again.} other{The mount was stopped from outside polar alignment. Polar alignment ended. Start again when ready.}}'**
  String polarAlignStoppedFromOutside(String by);

  /// Polar Align card (both modes): added after the polarAlignStoppedFromOutside sentence when the run could not confirm its own stop afterwards (polar_align.error stop_unconfirmed, #2066); the mount may still be moving
  ///
  /// In en, this message translates to:
  /// **'Ara could not confirm that the mount stopped. Cut the mount\'s power now, and check the mount before you start again.'**
  String get polarAlignStopNotConfirmed;

  /// Refusal (polar-align-on-axis, #2082): polar alignment was started with the scope pointing within 3° of the celestial pole, where the camera looks along the RA axis; nothing tilts it off any more
  ///
  /// In en, this message translates to:
  /// **'The scope points within 3° of the pole. Point it 2–5° from the pole, counterweights down, then start again.'**
  String get refusalPolarAlignOnAxis;

  /// Refusal (polar-align-pointing-unknown, #2066): polar alignment plans only from a reading of the mount taken now; none came, it was the (0, 0) placeholder, or it was more than 2 s old
  ///
  /// In en, this message translates to:
  /// **'Polar Align got no fresh reading of where the mount points. Check the mount\'s connection, then start again.'**
  String get refusalPolarAlignPointingUnknown;

  /// Refusal (polar-align-pose-ambiguous, #2066): the mount reads a declination beyond ±90° (its Dec axis over the pole, e.g. after a sync), so which side of the pier it is on is ambiguous; polar alignment moves nothing
  ///
  /// In en, this message translates to:
  /// **'The mount reports a Dec past the pole, so its pier side is not known. Point the tube 2–5° from the pole, counterweights down, with the hand controller (or send the mount Home), then start again.'**
  String get refusalPolarAlignPoseAmbiguous;

  /// Refusal (polar-align-mount-busy, #2066): polar alignment was started while the mount slews (or does not say whether it does), or while the guider guides or calibrates; it would turn the mount beside another motion
  ///
  /// In en, this message translates to:
  /// **'The mount is moving or the guider is guiding. Let the slew finish or stop guiding, then start polar alignment again.'**
  String get refusalPolarAlignMountBusy;

  /// Error message tail after "Couldn't " (friendlyError action); lower-case verb phrase, no period
  ///
  /// In en, this message translates to:
  /// **'confirm the meridian flip'**
  String get actionConfirmFlip;

  /// Settings → Unattended running → When to stop, first-flip row (#1589): the user confirmed a completed flip
  ///
  /// In en, this message translates to:
  /// **'Meridian flip confirmed: later flips run without the alert. Re-arm after re-balancing or a rig change.'**
  String get safetyFirstFlipConfirmedStatus;

  /// Settings > Unattended running > When to stop: the switch of the profile's clearance limit (#1589)
  ///
  /// In en, this message translates to:
  /// **'Clearance limit'**
  String get safetyClearanceLimitLabel;

  /// Settings: the hint under the clearance limit switch (#1589)
  ///
  /// In en, this message translates to:
  /// **'Stops the mount before the camera or rotator can reach the pier or tripod. While on, Ara refuses a GoTo or a flip to a target at or above the highest safe altitude, and stops tracking and guiding and pauses the run when the mount tracks up to it. It never moves the mount.'**
  String get safetyClearanceLimitHint;

  /// Settings: the clearance limit's altitude in degrees, above 0 and under 90 (#1589)
  ///
  /// In en, this message translates to:
  /// **'Highest safe altitude (°)'**
  String get safetyClearanceMaxAltitudeLabel;

  /// Settings → Unattended running → When to stop, first-flip row (#1589): no flip confirmed and none completed since the last re-arm
  ///
  /// In en, this message translates to:
  /// **'Meridian flip not confirmed yet: every flip alerts and waits 60 s. Watch one flip from start to end, then confirm it here.'**
  String get safetyFirstFlipUnconfirmedStatus;

  /// Settings → Unattended running → When to stop, first-flip row (#1589): a flip ran to the end and waits for the user's confirm; the mount reported a new pier side. {when} is a local date and time like 2026-11-20 23:15
  ///
  /// In en, this message translates to:
  /// **'A flip completed at {when}: the mount reported its pier side changed. Confirm if the scope cleared the mount and the tripod, and frames and guiding came back.'**
  String safetyFirstFlipCompletedPierChanged(String when);

  /// Settings → Unattended running → When to stop, first-flip row (#1589): a flip ran to the end and waits for the user's confirm; the mount reported no pier side, or the same one. {when} is a local date and time like 2026-11-20 23:15
  ///
  /// In en, this message translates to:
  /// **'A flip completed at {when}: the mount did not report a pier side change, so check by eye. Confirm if the scope cleared the mount and the tripod, and frames and guiding came back.'**
  String safetyFirstFlipCompletedCheckByEye(String when);

  /// Settings → Unattended running → When to stop, first-flip row (#1589): button that confirms the completed flip
  ///
  /// In en, this message translates to:
  /// **'Confirm'**
  String get safetyFirstFlipConfirmButton;

  /// Settings → Unattended running → When to stop, first-flip row (#1589): snackbar after Confirm succeeds
  ///
  /// In en, this message translates to:
  /// **'Meridian flip confirmed. Later flips run without the alert.'**
  String get safetyFirstFlipConfirmedSnack;

  /// Settings → Unattended running → When to stop, first-flip row (#1589): snackbar after Re-arm succeeds; the confirmation and any completed-flip record are cleared
  ///
  /// In en, this message translates to:
  /// **'Re-armed. Every flip alerts and waits 60 s again until you confirm one.'**
  String get safetyFirstFlipRearmedSnack;

  /// Refusal (first-flip-not-completed, #1589): the user pressed Confirm for the profile's meridian flip, but no flip has run to the end since the last re-arm or optics change
  ///
  /// In en, this message translates to:
  /// **'No meridian flip has completed on this profile yet. Watch one flip from start to end, then confirm it.'**
  String get refusalFirstFlipNotCompleted;

  /// Refusal (polar-align-already-running, #2083): a Start reached the rig while a polar alignment run was active, with the other choice of turning RA by hand or with the mount's motor (for example from a second device)
  ///
  /// In en, this message translates to:
  /// **'Polar alignment is already running, and it turns RA the other way than you picked (by hand or with the motor). Stop it first, then start again.'**
  String get refusalPolarAlignAlreadyRunning;

  /// Refusal (polar-align-clutch-turned, #2083): a polar alignment start that turns RA with the motor, after a hand-turn run saw RA turned with the clutch open (the mount's reading is off by those turns); 'Turn RA by hand' is polarConfirmHandTurn
  ///
  /// In en, this message translates to:
  /// **'RA was turned by hand with the clutch open, so the mount no longer knows where it points. Sync it (Center does) or reconnect it once it is homed, then start again, or use Turn RA by hand.'**
  String get refusalPolarAlignClutchTurned;

  /// Polar align start confirm dialog (#2048): title. The routine starts from where the mount points and turns its RA axis
  ///
  /// In en, this message translates to:
  /// **'Before the mount moves'**
  String get polarConfirmTitle;

  /// Polar align start confirm dialog: where the user must point the mount before it moves; no Home, no GoTo
  ///
  /// In en, this message translates to:
  /// **'Point the tube 2–5° from the pole, with the counterweights down.'**
  String get polarConfirmPoint;

  /// Polar align start confirm dialog: the RA turns of the first step; step and total are whole degrees, direction is polarDirectionEast or polarDirectionWest
  ///
  /// In en, this message translates to:
  /// **'RA will turn about {step}° twice toward the {direction} ({total}° in all).'**
  String polarConfirmTurns(String step, String direction, String total);

  /// Polar align start confirm dialog (#2089): how far the RA axis has turned from the session's first start, whole degrees; direction is polarDirectionEast or polarDirectionWest. The run turns back toward that start
  ///
  /// In en, this message translates to:
  /// **'RA is {offset}° {direction} of where Polar Align started; these turns go back toward it.'**
  String polarConfirmFromOrigin(String offset, String direction);

  /// Polar align start confirm dialog (#2089): the pose becomes the session's start (first start since the mount connected, or the mount was moved by other means); limit is whole degrees
  ///
  /// In en, this message translates to:
  /// **'This pose is Polar Align\'s new start: until the mount is moved some other way, RA stays within {limit}° of it.'**
  String polarConfirmNewOrigin(String limit);

  /// Polar align start confirm dialog: safety line, the user cuts power if the mount heads for the pier
  ///
  /// In en, this message translates to:
  /// **'Keep a hand on the mount\'s power switch.'**
  String get polarConfirmPower;

  /// Polar align start confirm dialog: the button that starts the routine and lets the mount move
  ///
  /// In en, this message translates to:
  /// **'Move the mount'**
  String get polarConfirmGo;

  /// Polar align start confirm dialog: the button that closes it; nothing moves
  ///
  /// In en, this message translates to:
  /// **'Cancel'**
  String get polarConfirmCancel;

  /// Polar align start confirm dialog: the RA turn direction, inside polarConfirmTurns (lower case)
  ///
  /// In en, this message translates to:
  /// **'east'**
  String get polarDirectionEast;

  /// Polar align start confirm dialog: the RA turn direction, inside polarConfirmTurns (lower case)
  ///
  /// In en, this message translates to:
  /// **'west'**
  String get polarDirectionWest;

  /// Refusal (mount-clock-not-verified): a GoTo, Sync, Center or polar alignment before the mount's clock was set from the synced rig clock and checked
  ///
  /// In en, this message translates to:
  /// **'The mount\'s clock is not verified yet. Ara sets it from the synced rig clock: wait a moment and try again, or sync the time in Settings.'**
  String get refusalMountClockNotVerified;

  /// Polar align start confirm dialog (#2083): NINA's guidance on where to point before the start
  ///
  /// In en, this message translates to:
  /// **'Pick a field fairly high toward the pole for your hemisphere, at least about 15° above the horizon when you can.'**
  String get polarConfirmGuideField;

  /// Polar align start confirm dialog (#2083): NINA's guidance, keep the RA turns on one side of the meridian (pier collision)
  ///
  /// In en, this message translates to:
  /// **'Choose a start whose turns do not cross the meridian, so the tube cannot swing into the pier.'**
  String get polarConfirmGuideMeridian;

  /// Polar align start confirm dialog (#2083): the hand-turn option (NINA's manual mode); 'Turn RA by hand' is polarConfirmHandTurn. With the hand pad the turn is measured from the mount's RA reading (and its angle shown); with the clutch open the reading stands, so it is judged from the frames and no angle is shown
  ///
  /// In en, this message translates to:
  /// **'Or turn RA yourself: with Turn RA by hand, Ara moves nothing. It asks for each turn and measures it: with the hand pad from the mount\'s reading, with the clutch open from the frames.'**
  String get polarConfirmHandTurnHint;

  /// Polar align start confirm dialog (#2083): button that starts a run in which the user turns the RA axis by hand and the mount's motors never move
  ///
  /// In en, this message translates to:
  /// **'Turn RA by hand'**
  String get polarConfirmHandTurn;

  /// Polar align hand-turn run (#2083): what the user must do now; step is whole degrees, direction is polarDirectionEast or polarDirectionWest
  ///
  /// In en, this message translates to:
  /// **'Turn RA by hand about {step}° toward the {direction}, then let go.'**
  String polarHandTurnAsk(String step, String direction);

  /// Polar align hand-turn run (#2083): how far the mount's own RA reading shows the axis turned the asked way (hand pad), one decimal; not shown with the clutch open, where the mount's reading does not move
  ///
  /// In en, this message translates to:
  /// **'Turned {turned}° so far.'**
  String polarHandTurnTurned(String turned);

  /// Polar align hand-turn run (#2083): the user turned RA the wrong way; direction is polarDirectionEast or polarDirectionWest
  ///
  /// In en, this message translates to:
  /// **'That went the other way: turn toward the {direction}.'**
  String polarHandTurnWrongWay(String direction);

  /// Polar align hand-turn run (#2083): reminder while it measures that the mount's motors are never used
  ///
  /// In en, this message translates to:
  /// **'You turn RA by hand: Ara does not move the mount.'**
  String get polarHandTurnRunNote;

  /// Polar align readout (#2083): the azimuth error's convention, an arc on the sky (NINA shows the unscaled angle, larger by 1/cos(latitude))
  ///
  /// In en, this message translates to:
  /// **'true arc (cos alt)'**
  String get polarAzimuthArc;

  /// Polar align readout (#2083): the same azimuth error as NINA would show it; value is a signed arcminute figure like +1.5′
  ///
  /// In en, this message translates to:
  /// **'NINA {value}'**
  String polarAzimuthNina(String value);

  /// Polar align readout (#2083): NINA's large-error warning, the stage started more than 2° from the pole
  ///
  /// In en, this message translates to:
  /// **'The error at the start was over 2°: get closer, then run again. The readout is less reliable this far out.'**
  String get polarInitialErrorLarge;

  /// Polar align readout (#2083): NINA's huge-error warning, the stage started more than 10° from the pole
  ///
  /// In en, this message translates to:
  /// **'The error at the start was over 10°: check the site location and the start field, and that the mount turned only in RA.'**
  String get polarInitialErrorHuge;

  /// Add your devices: heading above the detected devices
  ///
  /// In en, this message translates to:
  /// **'Found on this rig'**
  String get bridgeSetupFoundTitle;

  /// Add your devices: button that adds every ticked device; count is how many are ticked
  ///
  /// In en, this message translates to:
  /// **'Add selected ({count})'**
  String bridgeSetupAddSelected(int count);

  /// Add your devices: status of the row being added, and the label of the Add selected button while it runs
  ///
  /// In en, this message translates to:
  /// **'Adding…'**
  String get bridgeSetupAdding;

  /// Add your devices: status of a ticked row not yet reached
  ///
  /// In en, this message translates to:
  /// **'Waiting'**
  String get bridgeSetupWaiting;

  /// Add your devices: status of a row the bridge registered
  ///
  /// In en, this message translates to:
  /// **'Added'**
  String get bridgeSetupAdded;

  /// Add your devices: status of a row the bridge refused or did not confirm
  ///
  /// In en, this message translates to:
  /// **'Failed'**
  String get bridgeSetupFailed;

  /// Add your devices: button that sends a failed row again
  ///
  /// In en, this message translates to:
  /// **'Retry'**
  String get bridgeSetupRetry;

  /// Add your devices: button that opens a row's details
  ///
  /// In en, this message translates to:
  /// **'Edit'**
  String get bridgeSetupEdit;

  /// Add your devices: status of a row that needs a value before it can be added
  ///
  /// In en, this message translates to:
  /// **'Needs details'**
  String get bridgeSetupNeedsDetails;

  /// Add your devices: note on a row whose add got no answer
  ///
  /// In en, this message translates to:
  /// **'No answer from the bridge: it may already exist. Retry checks first.'**
  String get bridgeSetupMayExist;

  /// Add your devices: shown while the bridge is read
  ///
  /// In en, this message translates to:
  /// **'Looking for devices'**
  String get bridgeSetupLooking;

  /// Add your devices: button that saves a row's details and goes on
  ///
  /// In en, this message translates to:
  /// **'Continue'**
  String get bridgeSetupContinue;

  /// Add your devices: button that leaves a form with nothing sent
  ///
  /// In en, this message translates to:
  /// **'Back'**
  String get bridgeSetupBack;

  /// Add your devices: button that adds a device chosen by hand
  ///
  /// In en, this message translates to:
  /// **'Add'**
  String get bridgeSetupAdd;

  /// Add your devices: button that opens the list of device types
  ///
  /// In en, this message translates to:
  /// **'Add a device by hand'**
  String get bridgeSetupByHand;

  /// Add your devices: heading above the attached cameras of a vendor
  ///
  /// In en, this message translates to:
  /// **'Which camera?'**
  String get bridgeSetupWhichCamera;

  /// Add your devices: nothing detected
  ///
  /// In en, this message translates to:
  /// **'No devices found on this rig. Plug in your gear and retry, or add one by hand.'**
  String get bridgeSetupNothingFound;

  /// Add your devices: a form field that must have a value
  ///
  /// In en, this message translates to:
  /// **'Required'**
  String get bridgeSetupRequired;

  /// Add your devices: a number outside the range the bridge accepts
  ///
  /// In en, this message translates to:
  /// **'Out of range'**
  String get bridgeSetupOutOfRange;

  /// Add your devices: a value not in the list the bridge accepts
  ///
  /// In en, this message translates to:
  /// **'Not an allowed value'**
  String get bridgeSetupNotAllowed;

  /// Add your devices: button that adds a row to a list field
  ///
  /// In en, this message translates to:
  /// **'Add a row'**
  String get bridgeSetupAddRow;

  /// Add your devices: button that removes a row from a list field
  ///
  /// In en, this message translates to:
  /// **'Remove this row'**
  String get bridgeSetupRemoveRow;

  /// Add your devices: an add was refused and no reason came with it
  ///
  /// In en, this message translates to:
  /// **'Ara refused this device.'**
  String get bridgeSetupRefused;

  /// Start question: the rig's clock could not be set and runs behind; {amount} is like '2 min'
  ///
  /// In en, this message translates to:
  /// **'The rig\'s clock is {amount} slow, so times and targets will be off. Start anyway?'**
  String clockWrongSlow(String amount);

  /// Start question: the rig's clock could not be set and runs ahead; {amount} is like '2 min'
  ///
  /// In en, this message translates to:
  /// **'The rig\'s clock is {amount} fast, so times and targets will be off. Start anyway?'**
  String clockWrongFast(String amount);

  /// Notice on connect: the rig's clock could not be set and runs behind; {amount} is like '2 min'
  ///
  /// In en, this message translates to:
  /// **'The rig\'s clock is {amount} slow and could not be set. Times and targets will be off.'**
  String clockNoticeSlow(String amount);

  /// Notice on connect: the rig's clock could not be set and runs ahead; {amount} is like '2 min'
  ///
  /// In en, this message translates to:
  /// **'The rig\'s clock is {amount} fast and could not be set. Times and targets will be off.'**
  String clockNoticeFast(String amount);

  /// Advanced Mode Run dialog title when the rig's clock could not be set
  ///
  /// In en, this message translates to:
  /// **'The rig\'s clock is wrong'**
  String get clockWrongTitle;

  /// Button: start the run although the rig's clock is wrong
  ///
  /// In en, this message translates to:
  /// **'Start anyway'**
  String get clockWrongStartAnyway;

  /// Guiding strip chip: the polar-alignment residual is being measured (elapsed / target, m:ss)
  ///
  /// In en, this message translates to:
  /// **'PA residual · measuring {elapsed} / {target}'**
  String paResidualChipMeasuring(String elapsed, String target);

  /// Guiding strip chip tooltip while the polar-alignment residual is measured
  ///
  /// In en, this message translates to:
  /// **'Guiding\'s figure for polar alignment: measuring the error left after Align from the Dec corrections over the first {target} of clean guiding (dithers and settling are left out).'**
  String paResidualTipMeasuring(String target);

  /// Guiding strip chip: no polar-alignment residual for this guiding run
  ///
  /// In en, this message translates to:
  /// **'PA residual n/a'**
  String get paResidualChipUnavailable;

  /// Guiding strip chip tooltip: no residual, with the reason sentence
  ///
  /// In en, this message translates to:
  /// **'No polar alignment residual for this guiding run. {reason}'**
  String paResidualTipUnavailable(String reason);

  /// Guiding strip chip: the residual (a lower bound) and its rating word
  ///
  /// In en, this message translates to:
  /// **'PA ≥ {value} · {rating}'**
  String paResidualChipRated(String value, String rating);

  /// Guiding strip chip: the residual is well above what Polar Align measured (#2072)
  ///
  /// In en, this message translates to:
  /// **'PA ≥ {value} · disagrees with Polar Align'**
  String paResidualChipDisagrees(String value);

  /// Guiding strip chip: the residual is too uncertain for a firm figure
  ///
  /// In en, this message translates to:
  /// **'PA ≥ {value} · noisy'**
  String paResidualChipNoisy(String value);

  /// Why a guiding run has no polar-alignment residual
  ///
  /// In en, this message translates to:
  /// **'Lock-position shift (comet tracking) is on, so the guide star drifts in Dec on purpose.'**
  String get paResidualReasonLockShift;

  /// Why a guiding run has no polar-alignment residual
  ///
  /// In en, this message translates to:
  /// **'An adaptive optics unit makes the Dec corrections, so the mount drift cannot be read from them.'**
  String get paResidualReasonAo;

  /// Why a guiding run has no polar-alignment residual
  ///
  /// In en, this message translates to:
  /// **'Guiding was not holding the star (the guide pulses were at their maximum and the star kept drifting), so the drift is not a polar alignment signal.'**
  String get paResidualReasonGuidingNotHolding;

  /// Why a guiding run has no polar-alignment residual
  ///
  /// In en, this message translates to:
  /// **'A settle failed during this run, so guiding was not holding the star.'**
  String get paResidualReasonSettleFailed;

  /// Why a guiding run has no polar-alignment residual
  ///
  /// In en, this message translates to:
  /// **'The mount was not tracking when guiding started, so the star drift is not a polar alignment signal.'**
  String get paResidualReasonMountNotTracking;

  /// Why a guiding run has no polar-alignment residual
  ///
  /// In en, this message translates to:
  /// **'The guider\'s calibration data could not be read.'**
  String get paResidualReasonNoCalibration;

  /// Why a guiding run has no polar-alignment residual
  ///
  /// In en, this message translates to:
  /// **'The guider has not reported its pixel scale.'**
  String get paResidualReasonNoPixelScale;

  /// Why a guiding run has no polar-alignment residual
  ///
  /// In en, this message translates to:
  /// **'Not enough clean guiding to fit the drift.'**
  String get paResidualReasonNoFit;

  /// Guiding residual tooltip, first line: what the figure measures and its value (a lower bound)
  ///
  /// In en, this message translates to:
  /// **'Guiding\'s figure: the polar alignment left after Align, read from the guider\'s Dec corrections over {duration} of guiding: at least {value}.'**
  String paResidualDetailFigure(String duration, String value);

  /// Guiding residual tooltip, first line with the 1-sigma uncertainty
  ///
  /// In en, this message translates to:
  /// **'Guiding\'s figure: the polar alignment left after Align, read from the guider\'s Dec corrections over {duration} of guiding: at least {value} (± {sigma}).'**
  String paResidualDetailFigureSigma(
    String duration,
    String value,
    String sigma,
  );

  /// Guiding residual tooltip: the Dec drift the figure came from
  ///
  /// In en, this message translates to:
  /// **'Dec drift {drift}″/min'**
  String paResidualDetailDrift(String drift);

  /// Guiding residual tooltip: the hour angle the figure came from
  ///
  /// In en, this message translates to:
  /// **'hour angle {hours} h'**
  String paResidualDetailHourAngle(String hours);

  /// Guiding residual tooltip: the Dec drift and the hour angle the figure came from
  ///
  /// In en, this message translates to:
  /// **'Dec drift {drift}″/min at hour angle {hours} h'**
  String paResidualDetailDriftAtHourAngle(String drift, String hours);

  /// Guiding residual tooltip: why the figure is a lower bound
  ///
  /// In en, this message translates to:
  /// **'Guiding at one hour angle sees only part of the error, so the total can be larger.'**
  String get paResidualDetailLowerBound;

  /// Guiding residual tooltip: the residual is well above Polar Align's figure; names both causes and how to tell them apart (#2072)
  ///
  /// In en, this message translates to:
  /// **'Polar Align and guiding disagree ({align} vs at least {guiding}). Either Dec backlash ate guide pulses, so the guiding figure is too high, or the mount shifted since the alignment or its reference was wrong, so the Polar Align figure is too low. Run Polar Align again: about {align} again points at backlash, much more points at the mount or the alignment.'**
  String paResidualDetailDisagree(String align, String guiding);

  /// Guiding residual tooltip: the figure is too uncertain
  ///
  /// In en, this message translates to:
  /// **'Guiding was too noisy for a firm figure.'**
  String get paResidualDetailNoisy;

  /// Guiding residual tooltip: tonight's last Polar Align result for comparison
  ///
  /// In en, this message translates to:
  /// **'Polar Align\'s figure: {value}, from plate solves at the time of the run.'**
  String paResidualDetailAlign(String value);

  /// Guiding residual tooltip: tonight's last Polar Align result and when it was measured
  ///
  /// In en, this message translates to:
  /// **'Polar Align\'s figure: {value}, from plate solves at {time}.'**
  String paResidualDetailAlignAt(String value, String time);

  /// Polar align full UI: what the live readout measures, and the azimuth convention (#2072)
  ///
  /// In en, this message translates to:
  /// **'Polar Align\'s figure: the RA axis against the refracted pole, from plate solves, live as you turn the bolts. Azimuth is the true arc on the sky (cos alt applied).'**
  String get polarReadoutMeasures;

  /// Polar align full UI checked card: what the check's figure measures, and the azimuth convention (#2072)
  ///
  /// In en, this message translates to:
  /// **'Verify\'s figure: a fresh three-point measurement of the RA axis against the refracted pole. Azimuth is the true arc on the sky (cos alt applied).'**
  String get polarCheckMeasures;

  /// Polar align (Simple Mode panel and full UI): the live readout is weak in altitude for this camera position (#2072)
  ///
  /// In en, this message translates to:
  /// **'The camera points nearly due east or west of the pole, where this readout reads the altitude bolt poorly. Trust Verify, or start again with the scope nearer the pole.'**
  String get polarReadoutCautionAltitudeWeak;

  /// Warning on the About panel, the first-run screen and Simple Mode's version rows when the server and the app come from different X.Y release lines; {server} and {app} are versions like '1.2.0'
  ///
  /// In en, this message translates to:
  /// **'Server {server} and app {app} differ; update both from the same release.'**
  String versionMismatchWarning(String server, String app);
}

class _AppLocalizationsDelegate
    extends LocalizationsDelegate<AppLocalizations> {
  const _AppLocalizationsDelegate();

  @override
  Future<AppLocalizations> load(Locale locale) {
    return SynchronousFuture<AppLocalizations>(lookupAppLocalizations(locale));
  }

  @override
  bool isSupported(Locale locale) =>
      <String>['en', 'pt'].contains(locale.languageCode);

  @override
  bool shouldReload(_AppLocalizationsDelegate old) => false;
}

AppLocalizations lookupAppLocalizations(Locale locale) {
  // Lookup logic when language+country codes are specified.
  switch (locale.languageCode) {
    case 'pt':
      {
        switch (locale.countryCode) {
          case 'BR':
            return AppLocalizationsPtBr();
        }
        break;
      }
  }

  // Lookup logic when only language code is specified.
  switch (locale.languageCode) {
    case 'en':
      return AppLocalizationsEn();
    case 'pt':
      return AppLocalizationsPt();
  }

  throw FlutterError(
    'AppLocalizations.delegate failed to load unsupported locale "$locale". This is likely '
    'an issue with the localizations generation tool. Please file an issue '
    'on GitHub with a reproducible sample app and the gen-l10n configuration '
    'that was used.',
  );
}
