import AppKit
import ApplicationServices

// MARK: Accessibility (the focused text field)

/// 1 when Jarvis may read and drive other apps; with prompt != 0 macOS shows its dialog.
@_cdecl("jv_ax_trusted")
public func jv_ax_trusted(_ prompt: Int32) -> Int32 {
    let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: prompt != 0] as CFDictionary
    return AXIsProcessTrustedWithOptions(options) ? 1 : 0
}

private func attribute(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
    var value: CFTypeRef?
    return AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success ? value : nil
}

private func focusedElement() -> AXUIElement? {
    let system = AXUIElementCreateSystemWide()
    AXUIElementSetMessagingTimeout(system, 0.6) // a hung app must not stall dictation
    guard let focused = attribute(system, kAXFocusedUIElementAttribute), CFGetTypeID(focused) == AXUIElementGetTypeID() else { return nil }
    return (focused as! AXUIElement) // swiftlint:disable:this force_cast
}

private func isSecure(_ element: AXUIElement) -> Bool {
    (attribute(element, kAXSubroleAttribute) as? String) == (kAXSecureTextFieldSubrole as String)
}

/// The focused element (retained; free with jv_ax_release), its app's name and its text.
/// Password fields are returned without their text (*secure = 1).
@_cdecl("jv_ax_focused")
public func jv_ax_focused(_ app: UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>?, _ text: UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>?, _ secure: UnsafeMutablePointer<Int32>?) -> UnsafeMutableRawPointer? {
    guard let element = focusedElement() else { return nil }
    var pid: pid_t = 0
    AXUIElementGetPid(element, &pid)
    app?.pointee = dup(NSRunningApplication(processIdentifier: pid)?.localizedName ?? "")
    let hidden = isSecure(element)
    secure?.pointee = hidden ? 1 : 0
    text?.pointee = hidden ? nil : dup(attribute(element, kAXValueAttribute) as? String)
    return Unmanaged.passRetained(element).toOpaque()
}

/// The element's current text, or null (gone, not text, or a password field).
@_cdecl("jv_ax_read")
public func jv_ax_read(_ handle: UnsafeMutableRawPointer?) -> UnsafeMutablePointer<CChar>? {
    guard let handle else { return nil }
    let element = Unmanaged<AXUIElement>.fromOpaque(handle).takeUnretainedValue()
    AXUIElementSetMessagingTimeout(element, 0.6)
    return isSecure(element) ? nil : dup(attribute(element, kAXValueAttribute) as? String)
}

/// 1 when the element still has the keyboard focus.
@_cdecl("jv_ax_is_focused")
public func jv_ax_is_focused(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    guard let handle, let now = focusedElement() else { return 0 }
    return CFEqual(Unmanaged<AXUIElement>.fromOpaque(handle).takeUnretainedValue(), now) ? 1 : 0
}

@_cdecl("jv_ax_release")
public func jv_ax_release(_ handle: UnsafeMutableRawPointer?) {
    guard let handle else { return }
    Unmanaged<AXUIElement>.fromOpaque(handle).release()
}

/// The front window's title (Accessibility), or null.
func frontWindowTitle(_ pid: pid_t) -> String? {
    let app = AXUIElementCreateApplication(pid)
    AXUIElementSetMessagingTimeout(app, 0.3)
    guard let window = attribute(app, kAXFocusedWindowAttribute), CFGetTypeID(window) == AXUIElementGetTypeID() else { return nil }
    return attribute(window as! AXUIElement, kAXTitleAttribute) as? String // swiftlint:disable:this force_cast
}

// MARK: Pasteboard

/// Everything on the general pasteboard, every item and type, to put back later.
final class PasteboardSnapshot {
    let items: [[NSPasteboard.PasteboardType: Data]]

    init() {
        items = (NSPasteboard.general.pasteboardItems ?? []).map { item in
            var types: [NSPasteboard.PasteboardType: Data] = [:]
            for type in item.types {
                types[type] = item.data(forType: type)
            }
            return types
        }
    }

    func restore() {
        let board = NSPasteboard.general
        board.clearContents()
        let items = items.map { types -> NSPasteboardItem in
            let item = NSPasteboardItem()
            for (type, data) in types {
                item.setData(data, forType: type)
            }
            return item
        }
        board.writeObjects(items)
    }
}

@_cdecl("jv_pb_save")
public func jv_pb_save() -> UnsafeMutableRawPointer? {
    Unmanaged.passRetained(onMainSync { PasteboardSnapshot() }).toOpaque()
}

/// Puts the snapshot back and frees it; restore = 0 only frees it.
@_cdecl("jv_pb_restore")
public func jv_pb_restore(_ handle: UnsafeMutableRawPointer?, _ restore: Int32) {
    guard let handle else { return }
    let snapshot = Unmanaged<PasteboardSnapshot>.fromOpaque(handle).takeRetainedValue()
    if restore != 0 { onMainSync { snapshot.restore() } }
}

/// Puts text on the pasteboard, marked transient and concealed so clipboard managers skip it
/// (nspasteboard.org). Returns the new change count.
@_cdecl("jv_pb_set")
public func jv_pb_set(_ text: UnsafePointer<CChar>?, _ transient: Int32) -> Int {
    let value = string(text) ?? ""
    return onMainSync {
        let board = NSPasteboard.general
        board.clearContents()
        board.setString(value, forType: .string)
        if transient != 0 {
            board.setData(Data(), forType: NSPasteboard.PasteboardType("org.nspasteboard.TransientType"))
            board.setData(Data(), forType: NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType"))
        }
        return board.changeCount
    }
}

@_cdecl("jv_pb_change_count")
public func jv_pb_change_count() -> Int {
    onMainSync { NSPasteboard.general.changeCount }
}

@_cdecl("jv_pb_text")
public func jv_pb_text() -> UnsafeMutablePointer<CChar>? {
    dup(onMainSync { NSPasteboard.general.string(forType: .string) })
}
