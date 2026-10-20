using BC_LocalAI.Models.Authentication;
using BC_LocalAI.Services.Authentication;

namespace BC_LocalAI.Tests.Authentication;

public sealed class AuthenticationRequestValidatorTests
{
    private static LoginRequest Request(string? username = "synthetic.user", string? password = "synthetic-password") =>
        new() { Username = username, Password = password };

    [Theory]
    [InlineData(null, "synthetic")]
    [InlineData("", "synthetic")]
    [InlineData("   ", "synthetic")]
    [InlineData("synthetic", null)]
    [InlineData("synthetic", "")]
    [InlineData("user\nname", "synthetic")]
    [InlineData("user\0name", "synthetic")]
    public void InvalidFieldsHaveTheSameSafeError(string? username, string? password)
    {
        var error = AuthenticationRequestValidator.Validate(Request(username, password));
        Assert.Equal("AUTH_INVALID_REQUEST", error!.Code);
        Assert.Equal("ข้อมูลเข้าสู่ระบบไม่ถูกต้อง", error.Message);
    }

    [Fact]
    public void NullRequestHasTheSameValidationError() =>
        Assert.Equal("AUTH_INVALID_REQUEST", AuthenticationRequestValidator.Validate(null)!.Code);

    [Theory]
    [InlineData(" รหัสผ่านสมมติ😀\n ")]
    [InlineData("   ")]
    [InlineData("e\u0301")]
    [InlineData("é")]
    public void ValidPasswordIsPreservedExactlyRatherThanTrimmedOrNormalized(string password)
    {
        var input = Request(password: password);
        Assert.Null(AuthenticationRequestValidator.Validate(input));
        Assert.Equal(password, input.Password);
    }

    [Fact]
    public void UsernameIsNotSilentlyNormalizedWithoutTheReviewedAccountContract()
    {
        var input = Request(" Synthetic.User ");
        Assert.Null(AuthenticationRequestValidator.Validate(input));
        Assert.Equal(" Synthetic.User ", input.Username);
    }

    [Fact]
    public void CharacterCeilingsAcceptBoundaryAndRejectLargerValues()
    {
        var limits = new AuthenticationInputLimits(4, 5, 20);
        Assert.Null(AuthenticationRequestValidator.Validate(Request("user", "12345"), limits));
        Assert.NotNull(AuthenticationRequestValidator.Validate(Request("users", "12345"), limits));
        Assert.NotNull(AuthenticationRequestValidator.Validate(Request("user", "123456"), limits));
    }

    [Fact]
    public void Utf8ByteCeilingIsNotConfusedWithCharacterCount()
    {
        var limits = new AuthenticationInputLimits(128, 100, 6);
        Assert.Null(AuthenticationRequestValidator.Validate(Request(password: "กข"), limits));
        Assert.NotNull(AuthenticationRequestValidator.Validate(Request(password: "กขค"), limits));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IllFormedUnicodeIsRejectedRatherThanReplaced(bool invalidUsername)
    {
        // Attribute string metadata replaces lone surrogates; construct the actual malformed input at runtime.
        var invalid = new string((char)0xd800, 1);
        Assert.NotNull(AuthenticationRequestValidator.Validate(invalidUsername
            ? Request(username: invalid) : Request(password: invalid)));
    }

    [Theory]
    [InlineData(0, 5, 20)]
    [InlineData(5, -1, 20)]
    [InlineData(5, 5, 0)]
    public void InvalidConfigurationFailsRatherThanDisablingLimits(int username, int password, int bytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => AuthenticationRequestValidator.Validate(Request(), new(username, password, bytes)));

    [Fact]
    public void ResourceGuardIsNotPresentedAsSqlInjectionProtection()
    {
        // A legitimate password/username can contain SQL-like text. The future DAL must parameterize, not concatenate.
        var input = Request("synthetic' OR 1=1 --", "synthetic' OR 1=1 --");
        Assert.Null(AuthenticationRequestValidator.Validate(input));
    }
}
