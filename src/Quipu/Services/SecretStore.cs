using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Quipu.Services;

/// <summary>
/// Hugging Face tokens, encrypted with Windows DPAPI (CurrentUser scope) in
/// %LOCALAPPDATA%\Quipu\secrets.json. Kept out of quipu.yaml so the config can live in git.
/// </summary>
public sealed class SecretStore
{
    private static readonly byte[] Entropy = "Quipu.v1.hf-tokens"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, StoredToken> _tokens;

    public SecretStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quipu");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "secrets.json");
        _tokens = Load();
    }

    public string FilePath => _path;

    public IReadOnlyList<TokenInfo> List()
    {
        lock (_gate)
            return _tokens.Select(kv => new TokenInfo(kv.Key, Mask(Unprotect(kv.Value.Cipher)), kv.Value.User,
                    kv.Value.Role, kv.Value.CreatedAt, kv.Value.ValidatedAt))
                .OrderBy(t => t.Name).ToList();
    }

    public bool Exists(string name)
    {
        lock (_gate) return _tokens.ContainsKey(name);
    }

    public string? Get(string name)
    {
        lock (_gate) return _tokens.TryGetValue(name, out var t) ? Unprotect(t.Cipher) : null;
    }

    public void Set(string name, string token)
    {
        lock (_gate)
        {
            _tokens[name] = new StoredToken(Protect(token.Trim()), null, null, DateTimeOffset.Now, null);
            Save();
        }
    }

    public void MarkValidated(string name, string? user, string? role)
    {
        lock (_gate)
        {
            if (!_tokens.TryGetValue(name, out var t)) return;
            _tokens[name] = t with { User = user, Role = role, ValidatedAt = DateTimeOffset.Now };
            Save();
        }
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            var removed = _tokens.Remove(name);
            if (removed) Save();
            return removed;
        }
    }

    public static string Mask(string? token) =>
        string.IsNullOrEmpty(token) ? "" : token.Length <= 8 ? "****" : $"{token[..5]}…{token[^4..]}";

    private Dictionary<string, StoredToken> Load()
    {
        if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
        var data = JsonSerializer.Deserialize<Dictionary<string, StoredToken>>(File.ReadAllText(_path), Json);
        return new(data ?? new(), StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_tokens, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    private static string? Unprotect(string cipher)
    {
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null; // encrypted by another Windows user / machine
        }
    }

    private sealed record StoredToken(string Cipher, string? User, string? Role, DateTimeOffset CreatedAt, DateTimeOffset? ValidatedAt);
}

public sealed record TokenInfo(string Name, string Masked, string? User, string? Role, DateTimeOffset CreatedAt, DateTimeOffset? ValidatedAt);
