using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SimpleRdpManager;

public class ServerConfig
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 3389;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public int DesktopScale { get; set; } = 100;
    public int ColorDepth { get; set; } = 32;

    [JsonIgnore]
    public string DecryptedPassword
    {
        get
        {
            if (string.IsNullOrEmpty(Password)) return "";
            try
            {
                var bytes = Convert.FromBase64String(Password);
                var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(decrypted);
            }
            catch { return ""; }
        }
        set
        {
            if (string.IsNullOrEmpty(value)) { Password = ""; return; }
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            Password = Convert.ToBase64String(encrypted);
        }
    }

    // Helper to set password from UI
    public void SetPassword(string plainText) => DecryptedPassword = plainText;
    public string GetPassword() => DecryptedPassword;
}

public class ConfigManager
{
    private static readonly string ConfigPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "servers.json");

    public static List<ServerConfig> Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<List<ServerConfig>>(json) ?? new();
            }
        }
        catch { }
        return new();
    }

    public static void Save(List<ServerConfig> servers)
    {
        var json = JsonSerializer.Serialize(servers, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }
}
