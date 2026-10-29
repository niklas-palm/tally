import AVFoundation
import AudioToolbox
import CoreAudio
import os

/// Records a meeting: everything the Mac plays, through a global Core Audio process tap that leaves Tally itself out,
/// and the default microphone, together in one private aggregate device so both run on one clock.
///
/// Channel 1 of the file is the mic, channel 2 the system audio (docs/architecture.md). While recording it writes 16-bit PCM
/// to a CAF, which a crash doesn't ruin the way it would an unfinished m4a; `encode` turns that into the m4a that is
/// uploaded. When the default input changes (AirPods connecting), the aggregate device is rebuilt around the new mic
/// and the same file carries on.
final class Recorder {
    static let sampleRate = 48_000.0

    struct Levels {
        var mic: Float = -120  // dBFS of the last buffer
        var system: Float = -120
        var lastSystemSound: Date?  // when the Mac last played anything: never, with the permission missing
        var lost = false  // the microphone went, and no other is there to record with
    }

    var levels: Levels { levelsLock.withLock { $0 } }

    // Two queues: `queue` starts, stops and rebuilds the device; `io` runs the IOProc. Stopping waits for the IO
    // thread, which would deadlock if both ran on one queue. The file is shared between them under `fileLock`.
    private let queue = DispatchQueue(label: "tally.recorder")
    private let io = DispatchQueue(label: "tally.recorder.io", qos: .userInteractive)
    private let levelsLock = OSAllocatedUnfairLock(initialState: Levels())
    private let fileLock = NSLock()
    private let format = AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: 2)!

    private var file: AVAudioFile?
    private var tap = AudioObjectID(kAudioObjectUnknown)
    private var tapUID = ""
    private var aggregate = AudioObjectID(kAudioObjectUnknown)
    private var ioProc: AudioDeviceIOProcID?
    private var micChannels = 0
    private var converter: AVAudioConverter?  // when the aggregate can't run at 48 kHz (a Bluetooth mic)
    private var inputListener: AudioObjectPropertyListenerBlock?
    private var paused = false  // the device is stopped, the file open: a microphone change waits for resume

    func start(writingTo url: URL) throws {
        try queue.sync {
            let file = try AVAudioFile(forWriting: url, settings: [
                AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: Self.sampleRate, AVNumberOfChannelsKey: 2,
                AVLinearPCMBitDepthKey: 16, AVLinearPCMIsFloatKey: false, AVLinearPCMIsNonInterleaved: false,
            ], commonFormat: .pcmFormatFloat32, interleaved: false)
            fileLock.withLock { self.file = file }
            try createTap()
            try startDevice()

            let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in self?.restartDevice() }
            var address = Self.address(kAudioHardwarePropertyDefaultInputDevice)
            AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, queue, listener)
            inputListener = listener
        }
    }

    /// Stops listening without closing the file: when a call ends, the recording can still carry on in the same file
    /// (the time between isn't in the audio).
    func pause() {
        queue.sync {
            paused = true
            stopDevice()
            levelsLock.withLock { $0 = Levels(lastSystemSound: $0.lastSystemSound) }
        }
    }

    func resume() throws {
        try queue.sync {
            paused = false
            try startDevice()
        }
    }

    func stop() {
        queue.sync {
            if let inputListener {
                var address = Self.address(kAudioHardwarePropertyDefaultInputDevice)
                AudioObjectRemovePropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &address, queue, inputListener)
            }
            inputListener = nil
            stopDevice()
            if tap != kAudioObjectUnknown { AudioHardwareDestroyProcessTap(tap) }
            tap = AudioObjectID(kAudioObjectUnknown)
            fileLock.withLock {
                file?.close()
                file = nil
            }
        }
    }

    /// Encodes a recording to the 2-channel AAC m4a that is uploaded (about 55 MB an hour at 128 kbps).
    static func encode(_ source: URL, to destination: URL) throws {
        let input = try AVAudioFile(forReading: source)
        let output = try AVAudioFile(forWriting: destination, settings: [
            AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: input.fileFormat.sampleRate,
            AVNumberOfChannelsKey: input.fileFormat.channelCount, AVEncoderBitRateKey: 128_000,
        ], commonFormat: .pcmFormatFloat32, interleaved: false)
        let buffer = AVAudioPCMBuffer(pcmFormat: input.processingFormat, frameCapacity: 48_000)!
        while input.framePosition < input.length {
            try input.read(into: buffer)
            try output.write(from: buffer)
        }
        output.close()
    }

    // MARK: - The tap and the aggregate device (on `queue`)

    private func createTap() throws {
        // Leave Tally's own sound out (its clip player). Before it has played anything it has no process object.
        var exclude: [AudioObjectID] = []
        var pid = getpid()
        var process = AudioObjectID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioObjectID>.size)
        var address = Self.address(kAudioHardwarePropertyTranslatePIDToProcessObject)
        AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address,
                                   UInt32(MemoryLayout<pid_t>.size), &pid, &size, &process)
        if process != kAudioObjectUnknown { exclude.append(process) }

        let description = CATapDescription(stereoGlobalTapButExcludeProcesses: exclude)
        description.uuid = UUID()
        description.isPrivate = true
        description.muteBehavior = .unmuted
        try check(AudioHardwareCreateProcessTap(description, &tap), "start recording your Mac's sound")
        tapUID = description.uuid.uuidString
    }

    private func startDevice() throws {
        let mic = try Self.defaultInput()
        let micUID = try Self.string(mic, kAudioDevicePropertyDeviceUID)
        micChannels = Self.inputChannels(mic)

        let description: [String: Any] = [
            kAudioAggregateDeviceNameKey: "Tally",
            kAudioAggregateDeviceUIDKey: UUID().uuidString,
            kAudioAggregateDeviceMainSubDeviceKey: micUID,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            // Not auto-start: with it the device runs only while something plays, so a quiet room records nothing.
            kAudioAggregateDeviceTapAutoStartKey: false,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: micUID]],
            kAudioAggregateDeviceTapListKey: [[kAudioSubTapUIDKey: tapUID, kAudioSubTapDriftCompensationKey: true]],
        ]
        try check(AudioHardwareCreateAggregateDevice(description as CFDictionary, &aggregate), "set up recording")

        // Ask for 48 kHz; a mic that can't do it (Bluetooth) keeps its own rate, and the buffers are converted.
        var rate = Self.sampleRate
        var address = Self.address(kAudioDevicePropertyNominalSampleRate)
        AudioObjectSetPropertyData(aggregate, &address, 0, nil, UInt32(MemoryLayout<Double>.size), &rate)
        var size = UInt32(MemoryLayout<Double>.size)
        AudioObjectGetPropertyData(aggregate, &address, 0, nil, &size, &rate)
        converter = rate == Self.sampleRate ? nil
            : AVAudioConverter(from: AVAudioFormat(standardFormatWithSampleRate: rate, channels: 2)!, to: format)

        try check(AudioDeviceCreateIOProcIDWithBlock(&ioProc, aggregate, io) { [weak self] _, input, _, _, _ in
            self?.write(input, rate: rate)
        }, "start recording")
        try check(AudioDeviceStart(aggregate, ioProc), "start recording")
    }

    private func stopDevice() {
        if let ioProc {
            AudioDeviceStop(aggregate, ioProc)
            AudioDeviceDestroyIOProcID(aggregate, ioProc)
        }
        ioProc = nil
        if aggregate != kAudioObjectUnknown { AudioHardwareDestroyAggregateDevice(aggregate) }
        aggregate = AudioObjectID(kAudioObjectUnknown)
    }

    private func restartDevice() {
        guard fileLock.withLock({ file != nil }), !paused else { return }  // a change just after stop, or while paused
        stopDevice()
        do {
            try startDevice()
            levelsLock.withLock { $0.lost = false }
        } catch {  // with no input at all, there's nothing to record until one returns
            levelsLock.withLock { $0 = Levels(lastSystemSound: $0.lastSystemSound, lost: true) }
        }
    }

    /// Mixes one IO cycle down to the file's two channels: the mic's channels come first in the aggregate's
    /// buffers, then the tap's stereo.
    private func write(_ input: UnsafePointer<AudioBufferList>, rate: Double) {
        fileLock.lock()
        defer { fileLock.unlock() }
        guard let file else { return }
        let buffers = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: input))
        let channels = buffers.map { Int($0.mNumberChannels) }
        let total = channels.reduce(0, +)
        guard total > micChannels, let frames = zip(buffers, channels).map({ Int($0.mDataByteSize) / (4 * max($1, 1)) }).min(),
              frames > 0,
              let mixed = AVAudioPCMBuffer(pcmFormat: AVAudioFormat(standardFormatWithSampleRate: rate, channels: 2)!,
                                           frameCapacity: AVAudioFrameCount(frames))
        else { return }
        mixed.frameLength = AVAudioFrameCount(frames)
        let mic = mixed.floatChannelData![0], system = mixed.floatChannelData![1]
        mic.update(repeating: 0, count: frames)
        system.update(repeating: 0, count: frames)

        var channel = 0
        for (buffer, count) in zip(buffers, channels) {
            guard let data = buffer.mData?.assumingMemoryBound(to: Float.self) else { channel += count; continue }
            for c in 0..<count {
                let isMic = channel < micChannels
                let target = isMic ? mic : system
                let weight = 1 / Float(isMic ? micChannels : total - micChannels)
                for f in 0..<frames { target[f] += data[f * count + c] * weight }
                channel += 1
            }
        }

        let (micLevel, systemLevel) = (Self.dB(mic, frames), Self.dB(system, frames))
        levelsLock.withLock { levels in
            levels.mic = micLevel
            levels.system = systemLevel
            if systemLevel > -80 { levels.lastSystemSound = Date() }
        }

        if let converter {
            let capacity = AVAudioFrameCount(Double(frames) * Self.sampleRate / rate) + 1
            guard let converted = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: capacity) else { return }
            var consumed = false
            converter.convert(to: converted, error: nil) { _, status in
                status.pointee = consumed ? .noDataNow : .haveData
                defer { consumed = true }
                return consumed ? nil : mixed
            }
            try? file.write(from: converted)
        } else {
            try? file.write(from: mixed)
        }
    }

    // MARK: - Core Audio helpers

    private static func dB(_ samples: UnsafeMutablePointer<Float>, _ count: Int) -> Float {
        var sum: Float = 0
        for i in 0..<count { sum += samples[i] * samples[i] }
        return max(-120, 10 * log10(sum / Float(count) + 1e-12))
    }

    private static func address(_ selector: AudioObjectPropertySelector,
                                _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
    }

    private static func defaultInput() throws -> AudioDeviceID {
        var device = AudioDeviceID(kAudioObjectUnknown)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        var address = address(kAudioHardwarePropertyDefaultInputDevice)
        AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &device)
        guard device != kAudioObjectUnknown else { throw AppError("No microphone is connected. Connect one and try again.") }
        return device
    }

    private static func string(_ device: AudioDeviceID, _ selector: AudioObjectPropertySelector) throws -> String {
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        var address = address(selector)
        try check(AudioObjectGetPropertyData(device, &address, 0, nil, &size, &value), "read the microphone")
        return value!.takeRetainedValue() as String
    }

    private static func inputChannels(_ device: AudioDeviceID) -> Int {
        var address = address(kAudioDevicePropertyStreamConfiguration, kAudioObjectPropertyScopeInput)
        var size: UInt32 = 0
        AudioObjectGetPropertyDataSize(device, &address, 0, nil, &size)
        let list = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { list.deallocate() }
        AudioObjectGetPropertyData(device, &address, 0, nil, &size, list)
        return UnsafeMutableAudioBufferListPointer(list.assumingMemoryBound(to: AudioBufferList.self))
            .reduce(0) { $0 + Int($1.mNumberChannels) }
    }
}

private func check(_ status: OSStatus, _ what: String) throws {
    guard status == noErr else { throw AppError("Tally couldn't \(what) (error \(status)). Try again; if it keeps happening, restart your Mac.") }
}
