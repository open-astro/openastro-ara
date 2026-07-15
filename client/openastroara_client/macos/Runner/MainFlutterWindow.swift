import Cocoa
import FlutterMacOS

class MainFlutterWindow: NSWindow {
  override func awakeFromNib() {
    let flutterViewController = FlutterViewController()
    self.contentViewController = flutterViewController

    // WILMA is a desktop workstation UI (playbook §25): the xib's small
    // default frame overflows the shell's layout, so open filling the whole
    // screen (the menu-bar/dock-aware visibleFrame — maximized, without
    // forcing a separate macOS fullscreen Space) and stop resizes below what
    // the shell can lay out.
    self.minSize = NSSize(width: 1100, height: 700)
    if let screen = self.screen ?? NSScreen.main {
      self.setFrame(screen.visibleFrame, display: true)
    } else {
      self.setFrame(self.frame, display: true)
    }

    RegisterGeneratedPlugins(registry: flutterViewController)

    super.awakeFromNib()
  }
}
