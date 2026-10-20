using BC_LocalAI.Configuration;
using Microsoft.Extensions.FileProviders;
using MySqlConnector;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;

namespace BC_LocalAI.Tests.Authentication;

public sealed class AuthenticationOptionsTests
{
    private sealed class EnvironmentStub : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "synthetic";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Theory]
    [InlineData("root")]
    [InlineData("bc_sec001_admin")]
    public void AdministrativeDbCredentialsAreRejected(string username)
    {
        var options = Valid();
        var connection = new MySqlConnectionStringBuilder(options.ReaderConnectionString) { UserID = username };
        options.ReaderConnectionString = connection.ConnectionString;
        Assert.True(new AuthenticationOptionsValidator(new EnvironmentStub()).Validate(null, options).Failed);
    }

    [Theory]
    [InlineData(MySqlSslMode.Disabled)]
    [InlineData(MySqlSslMode.Preferred)]
    [InlineData(MySqlSslMode.Required)]
    [InlineData(MySqlSslMode.VerifyCA)]
    public void ProductionRequiresHostnameAndCertificateValidation(MySqlSslMode mode)
    {
        var options = Valid();
        var connection = new MySqlConnectionStringBuilder(options.ReaderConnectionString) { SslMode = mode };
        options.ReaderConnectionString = connection.ConnectionString;
        var result = new AuthenticationOptionsValidator(new EnvironmentStub()).Validate(null, options);
        Assert.True(result.Failed);
        Assert.DoesNotContain("placeholder", string.Join(";", result.Failures));
    }

    [Fact]
    public void LocalExceptionCannotBeEnabledInProductionOrForRemoteServer()
    {
        var options = Valid(); options.AllowLocalDevelopmentDatabase = true;
        Assert.True(new AuthenticationOptionsValidator(new EnvironmentStub()).Validate(null, options).Failed);
        Assert.True(new AuthenticationOptionsValidator(new EnvironmentStub { EnvironmentName = "Development" }).Validate(null, options).Failed);
        var connection = new MySqlConnectionStringBuilder(options.ReaderConnectionString) { Server = "127.0.0.1", SslMode = MySqlSslMode.Required };
        options.ReaderConnectionString = connection.ConnectionString;
        Assert.True(new AuthenticationOptionsValidator(new EnvironmentStub { EnvironmentName = "Development" }).Validate(null, options).Succeeded);
    }

    [Fact]
    public void InvalidLimitsPoolOrPublicKeyDirectoryFailClosed()
    {
        var options = Valid(); options.ConcurrentLogins = 0;
        Assert.True(new AuthenticationOptionsValidator(new EnvironmentStub()).Validate(null, options).Failed);
        options = Valid(); options.ReaderConnectionString += ";AllowLoadLocalInfile=true";
        Assert.True(new AuthenticationOptionsValidator(new EnvironmentStub()).Validate(null, options).Failed);
        options = Valid(); options.ReaderConnectionString += ";MaximumPoolSize=999";
        Assert.True(new AuthenticationOptionsValidator(new EnvironmentStub()).Validate(null, options).Failed);
        options = Valid(); options.KeyDirectory = Path.Combine(Path.GetTempPath(), "wwwroot");
        Assert.True(new AuthenticationOptionsValidator(new EnvironmentStub()).Validate(null, options).Failed);
    }

    [Fact]
    public void DisabledNeedsNoSecretsButEnabledConfigurationMustBeComplete()
    {
        var validator = new AuthenticationOptionsValidator(new EnvironmentStub());
        Assert.True(validator.Validate(null, new()).Succeeded);
        Assert.True(validator.Validate(null, new() { Enabled = true }).Failed);
        Assert.True(validator.Validate(null, Valid()).Succeeded);
    }

    [Fact]
    public void CustomWebRootCannotContainTheKeyDirectory()
    {
        var environment = new EnvironmentStub
        { ContentRootPath = Path.Combine(Path.GetTempPath(), "synthetic-content"), WebRootPath = Path.GetTempPath() };
        Assert.True(new AuthenticationOptionsValidator(environment).Validate(null, Valid()).Failed);
    }

    private static AuthenticationOptions Valid() => new()
    {
        Enabled = true, KeyDirectory = Path.GetTempPath(),
        ReaderConnectionString = "Server=localhost;User ID=bc_auth_reader;Password=placeholder;Database=synthetic;SslMode=VerifyFull;Connection Timeout=3;MaximumPoolSize=4" // ci-allow-secret: fake placeholder configuration for validator tests only.
    };
}
