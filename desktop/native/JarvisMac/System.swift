import AppKit
import Security
import ServiceManagement

// MARK: Displays

/// Every display on the y-down desktop, in points, primary first:
/// [{"left","top","width","height","scale","notch": width or 0, "notchHeight", "menuBar"}].
@_cdecl("jv_screens")
public func jv_screens() -> UnsafeMutablePointer<CChar>? {
    let screens: [[String: Any]] = onMainSync {
        NSScreen.screens.map { screen in
            let frame = desktopFrame(screen)
            var notch: CGFloat = 0
            if let left = screen.auxiliaryTopLeftArea, let right = screen.auxiliaryTopRightArea {
                notch = screen.frame.width - left.width - right.width
            }
            return [
                "left": frame.minX, "top": frame.minY, "width": frame.width, "height": frame.height,
                "scale": screen.backingScaleFactor, "notch": notch, "notchHeight": screen.safeAreaInsets.top,
                "menuBar": screen.frame.maxY - screen.visibleFrame.maxY,
            ]
        }
    }
    return json(screens)
}

// MARK: The app in front

/// {"app", "bundle", "pid", "title", "fullscreen"} for the frontmost app, or null.
@_cdecl("jv_front_app")
public func jv_front_app() -> UnsafeMutablePointer<CChar>? {
    guard let app = NSWorkspace.shared.frontmostApplication else { return nil }
    let pid = app.processIdentifier
    return json([
        "app": app.localizedName ?? "", "bundle": app.bundleIdentifier ?? "", "pid": Int(pid),
        "title": frontWindowTitle(pid) ?? "", "fullscreen": coversDisplay(pid),
    ] as [String: Any])
}

/// Whether the app's frontmost normal window covers a whole display (a fullscreen video, game or
/// slideshow). Fullscreen Spaces count, maximised-but-windowed doesn't (the menu bar shows).
private func coversDisplay(_ pid: pid_t) -> Bool {
    guard pid != ProcessInfo.processInfo.processIdentifier,
          let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else { return false }
    let displays = onMainSync { NSScreen.screens.map(desktopFrame) }
    for window in windows where (window[kCGWindowOwnerPID as String] as? pid_t) == pid && (window[kCGWindowLayer as String] as? Int) == 0 {
        guard let bounds = window[kCGWindowBounds as String] as? [String: CGFloat] else { continue }
        let rect = CGRect(x: bounds["X"] ?? 0, y: bounds["Y"] ?? 0, width: bounds["Width"] ?? 0, height: bounds["Height"] ?? 0)
        return displays.contains { $0.equalTo(rect) }
    }
    return false
}

/// Running GUI apps' names, for "open/switch to X".
@_cdecl("jv_running_apps")
public func jv_running_apps() -> UnsafeMutablePointer<CChar>? {
    json(NSWorkspace.shared.runningApplications.filter { $0.activationPolicy == .regular }.compactMap { $0.localizedName })
}

/// Brings a running app forward by name; 0 when none matches.
@_cdecl("jv_activate_app")
public func jv_activate_app(_ name: UnsafePointer<CChar>?) -> Int32 {
    guard let name = string(name) else { return 0 }
    let app = NSWorkspace.shared.runningApplications.first { $0.localizedName?.caseInsensitiveCompare(name) == .orderedSame }
    return app?.activate() == true ? 1 : 0
}

/// Gives the keyboard back to the last app that isn't Jarvis (before typing into it).
@_cdecl("jv_activate_pid")
public func jv_activate_pid(_ pid: Int32) -> Int32 {
    NSRunningApplication(processIdentifier: pid)?.activate() == true ? 1 : 0
}

// MARK: Keychain

private func query(_ service: String, _ account: String) -> [String: Any] {
    [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account]
}

@_cdecl("jv_kc_save")
public func jv_kc_save(_ service: UnsafePointer<CChar>?, _ account: UnsafePointer<CChar>?, _ secret: UnsafePointer<CChar>?) -> Int32 {
    let base = query(string(service) ?? "", string(account) ?? "")
    let data = Data((string(secret) ?? "").utf8)
    SecItemDelete(base as CFDictionary)
    var add = base
    add[kSecValueData as String] = data
    add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
    return SecItemAdd(add as CFDictionary, nil)
}

@_cdecl("jv_kc_read")
public func jv_kc_read(_ service: UnsafePointer<CChar>?, _ account: UnsafePointer<CChar>?) -> UnsafeMutablePointer<CChar>? {
    var search = query(string(service) ?? "", string(account) ?? "")
    search[kSecReturnData as String] = true
    search[kSecMatchLimit as String] = kSecMatchLimitOne
    var result: CFTypeRef?
    guard SecItemCopyMatching(search as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
    return dup(String(data: data, encoding: .utf8))
}

@_cdecl("jv_kc_delete")
public func jv_kc_delete(_ service: UnsafePointer<CChar>?, _ account: UnsafePointer<CChar>?) {
    SecItemDelete(query(string(service) ?? "", string(account) ?? "") as CFDictionary)
}

// MARK: Launch at login

/// 1 enabled, 0 not, -1 unavailable (not running from an .app bundle) or waiting for approval in
/// System Settings → General → Login Items.
@_cdecl("jv_login_status")
public func jv_login_status() -> Int32 {
    switch SMAppService.mainApp.status {
    case .enabled: return 1
    case .notRegistered: return 0
    default: return -1
    }
}

@_cdecl("jv_login_set")
public func jv_login_set(_ enabled: Int32) -> Int32 {
    do {
        if enabled != 0 { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        return 1
    } catch {
        return 0
    }
}

// MARK: Spelling

/// 1 when the word is spelled right in English (US or UK).
@_cdecl("jv_spell_known")
public func jv_spell_known(_ word: UnsafePointer<CChar>?) -> Int32 {
    guard let word = string(word), !word.isEmpty else { return 0 }
    return onMainSync {
        let checker = NSSpellChecker.shared
        return ["en_US", "en_GB"].contains { language in
            checker.checkSpelling(of: word, startingAt: 0, language: language, wrap: false, inSpellDocumentWithTag: 0, wordCount: nil).location == NSNotFound
        }
    } ? 1 : 0
}
