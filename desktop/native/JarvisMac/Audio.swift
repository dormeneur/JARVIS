import AVFoundation
import CoreAudio

public typealias AudioChunkCallback = @convention(c) (UnsafeMutableRawPointer?, UnsafePointer<UInt8>?, Int32) -> Void

// MARK: Microphone

/// One recording: the input node's native format converted to PCM16 LE at the requested rate.
final class Recording {
    let engine = AVAudioEngine()
    var converter: AVAudioConverter?

    func start(rate: Double, channels: AVAudioChannelCount, device: AudioDeviceID?, chunk: AudioChunkCallback, context: UnsafeMutableRawPointer?) throws {
        let input = engine.inputNode
        if let device, var id = Optional(device), let unit = input.audioUnit {
            AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &id, UInt32(MemoryLayout<AudioDeviceID>.size))
        }

        let source = input.outputFormat(forBus: 0)
        guard source.sampleRate > 0, source.channelCount > 0 else { throw MicError.noDevice }
        guard let target = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: rate, channels: channels, interleaved: true),
              let converter = AVAudioConverter(from: source, to: target) else { throw MicError.noDevice }
        self.converter = converter
        let box = UInt(bitPattern: context)

        input.installTap(onBus: 0, bufferSize: 2048, format: source) { buffer, _ in
            let capacity = AVAudioFrameCount((Double(buffer.frameLength) * rate / source.sampleRate).rounded(.up)) + 32
            guard let output = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: capacity) else { return }
            var fed = false
            var error: NSError?
            converter.convert(to: output, error: &error) { _, status in
                if fed {
                    status.pointee = .noDataNow
                    return nil
                }

                fed = true
                status.pointee = .haveData
                return buffer
            }

            guard error == nil, output.frameLength > 0, let samples = output.int16ChannelData else { return }
            let bytes = Int(output.frameLength) * Int(channels) * 2
            samples[0].withMemoryRebound(to: UInt8.self, capacity: bytes) { chunk(UnsafeMutableRawPointer(bitPattern: box), $0, Int32(bytes)) }
        }

        engine.prepare()
        try engine.start()
    }

    func stop() {
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
    }
}

enum MicError: Error {
    case noDevice
}

private func deviceID(forUID uid: String) -> AudioDeviceID? {
    var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyTranslateUIDToDevice, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    var id = AudioDeviceID(0)
    var size = UInt32(MemoryLayout<AudioDeviceID>.size)
    var cfUID = uid as CFString
    let status = withUnsafePointer(to: &cfUID) { pointer in
        AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, UInt32(MemoryLayout<CFString>.size), pointer, &size, &id)
    }
    return status == noErr && id != 0 ? id : nil
}

/// 0 = allowed, 1 = denied (System Settings → Privacy → Microphone), 2 = not asked yet.
@_cdecl("jv_mic_access")
public func jv_mic_access(_ ask: Int32) -> Int32 {
    switch AVCaptureDevice.authorizationStatus(for: .audio) {
    case .authorized:
        return 0
    case .notDetermined:
        guard ask != 0 else { return 2 }
        if Thread.isMainThread {
            AVCaptureDevice.requestAccess(for: .audio) { _ in }
            return 2
        }

        let answered = DispatchSemaphore(value: 0)
        var granted = false
        AVCaptureDevice.requestAccess(for: .audio) { allowed in
            granted = allowed
            answered.signal()
        }
        answered.wait()
        return granted ? 0 : 1
    default:
        return 1
    }
}

/// Starts recording; null with *error = 1 (blocked by privacy settings) or 2 (no usable input).
@_cdecl("jv_mic_start")
public func jv_mic_start(_ rate: Int32, _ channels: Int32, _ uid: UnsafePointer<CChar>?, _ chunk: AudioChunkCallback, _ context: UnsafeMutableRawPointer?, _ failure: UnsafeMutablePointer<Int32>?) -> UnsafeMutableRawPointer? {
    if jv_mic_access(1) != 0 {
        failure?.pointee = 1
        return nil
    }

    let recording = Recording()
    do {
        try recording.start(rate: Double(rate), channels: AVAudioChannelCount(channels), device: string(uid).flatMap { $0.isEmpty ? nil : deviceID(forUID: $0) }, chunk: chunk, context: context)
    } catch {
        recording.stop()
        failure?.pointee = 2
        return nil
    }

    return Unmanaged.passRetained(recording).toOpaque()
}

