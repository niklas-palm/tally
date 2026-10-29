import AVFoundation
import Foundation

/// Plays a speaker's clips back to back, streamed from their links: play, pause, and stop when they end or another
/// speaker's start.
@MainActor @Observable final class ClipPlayer {
    private(set) var current: [URL] = []  // the clips loaded
    private(set) var isPlaying = false
    private var player: AVQueuePlayer?
    private var ended: NSObjectProtocol?

    func toggle(_ clips: [URL]) {
        if current == clips, let player {
            isPlaying ? player.pause() : player.play()
            isPlaying.toggle()
            return
        }
        stop()
        let items = clips.map { AVPlayerItem(url: $0) }
        guard let last = items.last else { return }
        player = AVQueuePlayer(items: items)
        current = clips
        isPlaying = true
        ended = NotificationCenter.default.addObserver(forName: AVPlayerItem.didPlayToEndTimeNotification, object: last, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.stop() }
        }
        player?.play()
    }

    func isPlaying(_ clips: [URL]) -> Bool { isPlaying && current == clips }

    func stop() {
        player?.pause()
        player = nil
        current = []
        isPlaying = false
        if let ended { NotificationCenter.default.removeObserver(ended) }
        ended = nil
    }
}
