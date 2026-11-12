import AppKit
import ScreenCaptureKit
import UniformTypeIdentifiers

public typealias CapturedCallback = @convention(c) (UnsafeMutableRawPointer?, UnsafePointer<UInt8>?, Int32, Int32, Int32, Double, Double, Double, Double, Double) -> Void

/// 1 when Screen Recording is allowed; with ask != 0 the first call shows the system prompt.
@_cdecl("jv_screen_access")
public func jv_screen_access(_ ask: Int32) -> Int32 {
    if CGPreflightScreenCaptureAccess() { return 1 }
    if ask != 0 { _ = CGRequestScreenCaptureAccess() }
    return 0
}

/// Screenshots every display, the one under the mouse first, leaving out Jarvis's own windows
/// (the overlay, the notch). Each is scaled so its longest edge is at most maxEdge pixels and
/// JPEG-encoded. Blocks the calling thread (never call it on the main thread).
/// Returns the number of displays captured, or -1 without Screen Recording permission, -2 on failure.
@_cdecl("jv_capture")
public func jv_capture(_ maxEdge: Int32, _ quality: Double, _ captured: CapturedCallback, _ context: UnsafeMutableRawPointer?) -> Int32 {
    guard CGPreflightScreenCaptureAccess() else { return -1 }

    let done = DispatchSemaphore(value: 0)
    var count: Int32 = 0
    var failed = false
    let box = UInt(bitPattern: context)
    let mouse = CGEvent(source: nil)?.location ?? .zero

    Task {
        defer { done.signal() }
        do {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            let ours = content.applications.filter { $0.processID == ProcessInfo.processInfo.processIdentifier }
            let displays = content.displays.sorted { first, _ in CGDisplayBounds(first.displayID).contains(mouse) }
            for display in displays {
                let bounds = CGDisplayBounds(display.displayID) // points, y-down desktop
                let mode = CGDisplayCopyDisplayMode(display.displayID)
                let scale = mode.map { Double($0.pixelWidth) / Double(bounds.width) } ?? 2
                let fit = min(1, Double(maxEdge) / (max(bounds.width, bounds.height) * scale))
                let configuration = SCStreamConfiguration()
                configuration.width = Int((bounds.width * scale * fit).rounded())
                configuration.height = Int((bounds.height * scale * fit).rounded())
                configuration.showsCursor = false
                let filter = SCContentFilter(display: display, excludingApplications: ours, exceptingWindows: [])
                let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: configuration)
                guard let jpeg = encode(image, quality) else { continue }
                jpeg.withUnsafeBytes { raw in
                    captured(UnsafeMutableRawPointer(bitPattern: box), raw.bindMemory(to: UInt8.self).baseAddress, Int32(jpeg.count), Int32(image.width), Int32(image.height),
                             bounds.minX, bounds.minY, bounds.width, bounds.height, scale)
                }
                count += 1
            }
        } catch {
            failed = true
        }
    }

    done.wait()
    return failed && count == 0 ? -2 : count
}

private func encode(_ image: CGImage, _ quality: Double) -> Data? {
    let data = NSMutableData()
    guard let destination = CGImageDestinationCreateWithData(data, UTType.jpeg.identifier as CFString, 1, nil) else { return nil }
    CGImageDestinationAddImage(destination, image, [kCGImageDestinationLossyCompressionQuality: quality] as CFDictionary)
    return CGImageDestinationFinalize(destination) ? data as Data : nil
}
