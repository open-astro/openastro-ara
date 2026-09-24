// Post a real mouse click (or typed text) at screen-point coordinates via CoreGraphics.
// Flutter ignores System Events' synthetic `click at`, but CGEvent clicks it accepts.
// Usage: click <x> <y>            left click at global point (x,y), in points
//        click <x> <y> type <text>   click, then type text; a trailing "\n" presses Return
// Needs Accessibility for the calling terminal.
import CoreGraphics
import Foundation
let a = CommandLine.arguments
guard a.count >= 3, let x = Double(a[1]), let y = Double(a[2]) else { print("usage: click x y [type text]"); exit(2) }
let p = CGPoint(x: x, y: y)
let src = CGEventSource(stateID: .hidSystemState)
CGEvent(mouseEventSource: src, mouseType: .mouseMoved, mouseCursorPosition: p, mouseButton: .left)?.post(tap: .cghidEventTap)
usleep(80_000)
CGEvent(mouseEventSource: src, mouseType: .leftMouseDown, mouseCursorPosition: p, mouseButton: .left)?.post(tap: .cghidEventTap)
usleep(90_000)
CGEvent(mouseEventSource: src, mouseType: .leftMouseUp, mouseCursorPosition: p, mouseButton: .left)?.post(tap: .cghidEventTap)
if a.count >= 5, a[3] == "type" {
    usleep(300_000)
    var text = a[4]
    let pressReturn = text.hasSuffix("\\n")
    if pressReturn { text.removeLast(2) }
    for ch in text.utf16 {
        var c = ch
        let d = CGEvent(keyboardEventSource: src, virtualKey: 0, keyDown: true)
        d?.keyboardSetUnicodeString(stringLength: 1, unicodeString: &c); d?.post(tap: .cghidEventTap)
        let u = CGEvent(keyboardEventSource: src, virtualKey: 0, keyDown: false)
        u?.keyboardSetUnicodeString(stringLength: 1, unicodeString: &c); u?.post(tap: .cghidEventTap)
        usleep(20_000)
    }
    if pressReturn {
        usleep(150_000)
        CGEvent(keyboardEventSource: src, virtualKey: 36, keyDown: true)?.post(tap: .cghidEventTap)
        CGEvent(keyboardEventSource: src, virtualKey: 36, keyDown: false)?.post(tap: .cghidEventTap)
    }
}
