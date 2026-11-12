import AppKit
import Foundation

// The C ABI every export uses: UTF-8 strings in, malloc'd UTF-8 strings out (freed with jv_free),
// plain numbers and C function pointers. Nothing Swift-specific crosses the boundary.

public typealias VoidCallback = @convention(c) () -> Void
public typealias IntCallback = @convention(c) (Int32) -> Void
public typealias TextCallback = @convention(c) (Int32, UnsafePointer<CChar>?) -> Void

func onMain(_ work: @escaping () -> Void) {
    if Thread.isMainThread { work() } else { DispatchQueue.main.async(execute: work) }
}

func onMainSync<T>(_ work: () -> T) -> T {
    Thread.isMainThread ? work() : DispatchQueue.main.sync(execute: work)
}

func string(_ pointer: UnsafePointer<CChar>?) -> String? {
    pointer.map { String(cString: $0) }
}

func dup(_ value: String?) -> UnsafeMutablePointer<CChar>? {
    value.flatMap { strdup($0) }
}

func json(_ value: Any) -> UnsafeMutablePointer<CChar>? {
    guard let data = try? JSONSerialization.data(withJSONObject: value), let text = String(data: data, encoding: .utf8) else { return nil }
    return dup(text)
}

func parseJSON(_ pointer: UnsafePointer<CChar>?) -> Any? {
    guard let pointer else { return nil }
    return try? JSONSerialization.jsonObject(with: Data(bytes: pointer, count: strlen(pointer)))
}

func color(_ rgb: UInt32, _ alpha: CGFloat = 1) -> CGColor {
    CGColor(srgbRed: CGFloat((rgb >> 16) & 0xFF) / 255, green: CGFloat((rgb >> 8) & 0xFF) / 255, blue: CGFloat(rgb & 0xFF) / 255, alpha: alpha)
}

/// Height of the primary display: Cocoa's y-up coordinates flip against it to the CG/Windows-style
/// y-down desktop that Jarvis.Core works in (origin at the primary display's top-left, in points).
func primaryHeight() -> CGFloat {
    NSScreen.screens.first?.frame.height ?? 0
}

/// A desktop point (y-down) as a Cocoa screen point (y-up).
func cocoaPoint(_ x: Double, _ y: Double) -> NSPoint {
    NSPoint(x: x, y: primaryHeight() - y)
}

/// A screen's frame on the y-down desktop.
func desktopFrame(_ screen: NSScreen) -> CGRect {
    let frame = screen.frame
    return CGRect(x: frame.minX, y: primaryHeight() - frame.maxY, width: frame.width, height: frame.height)
}

@_cdecl("jv_free")
public func jv_free(_ pointer: UnsafeMutableRawPointer?) {
    free(pointer)
}

/// Key equivalents (⌘C, ⌘V, ⌘A, ⌘Z…) only reach text fields through the main menu, so even a
/// menu-bar app needs one: Settings and the notch composer would refuse to paste without it.
private func editMenu() -> NSMenu {
    let main = NSMenu()
    // The first item is always the app menu; left empty so ⌘Q can't skip Jarvis's own Quit (which cleans up).
    let appItem = NSMenuItem()
    appItem.submenu = NSMenu()
    main.addItem(appItem)

    let edit = NSMenu(title: "Edit")
    edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
    let redo = edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "z")
    redo.keyEquivalentModifierMask = [.command, .shift]
    edit.addItem(.separator())
    edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
    edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
    edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
    edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
    let editItem = NSMenuItem()
    editItem.submenu = edit
    main.addItem(editItem)
    return main
}

@_cdecl("jv_app_run")
public func jv_app_run(_ ready: VoidCallback) {
    let app = NSApplication.shared
    app.setActivationPolicy(.accessory) // menu bar only: no Dock icon, no app menu
    app.mainMenu = editMenu()
    DispatchQueue.main.async { ready() }
    app.run()
}

/// Ends the app loop: jv_app_run returns on the main thread, so the caller can clean up.
@_cdecl("jv_app_quit")
public func jv_app_quit() {
    onMain {
        NSApp.stop(nil)
        // stop takes effect after the next event; post one so it happens now.
        NSApp.postEvent(NSEvent.otherEvent(with: .applicationDefined, location: .zero, modifierFlags: [], timestamp: 0, windowNumber: 0, context: nil, subtype: 0, data1: 0, data2: 0)!, atStart: true)
    }
}

/// Exits without running C++ static destructors: ggml (Whisper) aborts in them after its
/// Metal device is gone. Everything Jarvis owns is disposed before this.
@_cdecl("jv_exit")
public func jv_exit(_ code: Int32) -> Never {
    fflush(stdout)
    fflush(stderr)
    _exit(code)
}

@_cdecl("jv_on_main")
public func jv_on_main(_ work: @convention(c) (UnsafeMutableRawPointer?) -> Void, _ context: UnsafeMutableRawPointer?) {
    let box = UInt(bitPattern: context)
    DispatchQueue.main.async { work(UnsafeMutableRawPointer(bitPattern: box)) }
}

@_cdecl("jv_open_url")
public func jv_open_url(_ url: UnsafePointer<CChar>?) -> Int32 {
    guard let text = string(url) else { return 0 }
    let target = text.hasPrefix("/") ? URL(fileURLWithPath: text) : URL(string: text)
    guard let target else { return 0 }
    return onMainSync { NSWorkspace.shared.open(target) } ? 1 : 0
}

@_cdecl("jv_beep")
public func jv_beep() {
    onMain { NSSound.beep() }
}

/// Debugging aid (JARVIS_SNAPSHOT): renders Jarvis's own windows (notch, overlays, Settings) to PNGs
/// in `folder`, without Screen Recording permission (only our views are drawn, never the screen).
@_cdecl("jv_debug_snapshot")
public func jv_debug_snapshot(_ folder: UnsafePointer<CChar>?) {
    guard let folder = string(folder) else { return }
    onMain {
        func write(_ view: NSView, _ name: String) {
            guard let layer = view.layer, view.bounds.width > 0, view.bounds.height > 0 else { return }
            let scale = view.window?.backingScaleFactor ?? 2
            let width = Int(view.bounds.width * scale), height = Int(view.bounds.height * scale)
            guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return }
            // A dark backdrop, so the black notch and white text read against it.
            context.setFillColor(CGColor(gray: 0.35, alpha: 1))
            context.fill(CGRect(x: 0, y: 0, width: width, height: height))
            context.scaleBy(x: scale, y: scale)
            if view.isFlipped {
                context.translateBy(x: 0, y: view.bounds.height)
                context.scaleBy(x: 1, y: -1)
            }
            layer.render(in: context)
            if let image = context.makeImage(), let destination = CGImageDestinationCreateWithURL(URL(fileURLWithPath: folder).appendingPathComponent(name) as CFURL, "public.png" as CFString, 1, nil) {
                CGImageDestinationAddImage(destination, image, nil)
                CGImageDestinationFinalize(destination)
            }
        }

        if let notch = Notch.shared?.panel.contentView { write(notch, "notch.png") }
        for (index, window) in (Overlay.shared?.windows ?? []).enumerated() { write(window.root, "overlay-\(index).png") }
        if let settings = SettingsWindow.shared?.window?.contentView { write(settings, "settings.png") }
    }
}
