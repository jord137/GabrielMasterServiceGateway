using System.Collections.Concurrent;
using System.Security.Cryptography;
using GabrielGateway.Models;

namespace GabrielGateway.Security;

public class AuthManager
{
    private readonly ConcurrentDictionary<string, UserAccount> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Username, DateTime Expiry)> _sessions = new();

    public AuthManager()
    {
        // Default superadmin credentials: admin / GabrielAdmin2026!
        CreateUser("admin", "GabrielAdmin2026!", UserRole.Admin);
    }

    public bool CreateUser(string username, string password, UserRole role)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return false;

        if (_users.ContainsKey(username))
            return false;

        byte[] saltBytes = RandomNumberGenerator.GetBytes(16);
        string salt = Convert.ToBase64String(saltBytes);
        string hash = HashPassword(password, saltBytes);

        var account = new UserAccount
        {
            Username = username,
            Salt = salt,
            PasswordHash = hash,
            Role = role
        };

        return _users.TryAdd(username, account);
    }

    public bool Authenticate(string username, string password, out UserAccount? account)
    {
        account = null;
        if (!_users.TryGetValue(username, out var user))
            return false;

        byte[] saltBytes = Convert.FromBase64String(user.Salt);
        string calculatedHash = HashPassword(password, saltBytes);

        if (CryptographicOperations.FixedTimeEquals(
                Convert.FromBase64String(calculatedHash),
                Convert.FromBase64String(user.PasswordHash)))
        {
            account = user;
            return true;
        }

        return false;
    }

    public string CreateSession(string username)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = (username, DateTime.UtcNow.AddHours(4));
        return token;
    }

    public UserAccount? ValidateSession(string? token, UserRole? requiredRole = null)
    {
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var session))
            return null;

        if (DateTime.UtcNow > session.Expiry)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }

        if (_users.TryGetValue(session.Username, out var account))
        {
            if (requiredRole == UserRole.Admin && account.Role != UserRole.Admin)
                return null;
            return account;
        }

        return null;
    }

    public IEnumerable<UserAccount> GetAllUsers() => _users.Values;

    private static string HashPassword(string password, byte[] salt)
    {
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations: 100_000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 32);

        return Convert.ToBase64String(hash);
    }
}