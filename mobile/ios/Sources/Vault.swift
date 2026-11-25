import Foundation
import CryptoKit
import Security
import SQLCipher
import UIKit

enum LocalError: Error { case storage, input }
private let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)
func secret(_ account: String, create: () throws -> Data) throws -> Data {
    let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "app.ctrl.messenger", kSecAttrAccount as String: account]
    let read = query.merging([kSecReturnData as String: true]) { _, v in v }
    var item: CFTypeRef?
    let status = SecItemCopyMatching(read as CFDictionary, &item)
    if status == errSecSuccess, let data = item as? Data { return data }
    guard status == errSecItemNotFound else { throw LocalError.storage }
    let data = try create()
    let insert = query.merging([kSecValueData as String: data, kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly]) { _, v in v }
    guard SecItemAdd(insert as CFDictionary, nil) == errSecSuccess else { throw LocalError.storage }
    return data
}
final class Vault {
    private var db: OpaquePointer?
    init() throws {
        let folder = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
        var path = folder.appendingPathComponent("ctrl-vault.db")
        guard sqlite3_open(path.path, &db) == SQLITE_OK else { throw LocalError.storage }
        let key = try secret("sqlcipher") { SymmetricKey(size: .bits256).withUnsafeBytes { Data($0) } }
        guard key.withUnsafeBytes({ sqlite3_key(db, $0.baseAddress, Int32($0.count)) }) == SQLITE_OK else { throw LocalError.storage }
        try exec("CREATE TABLE IF NOT EXISTS entries(scope TEXT,key TEXT,value BLOB NOT NULL,PRIMARY KEY(scope,key))")
        var flags = URLResourceValues(); flags.isExcludedFromBackup = true; try path.setResourceValues(flags)
    }
    deinit { sqlite3_close(db) }
    private func exec(_ sql: String) throws { guard sqlite3_exec(db, sql, nil, nil, nil) == SQLITE_OK else { throw LocalError.storage } }
    func run(_ scope: String, _ args: [String: Any]) throws -> Any {
        func statement(_ sql: String, key: String) throws -> OpaquePointer {
            var stmt: OpaquePointer?
            guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let s = stmt else { throw LocalError.storage }
            sqlite3_bind_text(s, 1, scope, -1, transient); sqlite3_bind_text(s, 2, key, -1, transient); return s
        }
        if let changes = args["changes"] as? [String: Any] {
            try exec("BEGIN IMMEDIATE")
            do { for (k,v) in changes {
                let s = try statement(v is NSNull ? "DELETE FROM entries WHERE scope=?1 AND key=?2" : "INSERT OR REPLACE INTO entries VALUES(?1,?2,?3)", key:k)
                defer { sqlite3_finalize(s) }
                if !(v is NSNull) {
                    let data = try JSONSerialization.data(withJSONObject:v,options:.fragmentsAllowed)
                    _ = data.withUnsafeBytes { sqlite3_bind_blob(s,3,$0.baseAddress,Int32($0.count),transient) }
                }
                guard sqlite3_step(s) == SQLITE_DONE else { throw LocalError.storage }
            }; try exec("COMMIT") } catch { try? exec("ROLLBACK"); throw error }
            return NSNull()
        }
        guard let k = args["key"] as? String else { throw LocalError.input }
        let s = try statement("SELECT value FROM entries WHERE scope=?1 AND key=?2",key:k); defer { sqlite3_finalize(s) }
        let step=sqlite3_step(s); if step == SQLITE_DONE {return NSNull()}
        guard step == SQLITE_ROW, let ptr=sqlite3_column_blob(s,0) else {throw LocalError.storage}
        return try JSONSerialization.jsonObject(with:Data(bytes:ptr,count:Int(sqlite3_column_bytes(s,0))),options:.fragmentsAllowed)
    }
}

