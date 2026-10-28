using System.Security.Cryptography;

namespace ClipboardPlus;

public sealed class LockedException : InvalidOperationException
{
    public LockedException() : base("This item is locked. Enter your password to unlock it.") { }
}

// Password-protected storage for sensitive clips. New items are encrypted to a public key, so capture keeps working
// while the vault is locked; reading them needs the private key, which only the password (or recovery code) can unwrap.
// Nothing here can bypass the password: forgetting both the password and the recovery code means the locked items
// can only be deleted, never recovered.
public sealed class Vault
{
    internal static int Iterations = 310_000;
    internal static readonly TimeSpan UnlockTime = TimeSpan.FromMinutes(5);
    private static readonly byte[] Magic = "CPV1"u8.ToArray();
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private sealed record VaultFile(string PublicKey, string PasswordWrap, string RecoveryWrap);
    private readonly string path;
    private readonly object gate = new();
    private byte[]? privateKey;
    private long unlockedUntil;
    public Vault(string root) => path = Path.Combine(root, "vault.json");
    public bool Exists => File.Exists(path);
    public bool IsUnlocked
    {
        get
        {
            lock (gate)
            {
                if (privateKey is null) return false;
                if (Environment.TickCount64 > unlockedUntil) { Lock(); return false; }
                return true;
            }
        }
    }
    public void Lock() { lock (gate) { if (privateKey is not null) CryptographicOperations.ZeroMemory(privateKey); privateKey = null; } }
    public static void ValidateNewPassword(string password, string confirmation)
    {
        if (password.Length < 8) throw new ArgumentException("Use at least 8 characters for the password.");
        if (!string.Equals(password, confirmation, StringComparison.Ordinal)) throw new ArgumentException("The two passwords are not the same. Type them again.");
    }
    // Returns the recovery code. It is shown once and never stored in readable form.
    public string Create(string password, string confirmation)
    {
        ValidateNewPassword(password, confirmation);
        if (Exists) throw new InvalidOperationException("A password already exists.");
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var secret = key.ExportPkcs8PrivateKey();
        var code = NewRecoveryCode();
        Write(new(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(Wrap(secret, password)), Convert.ToBase64String(Wrap(secret, NormalizeCode(code)))));
        CryptographicOperations.ZeroMemory(secret); // the new vault starts locked: every use asks for the password
        return code;
    }
    public bool Unlock(string password)
    {
        var file = Read();
        var secret = Unwrap(Convert.FromBase64String(file.PasswordWrap), password);
        if (secret is null) return false;
        Open(secret); return true;
    }
    public bool ChangePassword(string current, string password, string confirmation)
    {
        ValidateNewPassword(password, confirmation);
        var file = Read();
        var secret = Unwrap(Convert.FromBase64String(file.PasswordWrap), current);
        if (secret is null) return false;
        Write(file with { PasswordWrap = Convert.ToBase64String(Wrap(secret, password)) }); Open(secret); return true;
    }
    public bool ResetWithRecoveryCode(string code, string password, string confirmation)
    {
        ValidateNewPassword(password, confirmation);
        var file = Read();
        var secret = Unwrap(Convert.FromBase64String(file.RecoveryWrap), NormalizeCode(code));
        if (secret is null) return false;
        Write(file with { PasswordWrap = Convert.ToBase64String(Wrap(secret, password)) }); Open(secret); return true;
    }
    // Removes the password. Locked items become unreadable: the caller must delete them.
    public void Destroy() { Lock(); if (File.Exists(path)) File.Delete(path); }
    public byte[] Encrypt(byte[] plain)
    {
        var file = Read();
        using var recipient = ECDiffieHellman.Create(); recipient.ImportSubjectPublicKeyInfo(Convert.FromBase64String(file.PublicKey), out _);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var aesKey = ephemeral.DeriveKeyFromHash(recipient.PublicKey, HashAlgorithmName.SHA256);
        var ephemeralPublic = ephemeral.ExportSubjectPublicKeyInfo();
        var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(aesKey, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        return [.. Magic, (byte)(ephemeralPublic.Length >> 8), (byte)ephemeralPublic.Length, .. ephemeralPublic, .. nonce, .. tag, .. cipher];
    }
    public byte[] Decrypt(byte[] blob)
    {
        byte[] secret;
        lock (gate) { if (!IsUnlocked) throw new LockedException(); secret = privateKey!.ToArray(); }
        try
        {
            if (blob.Length < 4 + 2 || !blob.AsSpan(0, 4).SequenceEqual(Magic)) throw new CryptographicException("Unrecognized locked item.");
            int keyLength = (blob[4] << 8) | blob[5], offset = 6;
            using var me = ECDiffieHellman.Create(); me.ImportPkcs8PrivateKey(secret, out _);
            using var sender = ECDiffieHellman.Create(); sender.ImportSubjectPublicKeyInfo(blob.AsSpan(offset, keyLength), out _);
            var aesKey = me.DeriveKeyFromHash(sender.PublicKey, HashAlgorithmName.SHA256); offset += keyLength;
            var nonce = blob.AsSpan(offset, 12); var tag = blob.AsSpan(offset + 12, 16); var cipher = blob.AsSpan(offset + 28);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(aesKey, 16); aes.Decrypt(nonce, cipher, tag, plain);
            Touch(); return plain;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    private void Open(byte[] secret) { lock (gate) { if (privateKey is not null) CryptographicOperations.ZeroMemory(privateKey); privateKey = secret; Touch(); } }
    private void Touch() { lock (gate) unlockedUntil = Environment.TickCount64 + (long)UnlockTime.TotalMilliseconds; }
    private VaultFile Read() => JsonSerializer.Deserialize<VaultFile>(File.ReadAllText(path)) ?? throw new InvalidOperationException("The password store is unreadable.");
    private void Write(VaultFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(file)); File.Move(path + ".tmp", path, true);
    }
    private static string NormalizeCode(string code) => new(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string NewRecoveryCode()
    {
        var chars = Enumerable.Range(0, 20).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray();
        return string.Join('-', Enumerable.Range(0, 4).Select(i => new string(chars, i * 5, 5)));
    }
    // iterations(4) | salt(16) | nonce(12) | tag(16) | cipher
    private static byte[] Wrap(byte[] secret, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16); var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[secret.Length];
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, secret, cipher, tag);
        return [.. BitConverter.GetBytes(Iterations), .. salt, .. nonce, .. tag, .. cipher];
    }
    private static byte[]? Unwrap(byte[] wrapped, string password)
    {
        try
        {
            int iterations = BitConverter.ToInt32(wrapped, 0);
            var key = Rfc2898DeriveBytes.Pbkdf2(password, wrapped.AsSpan(4, 16), iterations, HashAlgorithmName.SHA256, 32);
            var cipher = wrapped.AsSpan(48); var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16); aes.Decrypt(wrapped.AsSpan(20, 12), cipher, wrapped.AsSpan(32, 16), plain);
            return plain;
        }
        catch (CryptographicException) { return null; }
    }
}
