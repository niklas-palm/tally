import Foundation

/// A ULID: 48 bits of milliseconds then 80 random bits, in Crockford base32. Meeting ids are ULIDs so they sort by time.
enum ULID {
    private static let alphabet = Array("0123456789ABCDEFGHJKMNPQRSTVWXYZ")

    static func make(_ date: Date = Date()) -> String {
        let ms = UInt64(date.timeIntervalSince1970 * 1000)
        let time = (0..<10).map { alphabet[Int((ms >> UInt64(45 - 5 * $0)) & 31)] }
        let random = (0..<16).map { _ in alphabet[Int.random(in: 0..<32)] }
        return String(time + random)
    }

    /// When a ULID was made: its first ten characters are the milliseconds.
    static func date(_ ulid: String) -> Date {
        let ms = ulid.prefix(10).reduce(UInt64(0)) { $0 << 5 | UInt64(alphabet.firstIndex(of: $1) ?? 0) }
        return Date(timeIntervalSince1970: Double(ms) / 1000)
    }
}
