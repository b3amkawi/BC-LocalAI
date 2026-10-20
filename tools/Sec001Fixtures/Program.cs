using System.Text;
using System.Text.Json;
using Sec001Fixtures;
using Microsoft.AspNetCore.Identity;

if (args.Length == 2 && args[0] == "verify-db")
{
    var accounts = JsonSerializer.Deserialize<FixtureCredential[]>(
        File.ReadAllText(Path.Combine(Path.GetFullPath(args[1]), "test-accounts.json")))!;
    var records = JsonSerializer.Deserialize<StoredCredential[]>(Console.In.ReadToEnd())!;
    if (accounts.Length != 3 || records.Length != 3 || records.Select(r => r.Username).Distinct().Count() != 3)
        throw new InvalidOperationException("Unexpected fixture count.");
    var hasher = FixtureFactory.CreateHasher();
    foreach (var account in accounts)
    {
        var record = records.Single(r => r.Username == account.Username);
        if (hasher.VerifyHashedPassword(record.Username, record.PasswordHash, account.Password) != PasswordVerificationResult.Success ||
            hasher.VerifyHashedPassword(record.Username, record.PasswordHash, account.Password + "wrong") != PasswordVerificationResult.Failed)
            throw new InvalidOperationException("Stored fixture verification failed.");
    }
    Console.WriteLine("Verified all 3 stored password hashes and rejected 3 incorrect passwords; no secrets printed.");
    return;
}

// Paths are provided by scripts/security/local-mysql.ps1 after ACL protection is applied.
if (args.Length != 2) throw new ArgumentException("Expected private output directory and schema path.");
var output = Path.GetFullPath(args[0]);
var schema = Path.GetFullPath(args[1]);
if (!Directory.Exists(output) || Directory.EnumerateFileSystemEntries(output).Any())
    throw new InvalidOperationException("Output must be a newly created, empty private directory.");
var rootPassword = FixtureFactory.RandomSecret();
var readerPassword = FixtureFactory.RandomSecret();
var fixtures = FixtureFactory.Create();
var schemaSql = string.Join(" ", File.ReadAllLines(schema)
    .Where(line => !line.TrimStart().StartsWith("--")));
var bootstrap = new List<string>
{
    $"ALTER USER 'root'@'localhost' IDENTIFIED BY '{rootPassword}';",
    schemaSql
};
bootstrap.AddRange(fixtures.Select(FixtureFactory.InsertSql));
bootstrap.Add($"CREATE USER 'bc_sec001_admin'@'127.0.0.1' IDENTIFIED BY '{rootPassword}';");
bootstrap.Add("GRANT ALL PRIVILEGES ON bc_localai_sec001.* TO 'bc_sec001_admin'@'127.0.0.1';");
bootstrap.Add("GRANT SHUTDOWN ON *.* TO 'bc_sec001_admin'@'127.0.0.1';");
bootstrap.Add($"CREATE USER 'bc_auth_reader'@'127.0.0.1' IDENTIFIED BY '{readerPassword}';");
bootstrap.Add("GRANT SELECT ON bc_localai_sec001.users TO 'bc_auth_reader'@'127.0.0.1';");
File.WriteAllLines(Path.Combine(output, "bootstrap.sql"), bootstrap, new UTF8Encoding(false));
foreach (var (name, user, password) in new[]
{
    ("admin.cnf", "bc_sec001_admin", rootPassword), ("reader.cnf", "bc_auth_reader", readerPassword)
})
    File.WriteAllText(Path.Combine(output, name),
        $"[client]\nuser={user}\npassword={password}\nhost=127.0.0.1\nport=3307\nprotocol=TCP\ndefault-character-set=utf8mb4\n",
        new UTF8Encoding(false));
File.WriteAllText(Path.Combine(output, "test-accounts.json"),
    JsonSerializer.Serialize(fixtures.Select(f => new { f.Username, f.Password, f.State }),
        new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
Console.WriteLine("Generated 3 synthetic accounts. Credentials and hashes are private; none printed.");

internal sealed record FixtureCredential(string Username, string Password, string State);
internal sealed record StoredCredential(string Username, string PasswordHash);
