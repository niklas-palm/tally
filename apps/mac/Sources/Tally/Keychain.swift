import Foundation
import Security

/// A few bytes in the login keychain under Tally's service name: where the sign-in is kept between launches.
enum Keychain {
    private static let service = Bundle.main.bundleIdentifier ?? "tally"

    static func load(_ account: String) -> Data? {
        var query = base(account)
        query[kSecReturnData as String] = true
        var item: CFTypeRef?
        return SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess ? item as? Data : nil
    }

    static func save(_ data: Data, as account: String) {
        delete(account)
        var item = base(account)
        item[kSecValueData as String] = data
        SecItemAdd(item as CFDictionary, nil)
    }

    static func delete(_ account: String) {
        SecItemDelete(base(account) as CFDictionary)
    }

    private static func base(_ account: String) -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword,
         kSecAttrService as String: service,
         kSecAttrAccount as String: account]
    }
}
