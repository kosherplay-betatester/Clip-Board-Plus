using System.Security.Cryptography;

namespace ClipboardPlus;

internal static class VaultTests
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        // Detection: things that should be caught...
        check(SensitiveText.Detect("My card is 4111 1111 1111 1111 thanks") == "credit card number", "Card number with a valid checksum is detected");
        check(SensitiveText.Detect("4111-1111-1111-1112") is null && SensitiveText.Detect("Order 1234567890123456") is null, "Digit strings that fail the card checksum are not flagged");
        check(SensitiveText.Detect("-----BEGIN RSA PRIVATE KEY-----\nMIIE") == "private key", "Private key block is detected");
        check(SensitiveText.Detect("AKIAIOSFODNN7EXAMPLE") == "AWS access key", "AWS access key is detected");
        check(SensitiveText.Detect("ghp_" + new string('a', 36)) == "GitHub token", "GitHub token is detected");
        check(SensitiveText.Detect("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r") == "JWT token", "JWT is detected");
        check(SensitiveText.Detect("password: hunter2024") == "password" && SensitiveText.Detect("סיסמה: abc12345") == "password", "Labelled passwords are detected in English and Hebrew");
        check(SensitiveText.Detect("GB82WEST12345698765432") == "bank account (IBAN)" && SensitiveText.Detect("GB82WEST12345698765431") is null, "IBAN is detected only with a valid check digit");
        check(SensitiveText.Detect("hjhSHNSH%GS$") == "possible password" && SensitiveText.Detect("hjJHu8$%&*nm3") == "possible password" && SensitiveText.Detect("Get-ChildItem") is null, "Password-like tokens are flagged with or without a digit; hyphenated command names are not");
        check(SensitiveText.Detect("Xk9#mP2$vL7@qR") == "possible password", "A single mixed-class token is flagged as a possible password");
        // ...and ordinary content that must not be.
        check(new[] { "Hello world, meeting at 5pm", "const x = Math.max(1, 2);", "https://example.com/a?b=c", "C:\\Users\\beta\\Documents\\file.txt", "v1.2.3-beta", "user@example.com", "שלום עולם, מה שלומך?", "Order #12345 shipped on 2026-10-28", "my_variable_name" }.All(t => SensitiveText.Detect(t) is null), "Everyday text, code, links, paths and emails are not flagged");

        // The vault: password twice, wrong password, recovery code, change password.
        Vault.Iterations = 2000;
        string vaultRoot = Path.Combine(root, "vault"); Directory.CreateDirectory(vaultRoot);
        var vault = new Vault(vaultRoot);
        bool mismatch = false; try { vault.Create("correct horse", "correct hors"); } catch (ArgumentException) { mismatch = true; }
        bool tooShort = false; try { vault.Create("short", "short"); } catch (ArgumentException) { tooShort = true; }
        check(mismatch && tooShort && !vault.Exists, "A password must be typed twice identically and be at least 8 characters");
        string code = vault.Create("correct horse", "correct horse");
        check(vault.Exists && !vault.IsUnlocked && System.Text.RegularExpressions.Regex.IsMatch(code, @"^[A-Z2-9]{5}(-[A-Z2-9]{5}){3}$"), "Creating a password yields a recovery code in the expected format");
        var secret = RandomNumberGenerator.GetBytes(500);
        var blob = vault.Encrypt(secret);
        vault.Lock();
        bool refused = false; try { vault.Decrypt(blob); } catch (LockedException) { refused = true; }
        check(refused && vault.Encrypt([1, 2, 3]).Length > 0, "While locked, items can still be encrypted but never decrypted");
        check(!vault.Unlock("wrong password") && !vault.IsUnlocked && vault.Unlock("correct horse") && vault.Decrypt(blob).SequenceEqual(secret), "Only the right password unlocks and returns the original bytes");
        vault.Lock();
        check(!vault.ResetWithRecoveryCode("AAAAA-AAAAA-AAAAA-AAAAA", "new password!", "new password!") && vault.ResetWithRecoveryCode(code.ToLowerInvariant(), "new password!", "new password!"), "A wrong recovery code is refused; the right one (any case) sets a new password");
        vault.Lock();
        check(!vault.Unlock("correct horse") && vault.Unlock("new password!") && vault.Decrypt(blob).SequenceEqual(secret), "After recovery the old password stops working and existing items are still readable");
        check(vault.ChangePassword("new password!", "third password", "third password") && !vault.ChangePassword("bad", "x password", "x password"), "Changing the password needs the current one");
        check(new Vault(vaultRoot).Unlock("third password"), "The password store survives a restart");

        // Store: Lock mode keeps sensitive text encrypted behind the vault, hidden from search and previews.
        string secretText = "Card 4111 1111 1111 1111 PIN 9876";
        var lockedSettings = new AppSettings { SensitiveMode = "Lock" };
        using (var store = new HistoryStore(Path.Combine(root, "sensitive"), lockedSettings))
        {
            Vault.Iterations = 2000;
            store.Vault.Create("store password", "store password");
            long plain = await store.AddAsync(new() { Text = "Normal note about lunch", Source = "Notepad" });
            long hidden = await store.AddAsync(new() { Text = secretText, Source = "Chrome" });
            var rows = await store.QueryAsync(new());
            var row = rows.Single(r => r.Id == hidden);
            check(row.Summary.Locked && !row.Title.Contains("4111") && !row.Preview.Contains("4111") && !rows.Single(r => r.Id == plain).Summary.Locked, "Sensitive text is stored as a locked row whose title and preview reveal nothing");
            check((await store.QueryAsync(new("4111"))).Count == 0 && (await store.QueryAsync(new("9876"))).Count == 0 && (await store.QueryAsync(new("lunch"))).Count == 1, "Locked content is not searchable, ordinary content still is");
            check(await MainWindow.VerifyLockedRowAsync(store, row, rows.Single(r => r.Id == plain)), "Locked rows show an inline password field (normal rows do not); a wrong password is refused and the vault stays locked");
            store.Vault.Lock();
            bool blocked = false; try { await store.GetAsync(hidden); } catch (LockedException) { blocked = true; }
            check(blocked && (await store.GetAsync(plain))!.Text == "Normal note about lunch", "A locked item cannot be read without the password; other items are unaffected");
            check(store.Vault.Unlock("store password") && (await store.GetAsync(hidden))!.Text == secretText, "After unlocking, the exact original text is returned");
            await store.RepairImagesBatchAsync(); while (await store.RepairGarbledTextBatchAsync()) { }
            check((await store.GetAsync(hidden))!.Text == secretText && await store.LockedCountAsync() == 1, "Background repair passes leave locked items untouched");
            long snippet = await store.AddAsync(new() { Text = secretText, Name = "My card", Source = "Snippet" }, true);
            check(snippet != 0 && !(await store.QueryAsync(new("card"))).Single(r => r.Id == snippet).Summary.Locked, "Snippets you create on purpose are never screened or locked");
            // Reset path: destroying the vault deletes exactly the protected items.
            store.Vault.Destroy();
            check(await store.DeleteLockedAsync() == 1 && !store.Vault.Exists && (await store.GetAsync(plain)) is not null, "Reset removes the password and only the protected items");
            check(await store.AddAsync(new() { Text = "Card 4111 1111 1111 1111 again", Source = "Chrome" }) == 0, "Lock mode without a password never stores sensitive text in the clear");
        }
        using (var skip = new HistoryStore(Path.Combine(root, "sensitive-skip"), new AppSettings { SensitiveMode = "Skip" }))
            check(await skip.AddAsync(new() { Text = "AKIAIOSFODNN7EXAMPLE" }) == 0 && await skip.AddAsync(new() { Text = "fine" }) != 0, "Skip mode drops sensitive content and keeps the rest");
        using (var off = new HistoryStore(Path.Combine(root, "sensitive-off"), new()))
            check(await off.AddAsync(new() { Text = "AKIAIOSFODNN7EXAMPLE" }) != 0, "With protection off nothing changes");
        Vault.Iterations = 310_000;

        // Updates: release parsing, quiet-until-next-version, and verified downloads.
        const string release = """{"tag_name":"v9.9.9","body":"Notes","assets":[{"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/x/y/releases/download/v9.9.9/SHA256SUMS.txt"},{"name":"ClipboardPlus-Setup-9.9.9-win-x64.exe","browser_download_url":"https://github.com/x/y/releases/download/v9.9.9/ClipboardPlus-Setup-9.9.9-win-x64.exe"},{"name":"ClipboardPlus-win-x64.zip","browser_download_url":"https://github.com/x/y/a.zip"}]}""";
        var info = UpdateChecker.ParseRelease(release);
        check(info.Version == new Version(9, 9, 9) && info.InstallerUrl!.EndsWith("Setup-9.9.9-win-x64.exe") && info.ChecksumUrl!.EndsWith("SHA256SUMS.txt"), "Release JSON yields the installer and checksum assets");
        check(UpdateChecker.ShouldPrompt(info, new Version(1, 1, 6), "") && !UpdateChecker.ShouldPrompt(info, new Version(1, 1, 6), "9.9.9") && !UpdateChecker.ShouldPrompt(info, new Version(9, 9, 9), "") && UpdateChecker.ShouldPrompt(info with { Version = new(10, 0, 0) }, new Version(1, 1, 6), "9.9.9"), "The prompt appears for a newer version, stays quiet after 'don't remind me', and returns for the next version");
        check(!UpdateChecker.ShouldPrompt(info with { ChecksumUrl = null }, new Version(1, 1, 6), ""), "Releases without a published checksum are not offered for automatic install");
        check(UpdateChecker.TrustedUrl("https://github.com/a/b") && UpdateChecker.TrustedUrl("https://objects.githubusercontent.com/x") && !UpdateChecker.TrustedUrl("http://github.com/a") && !UpdateChecker.TrustedUrl("https://evil.example/github.com"), "Only HTTPS GitHub hosts are trusted for downloads");
        string file = Path.Combine(root, "ClipboardPlus-Setup-9.9.9-win-x64.exe"); await File.WriteAllBytesAsync(file, [1, 2, 3, 4]);
        string good = Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])).ToLowerInvariant();
        check(UpdateChecker.VerifyChecksum(file, $"{good} *ClipboardPlus-Setup-9.9.9-win-x64.exe\nabc *other.zip\n", "ClipboardPlus-Setup-9.9.9-win-x64.exe") && !UpdateChecker.VerifyChecksum(file, $"{new string('0', 64)} *ClipboardPlus-Setup-9.9.9-win-x64.exe", "ClipboardPlus-Setup-9.9.9-win-x64.exe") && !UpdateChecker.VerifyChecksum(file, "abc *other.zip", "ClipboardPlus-Setup-9.9.9-win-x64.exe"), "A download is accepted only when its SHA-256 matches the release checksum file");
    }
}
