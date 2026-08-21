using System.Text;
using System.Text.Json;
using Fettler.Cli;
using Fettler.Core;
using Fettler.Mcp;
using Xunit;

namespace Fettler.Tests;

/// <summary>
/// The credential net: a write that INTRODUCES a secret is refused, one
/// that merely edits a file already holding one is not, and the refusal
/// never repeats what it found.
///
/// <para>Every value below is a published example or an obvious fixture.
/// They are written out rather than assembled at runtime because the
/// shape is the thing under test, and a test that hides its input is a
/// test nobody can check.</para>
/// </summary>
public sealed class CredentialTests
{
    // AWS's own documented example key, and fixtures shaped like the
    // real thing for everything else.
    const string AwsKey = "AKIAIOSFODNN7EXAMPLE";
    const string GithubToken = "ghp_1234567890abcdefghijklmnopqrstuvwxyzAB";
    // Deliberately not the digit-group shape Slack issues: GitHub's push
    // protection rejected the realistic fixture, which is a fair catch
    // and a working demonstration of the feature under test.
    const string SlackToken = "xoxb-not-a-real-token-fixture";
    const string StripeKey = "sk_live_abcdef1234567890ABCDEF";
    // AIza plus exactly 35, which is the length Google issues.
    const string GoogleKey = "AIzaSyA1234567890abcdefghijklmnopqrstuv";
    const string PemHeader = "-----BEGIN RSA PRIVATE KEY-----";

    // Long, no whitespace, mixed classes, high entropy: what tier two is
    // for, and what a pasted real secret looks like.
    const string Entropic = "T7xQ2mVz9KpL4wRc8FbN3yHd";

    // ---- tier one: shapes their issuer fixed ----

    [Theory]
    [InlineData(AwsKey, "aws-access-key-id")]
    [InlineData(GithubToken, "github-token")]
    [InlineData(SlackToken, "slack-token")]
    [InlineData(StripeKey, "stripe-live-key")]
    [InlineData(GoogleKey, "google-api-key")]
    [InlineData(PemHeader, "private-key-header")]
    public void EachStructuralDetectorFires(string secret, string detector)
    {
        IReadOnlyList<SecretFinding> found = Secrets.Scan($"value = {secret}\n");

        Assert.Contains(found, f => f.Detector == detector);
    }

    [Fact]
    public void TheLineNumberIsTheOneTheSecretIsOn()
    {
        IReadOnlyList<SecretFinding> found = Secrets.Scan($"one\ntwo\nkey = {AwsKey}\n");

        Assert.Equal(3, Assert.Single(found).Line);
    }

    // ---- tier two: a secret-shaped name given a secret-shaped value ----

    [Fact]
    public void AnAssignedHighEntropyValueIsCaught()
    {
        IReadOnlyList<SecretFinding> found = Secrets.Scan($"password = \"{Entropic}\"\n");

        Assert.Equal("assigned-password", Assert.Single(found).Detector);
    }

    /// <summary>
    /// The shapes a configuration is SUPPOSED to use. Every one of these
    /// firing would teach people to switch the check off, which is the
    /// failure mode that matters more than a miss.
    /// </summary>
    [Theory]
    [InlineData("password = \"\"")]
    [InlineData("password: null")]
    [InlineData("token: ${GITHUB_TOKEN}")]
    [InlineData("api_key = $(vault read secret)")]
    [InlineData("secret: {{ .Values.secret }}")]
    [InlineData("password = %APP_PASSWORD%")]
    [InlineData("api_key = \"changeme\"")]
    [InlineData("token = \"your-token-here\"")]
    [InlineData("password = \"xxxxxxxxxxxxxxxxxxxxxxxx\"")]
    [InlineData("secret = \"the location of the spare key is a secret\"")]
    [InlineData("token = \"short\"")]
    public void ReferencesAndPlaceholdersPass(string line)
    {
        Assert.Empty(Secrets.Scan(line + "\n"));
    }

    // ---- the diff, not the file ----

