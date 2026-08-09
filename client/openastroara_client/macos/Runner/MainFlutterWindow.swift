import Cocoa
import FlutterMacOS

class MainFlutterWindow: NSWindow {
  override func awakeFromNib() {
    let flutterViewController = FlutterViewController()
    self.contentViewController = flutterViewController

    // Build-time title (the xib carries the bundle name, "openastroara");
    // Dart re-stamps it with the version ("OpenAstro Ara 0.0.1a") over the
    // window channel as soon as PackageInfo resolves.
    self.title = "OpenAstro Ara"

    // Launchpad-first sizing: the app opens on the compact launchpad (server
    // connect + profile box), and the Dart router flips the window to the
    // maximized "workstation" mode when the §25 shell mounts — via the
    // openastroara/window channel below. Maximize = the menu-bar/dock-aware
    // visibleFrame, not a separate macOS fullscreen Space.
    self.applyLaunchpadFrame()

    let channel = FlutterMethodChannel(
      name: "openastroara/window",
      binaryMessenger: flutterViewController.engine.binaryMessenger)
    channel.setMethodCallHandler { [weak self] call, result in
      guard let self = self else {
        result(nil)
        return
      }
      switch call.method {
      case "workstation":
        // Floor stops resizes below what the shell can lay out.
        self.minSize = NSSize(width: 1100, height: 700)
        // Normal-app behavior (not forced-maximized): restore the user's last
        // workstation frame when one was saved; first run opens at a
        // comfortable default clamped to the screen. The autosave name keeps
        // macOS persisting subsequent moves/resizes automatically.
        if !self.setFrameUsingName("AraWorkstationWindow") {
          if let screen = self.screen ?? NSScreen.main {
            let vis = screen.visibleFrame
            let w = min(1440, vis.width)
            let h = min(900, vis.height)
            let frame = NSRect(
              x: vis.midX - w / 2, y: vis.midY - h / 2, width: w, height: h)
            self.setFrame(frame, display: true, animate: true)
          }
        }
        self.setFrameAutosaveName("AraWorkstationWindow")
        result(nil)
      case "launchpad":
        // The compact launchpad frame must not overwrite the saved
        // workstation frame — detach the autosave name first.
        self.setFrameAutosaveName("")
        self.applyLaunchpadFrame()
        result(nil)
      case "title":
        // "OpenAstro Ara <version>" — composed Dart-side so pubspec.yaml
        // stays the single source of the version string.
        if let title = call.arguments as? String {
          self.title = title
        }
        result(nil)
      default:
        result(FlutterMethodNotImplemented)
      }
    }

    RegisterGeneratedPlugins(registry: flutterViewController)

    super.awakeFromNib()
  }

  private func applyLaunchpadFrame() {
    self.minSize = NSSize(width: 760, height: 560)
    self.setContentSize(NSSize(width: 960, height: 680))
    self.center()
  }
}
