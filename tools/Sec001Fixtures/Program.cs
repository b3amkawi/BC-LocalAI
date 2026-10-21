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
string[] knownFlags = ["be005-attempt-writer", "be007-history"];
var flags = args.Skip(3).ToArray();
if (args.Length is < 2 or > 5 || flags.Any(flag => !knownFlags.Contains(flag)) || flags.Distinct().Count() != flags.Length)
    throw new ArgumentException("Expected private output directory, schema path, optional port and optional flags be005-attempt-writer / be007-history.");
var output = Path.GetFullPath(args[0]);
var schema = Path.GetFullPath(args[1]);
var port = 3307;
if (args.Length >= 3 && (!int.TryParse(args[2], out port) || port is < 1025 or > 65535))
    throw new ArgumentException("Port must be between 1025 and 65535.");
if (!Directory.Exists(output) || Directory.EnumerateFileSystemEntries(output).Any())
    throw new InvalidOperationException("Output must be a newly created, empty private directory.");
var secrets = DatabaseSecrets.Create();
var fixtures = FixtureFactory.Create();
var schemaSql = string.Join(" ", File.ReadAllLines(schema)
    .Where(line => !line.TrimStart().StartsWith("--")));
// bootstrap.sql is used once by --init-file; local-mysql.ps1 deletes it after the first start.
var bootstrap = BootstrapScript.Create(secrets, schemaSql, fixtures);
var writerSecret = flags.Contains("be005-attempt-writer") ? FixtureFactory.RandomSecret() : null;
if (writerSecret is not null) bootstrap.AddRange(AttemptWriterBootstrap.Create(writerSecret));
var historySecret = flags.Contains("be007-history") ? FixtureFactory.RandomSecret() : null;
if (historySecret is not null)
{
    // The BE-007 migration that operators apply, read from the repository next to the SEC-001 schema.
    var migration = Path.Combine(Path.GetDirectoryName(schema)!, "..", "chat-history", "001-initial.up.sql");
    bootstrap.AddRange(ChatHistoryBootstrap.Create(historySecret, string.Join(" ", File.ReadAllLines(migration)
        .Where(line => !line.TrimStart().StartsWith("--")))));
}
File.WriteAllLines(Path.Combine(output, "bootstrap.sql"), bootstrap, new UTF8Encoding(false));
if (writerSecret is not null) File.WriteAllText(Path.Combine(output, "writer.cnf"), BootstrapScript.OptionFile("bc_auth_attempt_writer", writerSecret, port), new UTF8Encoding(false));
if (historySecret is not null) File.WriteAllText(Path.Combine(output, "history.cnf"), BootstrapScript.OptionFile(ChatHistoryBootstrap.User, historySecret, port), new UTF8Encoding(false));
// The root secret is never stored: root@localhost cannot connect over TCP under skip-name-resolve, so nobody needs it,
// and the admin account (this schema + SHUTDOWN) covers every lifecycle step. Recovery of this dev instance = new Initialize.
foreach (var (name, user, password) in new[]
{
    ("admin.cnf", "bc_sec001_admin", secrets.Admin), ("reader.cnf", "bc_auth_reader", secrets.Reader)
})
    File.WriteAllText(Path.Combine(output, name), BootstrapScript.OptionFile(user, password, port), new UTF8Encoding(false));
File.WriteAllText(Path.Combine(output, "test-accounts.json"),
    JsonSerializer.Serialize(fixtures.Select(f => new { f.Username, f.Password, f.State }),
        new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
Console.WriteLine("Generated 3 synthetic accounts. Credentials and hashes are private; none printed.");

internal sealed record FixtureCredential(string Username, string Password, string State);
internal sealed record StoredCredential(string Username, string PasswordHash);