    [Fact]
    public void ASecretAlreadyThereDoesNotBlockAnEdit()
    {
        string existing = $"key = {AwsKey}\nname = mill\n";
        string updated = $"key = {AwsKey}\nname = mill\nregion = eu-west-2\n";

        Assert.Empty(Secrets.Introduced(existing, updated));
    }

    [Fact]
    public void MovingAnExistingSecretIsNotIntroducingOne()
    {
        string existing = $"a = 1\nkey = {AwsKey}\n";
        string updated = $"key = {AwsKey}\na = 1\n";

        Assert.Empty(Secrets.Introduced(existing, updated));
    }

    /// <summary>And it is reported once, not once per detector that
    /// recognises it - an assigned GitHub token matches both tiers.</summary>
    [Fact]
    public void ASecondDifferentSecretIsStillIntroduced()
    {
        string existing = $"key = {AwsKey}\n";
        string updated = $"key = {AwsKey}\ntoken = {GithubToken}\n";

        Assert.Equal("github-token", Assert.Single(Secrets.Introduced(existing, updated)).Detector);
    }

    [Fact]
    public void ANewFileHasNoBaselineSoEverythingCounts()
    {
        Assert.Single(Secrets.Introduced(null, $"key = {AwsKey}\n"));
    }

    // ---- the refusal must not repeat what it found ----

    [Fact]
    public void TheMessageNeverEchoesTheSecret()
    {
        string message = Secrets.Describe(Secrets.Scan(
            $"key = {AwsKey}\npassword = \"{Entropic}\"\n"));

        Assert.DoesNotContain(AwsKey, message, StringComparison.Ordinal);
        Assert.DoesNotContain(Entropic, message, StringComparison.Ordinal);
        Assert.Contains("line 1", message, StringComparison.Ordinal);
        Assert.Contains("aws-access-key-id", message, StringComparison.Ordinal);
    }

    // ---- through the write path ----

    [Fact]
    public async Task WritingASecretIsRefusedAndNothingLands()
    {
        using var box = new Sandbox();

        Result<Saved> wrote = await box.Bench.WriteAsync("config.json", $"key = {AwsKey}\n", true);

        Assert.False(wrote.IsOk);
        Assert.Equal(Outcome.Credential, wrote.Failure!.Outcome);
        Assert.DoesNotContain(AwsKey, wrote.Failure!.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(box.Full("config.json")), "nothing may be left behind");
    }

    [Fact]
    public async Task TheOverrideLetsItThrough()
    {
        using var box = new Sandbox();

        Result<Saved> wrote = await box.Bench.WriteAsync(
            "config.json", $"key = {AwsKey}\n", true, allowCredential: true);

        Assert.True(wrote.IsOk, wrote.Failure?.Message);
    }

    [Fact]
    public async Task AnOrdinaryWriteIsUntouched()
    {
        using var box = new Sandbox();

        Result<Saved> wrote = await box.Bench.WriteAsync("notes.txt", "nothing to see\n", true);

        Assert.True(wrote.IsOk, wrote.Failure?.Message);
    }

    // ---- the override is a person's, not a model's ----

    [Fact]
    public void TheMcpCatalogueDoesNotOfferTheOverride()
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) ToolCatalogue.Write(writer);
        string catalogue = Encoding.UTF8.GetString(buffer.ToArray());

        Assert.DoesNotContain("allow-credential", catalogue, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("allowCredential", catalogue, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the outcome carries its own code ----

    [Fact]
    public void TheOutcomeHasItsOwnExitCodeAndName()
    {
        Assert.Equal(12, ExitCodes.Of(Outcome.Credential));
        Assert.Equal("credential", ExitCodes.NameOf(Outcome.Credential));
        Assert.NotEqual(ExitCodes.Of(Outcome.Refused), ExitCodes.Of(Outcome.Credential));
        Assert.NotEqual(ExitCodes.Of(Outcome.Governed), ExitCodes.Of(Outcome.Credential));
    }
}
