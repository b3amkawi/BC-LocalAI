namespace BcAdmin;

internal static class EntryPoint
{
    // Explicit entry point (no top-level statements) so this assembly's types never clash with the web app's Program in tests.
    private static Task<int> Main(string[] args) =>
        new AdminCli(Console.In, Console.Out, Console.Error, new ConsolePasswordSource(),
            connectionString => new MySqlAdminAccountStore(connectionString), OsUser()).RunAsync(args);

    internal static string OsUser() => $"{Environment.UserDomainName}\\{Environment.UserName}";
}
