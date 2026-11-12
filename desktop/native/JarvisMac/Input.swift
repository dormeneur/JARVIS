import AppKit
import ApplicationServices

/// Marks events Jarvis posts itself, so the tap (and the "user took back control" watch) ignores them.
private let ourEvents: Int64 = 0x4A41_5256 // "JARV"

private func source() -> CGEventSource? {
    let source = CGEventSource(stateID: .hidSystemState)
    source?.userData = ourEvents
    return source
}

// MARK: Event tap

/// (kind: 1 key down, 2 key up, 3 modifiers changed; key code; modifier flags; milliseconds)
public typealias KeyCallback = @convention(c) (Int32, Int32, UInt64, Double) -> Void

private final class Tap {
    static var shared: Tap?
    var port: CFMachPort?
    let callback: KeyCallback

    init(callback: @escaping KeyCallback) {
        self.callback = callback
    }
}

/// Listens to the keyboard on its own thread. Listen-only: keys are observed, never swallowed.
/// Needs Input Monitoring; returns 0 when it isn't granted (the prompt is shown once).
@_cdecl("jv_tap_start")
public func jv_tap_start(_ callback: KeyCallback) -> Int32 {
    if !CGPreflightListenEventAccess() {
        _ = CGRequestListenEventAccess()
        return 0
    }

    let tap = Tap(callback: callback)
    Tap.shared = tap
    let started = DispatchSemaphore(value: 0)
    var ok = false
    let thread = Thread {
        let mask = (1 << CGEventType.keyDown.rawValue) | (1 << CGEventType.keyUp.rawValue) | (1 << CGEventType.flagsChanged.rawValue)
        guard let port = CGEvent.tapCreate(tap: .cghidEventTap, place: .headInsertEventTap, options: .listenOnly, eventsOfInterest: CGEventMask(mask), callback: { _, type, event, _ in
            guard let tap = Tap.shared else { return Unmanaged.passUnretained(event) }
            switch type {
            case .tapDisabledByTimeout, .tapDisabledByUserInput:
                // macOS turns off a tap that was slow once; turn it straight back on.
                if let port = tap.port { CGEvent.tapEnable(tap: port, enable: true) }
            case .keyDown, .keyUp, .flagsChanged:
                if event.getIntegerValueField(.eventSourceUserData) != ourEvents {
                    let kind: Int32 = type == .keyDown ? 1 : type == .keyUp ? 2 : 3
                    tap.callback(kind, Int32(event.getIntegerValueField(.keyboardEventKeycode)), event.flags.rawValue, ProcessInfo.processInfo.systemUptime * 1000)
                }
            default:
                break
            }
            return Unmanaged.passUnretained(event)
        }, userInfo: nil) else {
            started.signal()
            return
        }

        tap.port = port
        ok = true
        CFRunLoopAddSource(CFRunLoopGetCurrent(), CFMachPortCreateRunLoopSource(nil, port, 0), .commonModes)
        CGEvent.tapEnable(tap: port, enable: true)
        started.signal()
        CFRunLoopRun()
    }
    thread.name = "Jarvis keyboard tap"
    thread.qualityOfService = .userInteractive
    thread.start()
    started.wait()
    return ok ? 1 : 0
}

@_cdecl("jv_input_access")
public func jv_input_access() -> Int32 {
    CGPreflightListenEventAccess() ? 1 : 0
}

// MARK: Mouse

/// The mouse on the y-down desktop, in points.
@_cdecl("jv_mouse")
public func jv_mouse(_ x: UnsafeMutablePointer<Double>?, _ y: UnsafeMutablePointer<Double>?) {
    let location = CGEvent(source: nil)?.location ?? .zero
    x?.pointee = location.x
    y?.pointee = location.y
}

/// Bit 0: left button down, bit 1: right.
@_cdecl("jv_mouse_buttons")
public func jv_mouse_buttons() -> Int32 {
    Int32(NSEvent.pressedMouseButtons & 0x3)
}

/// Seconds since the last real keyboard or mouse input.
@_cdecl("jv_idle_seconds")
public func jv_idle_seconds() -> Double {
    CGEventSource.secondsSinceLastEventType(.hidSystemState, eventType: CGEventType(rawValue: ~0)!)
}

// MARK: Posting input (needs Accessibility)

/// kind: 0 move, 1 click, 2 double-click, 3 right-click.
@_cdecl("jv_post_mouse")
public func jv_post_mouse(_ kind: Int32, _ x: Double, _ y: Double) {
    let at = CGPoint(x: x, y: y)
    let source = source()
    CGEvent(mouseEventSource: source, mouseType: .mouseMoved, mouseCursorPosition: at, mouseButton: .left)?.post(tap: .cghidEventTap)
    guard kind > 0 else { return }

    let right = kind == 3
    let (down, up, button): (CGEventType, CGEventType, CGMouseButton) = right ? (.rightMouseDown, .rightMouseUp, .right) : (.leftMouseDown, .leftMouseUp, .left)
    for click in 1...(kind == 2 ? 2 : 1) {
        for type in [down, up] {
            let event = CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: at, mouseButton: button)
            event?.setIntegerValueField(.mouseEventClickState, value: Int64(click))
            event?.post(tap: .cghidEventTap)
        }
    }
}

@_cdecl("jv_post_scroll")
public func jv_post_scroll(_ lines: Int32) {
    CGEvent(scrollWheelEvent2Source: source(), units: .line, wheelCount: 1, wheel1: lines, wheel2: 0, wheel3: 0)?.post(tap: .cghidEventTap)
}

/// Types text as Unicode key events (any layout, any character).
@_cdecl("jv_post_text")
public func jv_post_text(_ text: UnsafePointer<CChar>?) {
    guard let text = string(text) else { return }
    let source = source()
    let units = Array(text.utf16)
    var index = 0
    while index < units.count {
        // Key events carry at most 20 UTF-16 units each.
        let slice = Array(units[index..<min(index + 20, units.count)])
        for down in [true, false] {
            let event = CGEvent(keyboardEventSource: source, virtualKey: 0, keyDown: down)
            event?.keyboardSetUnicodeString(stringLength: slice.count, unicodeString: slice)
            event?.post(tap: .cghidEventTap)
        }
        index += slice.count
    }
}

/// A key press with modifiers (CGEventFlags bits), e.g. key 9 (V) + command.
@_cdecl("jv_post_key")
public func jv_post_key(_ key: Int32, _ flags: UInt64) {
    let source = source()
    for down in [true, false] {
        let event = CGEvent(keyboardEventSource: source, virtualKey: CGKeyCode(key), keyDown: down)
        event?.flags = CGEventFlags(rawValue: flags)
        event?.post(tap: .cghidEventTap)
    }
}

/// Modifier flags held right now (CGEventFlags bits).
@_cdecl("jv_modifiers")
public func jv_modifiers() -> UInt64 {
    CGEventSource.flagsState(.hidSystemState).rawValue
}

/// Seconds since the last real key press (ours excluded: they're posted with our own user data,
/// but that doesn't affect the system counter, so callers compare against their own last post).
@_cdecl("jv_seconds_since_key")
public func jv_seconds_since_key() -> Double {
    CGEventSource.secondsSinceLastEventType(.hidSystemState, eventType: .keyDown)
}
