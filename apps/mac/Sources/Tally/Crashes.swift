import Foundation

/// The crash reports macOS writes for Tally (`~/Library/Logs/DiagnosticReports/Tally-….ips`), found at launch so the
/// owner can send the newest with a click. Reports from before Tally first looked are left alone, and a report is
/// handled, sent or not, only once: Tally remembers the newest it has seen.
enum Crashes {
    private static let folder = FileManager.default.homeDirectoryForCurrentUser.appending(path: "Library/Logs/DiagnosticReports")
    private static let seenKey = "crashesSeenUntil"

    /// The newest report not handled yet, if any.
    static func pending() -> URL? {
        guard let seen = UserDefaults.standard.object(forKey: seenKey) as? Date else {
            UserDefaults.standard.set(Date(), forKey: seenKey)  // the first look: older crashes aren't news
            return nil
        }
        return newest(in: folder, after: seen)
    }

    static func newest(in folder: URL, after date: Date) -> URL? {
        let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
        return files.filter { $0.lastPathComponent.hasPrefix("Tally") && $0.pathExtension == "ips" }
            .map { ($0, modified($0)) }
            .filter { $0.1 > date }
            .max { $0.1 < $1.1 }?.0
    }

    /// This report and every older one are handled.
    static func handled(_ report: URL) {
        UserDefaults.standard.set(modified(report), forKey: seenKey)
    }

    private static func modified(_ file: URL) -> Date {
        (try? file.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? .distantPast
    }
}
