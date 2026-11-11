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

  /// Simple Mode status pills: screen reader label for the +N fold past three pills; a tap opens the first one folded
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{1 more status} other{{count} more statuses}}'**
  String statusMoreSemantics(int count);

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

  /// Filter wheel window, Filter row: the filter in place and its slot number, as the full UI shows it
  ///
  /// In en, this message translates to:
  /// **'{filter} (slot {position})'**
  String deviceFilterInSlot(String filter, int position);

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

  /// Focuser window, Refocus temp row: never refocus on temperature
  ///
  /// In en, this message translates to:
  /// **'Never'**
  String get deviceNever;

  /// Focuser window row: refocus every time the temperature changes by this much (the value reads e.g. "2 °C", or "Never")
  ///
  /// In en, this message translates to:
  /// **'Refocus every'**
  String get deviceRefocusTemp;

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

  /// Simple Mode Polar align panel: what to do when the mount cannot find Home
  ///
  /// In en, this message translates to:
  /// **'This mount cannot find Home by itself. Counterweights down, tube pointing at the pole, then Continue.'**
  String get polarNeedsHomeHint;

  /// Simple Mode Polar align panel: the check's verdict
  ///
  /// In en, this message translates to:
  /// **'Three-point check: {verdict}'**
  String polarCheckedLine(String verdict);

  /// Simple Mode Polar align panel: the check's verdict and its disagreement with the fine stage
  ///
  /// In en, this message translates to:
  /// **'Three-point check: {verdict}, {delta}′ from the fine readout'**
  String polarCheckedVsFine(String verdict, String delta);

  /// Simple Mode Polar align panel: button once the mount is at Home by hand
  ///
  /// In en, this message translates to:
  /// **'Continue'**
  String get polarContinue;

  /// Completes "Couldn't …" for the Continue button
  ///
  /// In en, this message translates to:
  /// **'continue polar alignment'**
  String get polarActionContinue;

  /// Simple Mode Polar align panel: button from the rough stage to the fine stage
  ///
  /// In en, this message translates to:
  /// **'Next: fine'**
  String get polarNextFine;

  /// Completes "Couldn't …" for the Next: fine button
  ///
  /// In en, this message translates to:
  /// **'move on to the fine step'**
  String get polarActionNextFine;

  /// Simple Mode Polar align panel: button from the fine stage to the three-point check
  ///
  /// In en, this message translates to:
  /// **'Check it'**
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
  /// **'Sky'**
  String get shellSky;

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

  /// Simple Mode Settings › Devices when the app has no link to the rig
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

  /// Shutter screen-reader custom action: stop the run
  ///
  /// In en, this message translates to:
  /// **'Stop the run'**
  String get planStopTheRun;

  /// Hint beside the shutter: hold it to stop the run
  ///
  /// In en, this message translates to:
  /// **'Hold to stop'**
  String get planHoldToStop;

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
  /// **'Tap the shutter to resume and re-center; hold it to stop.'**
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
  /// **'Tap the shutter to pause after this frame; hold it to stop.'**
  String get planHintPause;

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
  /// **'Weak curve fit (R² {r2}): the focus comes from the measured frame. Run again if the sky has settled.'**
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

  /// Focus panel: focus the main telescope (short, shares a line with Auto / Manual)
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

  /// Guide panel headline while the guider is connected and not running (not "Not guiding": it read as a failure)
  ///
  /// In en, this message translates to:
  /// **'Idle'**
  String get guideIdle;

  /// Guide panel headline with no guider
  ///
  /// In en, this message translates to:
  /// **'Guider not connected'**
  String get guideNoGuider;

  /// Guide panel hint while idle
  ///
  /// In en, this message translates to:
  /// **'Tap the shutter to start guiding. It calibrates first when needed.'**
  String get guideTapShutter;

  /// Guide panel hint with no guider
  ///
  /// In en, this message translates to:
  /// **'Connect the guider in Settings › Devices.'**
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

  /// No description provided for @guideAssistantMeasuring.
  ///
  /// In en, this message translates to:
  /// **'Measuring. Guide corrections are off for now.'**
  String get guideAssistantMeasuring;

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
  /// **'Calibrate first: press Recalibrate now, then run the Guiding Assistant.'**
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

  /// No description provided for @guideAssistantFailed.
  ///
  /// In en, this message translates to:
  /// **'The Guiding Assistant stopped: {reason}'**
  String guideAssistantFailed(String reason);

  /// No description provided for @guideAssistantFailedNoReason.
  ///
  /// In en, this message translates to:
  /// **'the guider gave no reason'**
  String get guideAssistantFailedNoReason;

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

  /// Sky Album: bronze tier name (at least one light)
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

  /// Simple Mode left rail: opens the Sky Album
  ///
  /// In en, this message translates to:
  /// **'Sky Album'**
  String get shellAlbum;

  /// Sky Album card back: action on a missing card; hands the object to the planner
  ///
  /// In en, this message translates to:
  /// **'Image this target'**
  String get albumImageThis;

  /// Sky Album card back: action on a collected card; hands the object to the planner
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

  /// Plan sheet flats choice
  ///
  /// In en, this message translates to:
  /// **'Sky'**
  String get planFlatsSky;

  /// Plan sheet flats choice
  ///
  /// In en, this message translates to:
  /// **'Panel'**
  String get planFlatsPanel;

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
  /// **'Frame {frame} of {frames} · about {left} left on {target}'**
  String monitorFrameOf(int frame, int frames, String left, String target);

  /// Monitor Now tile: the upcoming steps
  ///
  /// In en, this message translates to:
  /// **'Up next'**
  String get monitorUpNext;

  /// Monitor Now tile with no plan: tonight's best Targets
  ///
  /// In en, this message translates to:
  /// **'Tonight\'s best'**
  String get monitorTonightsBest;

  /// Monitor Up next row: the next frame and filter
  ///
  /// In en, this message translates to:
  /// **'Frame {frame} · {filter}'**
  String monitorUpFrame(int frame, String filter);

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

  /// Monitor Tonight's best row: the Target's altitude now
  ///
  /// In en, this message translates to:
  /// **'{alt}° up'**
  String monitorAltitudeUp(String alt);

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

  /// Monitor Weather tile status word
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

  /// Monitor Last frame: the last autofocus and the temperature change since
  ///
  /// In en, this message translates to:
  /// **'Autofocus {time} · {delta} since'**
  String monitorAutofocusSince(String time, String delta);

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
