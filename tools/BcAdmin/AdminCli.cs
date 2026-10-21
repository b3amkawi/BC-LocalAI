using MySqlConnector;

namespace BcAdmin;

/// <summary>Where a new password comes from. Never the command line: arguments end up in shell history and process lists.</summary>
internal interface IPasswordSource
{
    /// <summary>Reads a password without echoing it, or returns null when that is impossible (no interactive console).</summary>
    string? ReadHidden(string prompt);
}

internal static class ExitCodes
{
    internal const int Ok = 0, Unexpected = 1, Usage = 2, NotFound = 3, Conflict = 4, Database = 5;
}

/// <summary>
/// SEC-003 admin account script. Output names the action, the target id and the audit record, never a password, hash or stamp.
/// </summary>
internal sealed class AdminCli(TextReader stdin, TextWriter stdout, TextWriter stderr, IPasswordSource passwords,
    Func<string, IAdminAccountStore> storeFactory, string osUser)
{
    private static readonly string[] Flags = ["--password-stdin"];
    private static readonly string[] Options =
        ["--db-config", "--database", "--actor", "--user", "--username", "--display-name", "--role", "--writer-user", "--writer-host"];

    internal const string Usage = """
        Usage: bc-admin <command> --db-config <full path> [--database <name>] --actor <your name> [options]
          create          --username <name> --display-name <text> [--role User|Admin] [--password-stdin]
          disable         --user <id|username>      (also ends every session)
          enable          --user <id|username>
          reset-password  --user <id|username> [--password-stdin]   (also ends every session)
          set-role        --user <id|username> --role User|Admin    (also ends every session)
          revoke-sessions --user <id|username>
        bc-admin print-grants --writer-host <host> [--writer-user bc_admin_writer]
        Passwords are typed at a hidden prompt (twice) or read from standard input with --password-stdin; never as arguments.
        """;

    internal async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h") { await stdout.WriteLineAsync(Usage); return args.Length == 0 ? ExitCodes.Usage : ExitCodes.Ok; }
        if (!TryParse(args.Skip(1).ToArray(), out var options, out var flags, out var error)) return await Fail(ExitCodes.Usage, error);

        try
        {
            var command = args[0];
            if (command == "print-grants") return await PrintGrants(options);
            var action = command switch
            {
                "create" => AdminAction.Create, "disable" => AdminAction.Disable, "enable" => AdminAction.Enable,
                "reset-password" => AdminAction.ResetPassword, "set-role" => AdminAction.SetRole, "revoke-sessions" => AdminAction.RevokeSessions,
                _ => (AdminAction?)null
            };
            if (action is null) return await Fail(ExitCodes.Usage, $"Unknown command. {Usage}");
            if (!options.TryGetValue("--db-config", out var configPath)) return await Fail(ExitCodes.Usage, "--db-config is required.");
            if (!options.TryGetValue("--actor", out var actor) || !AccountPolicy.IsValidActor(actor)) return await Fail(ExitCodes.Usage, "--actor <your name> is required (up to 128 characters).");
            var allowed = action switch
            {
                AdminAction.Create => new[] { "--username", "--display-name", "--role" },
                AdminAction.SetRole => ["--user", "--role"],
                _ => ["--user"]
            };
            var extra = options.Keys.Except(["--db-config", "--database", "--actor", .. allowed]).FirstOrDefault();
            if (extra is not null) return await Fail(ExitCodes.Usage, $"{extra} is not used by {command}.");
            var needsPassword = action is AdminAction.Create or AdminAction.ResetPassword;
            if (flags.Contains("--password-stdin") && !needsPassword) return await Fail(ExitCodes.Usage, $"--password-stdin is not used by {command}.");

            var audit = new AuditContext(actor, osUser);
            if (action == AdminAction.Create) return await Create(options, flags, audit, configPath, cancellationToken);

            if (!options.TryGetValue("--user", out var target) || !(Guid.TryParseExact(target, "D", out _) || AccountPolicy.IsValidUsername(target)))
                return await Fail(ExitCodes.Usage, "--user <id|username> is required.");
            string? value = null;
            if (action == AdminAction.SetRole)
            {
                if (!options.TryGetValue("--role", out value) || !AccountPolicy.IsValidRole(value)) return await Fail(ExitCodes.Usage, "--role must be User or Admin.");
            }
            else if (action == AdminAction.ResetPassword)
            {
                var password = ReadPassword(flags, Guid.TryParseExact(target, "D", out _) ? null : target, out error);
                if (password is null) return await Fail(ExitCodes.Usage, error);
                value = AccountPolicy.HashPassword(password);
            }

            var store = storeFactory(DbConfig.Load(configPath, options.GetValueOrDefault("--database")));
            var userId = await store.ResolveUserIdAsync(target, cancellationToken);
            if (userId is null) return await Fail(ExitCodes.NotFound, "Account not found. Nothing changed.");
            var result = await store.ChangeAsync(userId, action.Value, value, audit, cancellationToken);
            if (!result.Found) return await Fail(ExitCodes.NotFound, "Account not found. Nothing changed.");
            await stdout.WriteLineAsync($"OK {AdminActionText(action.Value)} user={result.UserId} sessions-revoked=yes audit={result.AuditId}");
            return ExitCodes.Ok;
        }
        catch (AdminConfigException exception) { return await Fail(ExitCodes.Database, exception.Message); }
        catch (MySqlException exception) { return await Fail(exception.Number == 1062 ? ExitCodes.Conflict : ExitCodes.Database, DatabaseMessage(exception)); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Type name only: messages may carry connection details. A connection lost during COMMIT leaves the outcome unknown,
            // so do not promise that nothing changed.
            return await Fail(ExitCodes.Unexpected, $"Unexpected error ({exception.GetType().Name}). Check the account and the audit log before retrying.");
        }
    }

    private async Task<int> Create(Dictionary<string, string> options, HashSet<string> flags, AuditContext audit, string configPath, CancellationToken cancellationToken)
    {
        if (!options.TryGetValue("--username", out var username) || !AccountPolicy.IsValidUsername(username))
            return await Fail(ExitCodes.Usage, "--username is required: up to 128 of A-Z a-z 0-9 . _ -");
        if (!options.TryGetValue("--display-name", out var displayName) || !AccountPolicy.IsValidDisplayName(displayName))
            return await Fail(ExitCodes.Usage, "--display-name is required (up to 200 characters).");
        var role = options.GetValueOrDefault("--role", "User");
        if (!AccountPolicy.IsValidRole(role)) return await Fail(ExitCodes.Usage, "--role must be User or Admin.");
        var password = ReadPassword(flags, username, out var error);
        if (password is null) return await Fail(ExitCodes.Usage, error);

        var store = storeFactory(DbConfig.Load(configPath, options.GetValueOrDefault("--database")));
        var result = await store.CreateAsync(new NewAccount(username, displayName, role, AccountPolicy.HashPassword(password)), audit, cancellationToken);
        await stdout.WriteLineAsync($"OK create user={result.UserId} role={role} active=yes audit={result.AuditId}");
        return ExitCodes.Ok;
    }

    private async Task<int> PrintGrants(Dictionary<string, string> options)
    {
        if (!options.TryGetValue("--writer-host", out var host)) return await Fail(ExitCodes.Usage, "--writer-host is required.");
        var user = options.GetValueOrDefault("--writer-user", AdminWriterGrants.DefaultUser);
        string[] grants;
        try { grants = AdminWriterGrants.Create(user, host); }
        catch (ArgumentException exception) { return await Fail(ExitCodes.Usage, exception.Message); }
        await stdout.WriteLineAsync($"-- Create the account first with a generated secret: CREATE USER '{user}'@'{host}' IDENTIFIED BY '<secret>' REQUIRE SSL;");
        foreach (var grant in grants) await stdout.WriteLineAsync(grant);
        return ExitCodes.Ok;
    }

    private string? ReadPassword(HashSet<string> flags, string? username, out string error)
    {
        string? password;
        if (flags.Contains("--password-stdin"))
        {
            password = stdin.ReadLine();
            if (password is null) { error = "No password on standard input."; return null; }
        }
        else
        {
            password = passwords.ReadHidden("New password: ");
            if (password is null) { error = "No interactive console: use --password-stdin."; return null; }
            if (passwords.ReadHidden("Repeat new password: ") != password) { error = "The passwords do not match. Nothing changed."; return null; }
        }
        error = AccountPolicy.CheckPassword(password, username) ?? "";
        return error.Length == 0 ? password : null;
    }

    private static bool TryParse(string[] args, out Dictionary<string, string> options, out HashSet<string> flags, out string error)
    {
        options = new(StringComparer.Ordinal);
        flags = new(StringComparer.Ordinal);
        error = "";
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (name.StartsWith("--password", StringComparison.OrdinalIgnoreCase) && name != "--password-stdin")
            {
                // Refused before anything else; the value is not echoed back.
                error = "Passwords are never accepted as arguments. Use the hidden prompt or --password-stdin.";
                return false;
            }
            if (Flags.Contains(name)) { flags.Add(name); continue; }
            if (!Options.Contains(name)) { error = "Unknown option. Run 'bc-admin help'."; return false; }
            if (i + 1 >= args.Length || args[i + 1].StartsWith("--")) { error = $"{name} needs a value."; return false; }
            if (!options.TryAdd(name, args[++i])) { error = $"{name} given twice."; return false; }
        }
        return true;
    }

    private static string AdminActionText(AdminAction action) => MySqlAdminAccountStore.ActionName(action).Replace('_', '-');

    private static string DatabaseMessage(MySqlException exception) => exception.Number switch
    {
        1062 => "That username already exists. Nothing changed.",
        1142 or 1143 or 1227 => $"The database account lacks a required permission (MySQL {exception.Number}). Check the SEC-003 grants. Nothing changed.",
        1146 => "Table missing: run database/sec003/admin-audit.sql first. Nothing changed.",
        1045 => "The database refused the credentials in the config file.",
        _ => $"Database error (MySQL {exception.Number}). Nothing changed."
    };

    private async Task<int> Fail(int code, string message)
    {
        await stderr.WriteLineAsync(message);
        return code;
    }
}

internal sealed class ConsolePasswordSource : IPasswordSource
{
    public string? ReadHidden(string prompt)
    {
        if (Console.IsInputRedirected) return null;
        Console.Error.Write(prompt);
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return buffer.ToString();
    }
}
