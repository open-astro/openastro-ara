import Cocoa
import FlutterMacOS

class MainFlutterWindow: NSWindow {
  override func awakeFromNib() {
    let flutterViewController = FlutterViewController()
    self.contentViewController = flutterViewController

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
        if let screen = self.screen ?? NSScreen.main {
          self.setFrame(screen.visibleFrame, display: true, animate: true)
        }
        result(nil)
      case "launchpad":
        self.applyLaunchpadFrame()
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