@_cdecl("jv_mic_stop")
public func jv_mic_stop(_ handle: UnsafeMutableRawPointer?) {
    guard let handle else { return }
    let recording = Unmanaged<Recording>.fromOpaque(handle).takeRetainedValue()
    recording.stop()
}

/// Input devices as JSON: [{"id": uid, "name": ..., "default": bool}].
@_cdecl("jv_mic_devices")
public func jv_mic_devices() -> UnsafeMutablePointer<CChar>? {
    var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    var size: UInt32 = 0
    AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size)
    var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
    AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &ids)

    var defaultAddress = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultInputDevice, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    var defaultID = AudioDeviceID(0)
    var defaultSize = UInt32(MemoryLayout<AudioDeviceID>.size)
    AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &defaultAddress, 0, nil, &defaultSize, &defaultID)

    var devices: [[String: Any]] = []
    for id in ids {
        var streams = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreams, mScope: kAudioDevicePropertyScopeInput, mElement: kAudioObjectPropertyElementMain)
        var streamSize: UInt32 = 0
        AudioObjectGetPropertyDataSize(id, &streams, 0, nil, &streamSize)
        guard streamSize > 0 else { continue } // output-only device

        devices.append(["id": property(id, kAudioDevicePropertyDeviceUID) ?? "", "name": property(id, kAudioObjectPropertyName) ?? "Microphone", "default": id == defaultID])
    }

    return json(devices)
}

private func property(_ id: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> String? {
    var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    var value: Unmanaged<CFString>?
    var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
    guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr, let value else { return nil }
    return value.takeRetainedValue() as String
}

// MARK: Player

/// Streams PCM16 buffers to the default output. Buffers are converted to the engine's float format
/// here, and counted until each has been heard (dataPlayedBack), so C# can wait for the tail.
final class Player {
    let engine = AVAudioEngine()
    let node = AVAudioPlayerNode()
    let format: AVAudioFormat
    let channels: Int
    private let lock = NSLock()
    private var pending = 0

    init?(rate: Double, channels: Int) {
        guard let format = AVAudioFormat(standardFormatWithSampleRate: rate, channels: AVAudioChannelCount(channels)) else { return nil }
        self.format = format
        self.channels = channels
        engine.attach(node)
        engine.connect(node, to: engine.mainMixerNode, format: format)
        engine.prepare()
        do {
            try engine.start()
        } catch {
            return nil
        }
    }

    func write(_ bytes: UnsafePointer<UInt8>, _ count: Int) {
        let frames = count / 2 / channels
        guard frames > 0, let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(frames)), let out = buffer.floatChannelData else { return }
        buffer.frameLength = AVAudioFrameCount(frames)
        bytes.withMemoryRebound(to: Int16.self, capacity: frames * channels) { samples in
            for frame in 0..<frames {
                for channel in 0..<channels {
                    out[channel][frame] = Float(Int16(littleEndian: samples[(frame * channels) + channel])) / 32768
                }
            }
        }

        lock.withLock { pending += 1 }
        node.scheduleBuffer(buffer, completionCallbackType: .dataPlayedBack) { [weak self] _ in
            guard let self else { return }
            self.lock.withLock { self.pending -= 1 }
        }

        if !node.isPlaying {
            node.play()
        }
    }

    var queued: Int { lock.withLock { pending } }

    func close() {
        node.stop()
        engine.stop()
    }
}

@_cdecl("jv_player_open")
public func jv_player_open(_ rate: Int32, _ channels: Int32) -> UnsafeMutableRawPointer? {
    guard let player = Player(rate: Double(rate), channels: Int(channels)) else { return nil }
    return Unmanaged.passRetained(player).toOpaque()
}

@_cdecl("jv_player_write")
public func jv_player_write(_ handle: UnsafeMutableRawPointer?, _ bytes: UnsafePointer<UInt8>?, _ count: Int32) {
    guard let handle, let bytes else { return }
    Unmanaged<Player>.fromOpaque(handle).takeUnretainedValue().write(bytes, Int(count))
}

/// Buffers written but not yet heard.
@_cdecl("jv_player_queued")
public func jv_player_queued(_ handle: UnsafeMutableRawPointer?) -> Int32 {
    guard let handle else { return 0 }
    return Int32(Unmanaged<Player>.fromOpaque(handle).takeUnretainedValue().queued)
}

@_cdecl("jv_player_close")
public func jv_player_close(_ handle: UnsafeMutableRawPointer?) {
    guard let handle else { return }
    Unmanaged<Player>.fromOpaque(handle).takeRetainedValue().close()
}
