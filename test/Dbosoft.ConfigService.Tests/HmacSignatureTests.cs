using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Dbosoft.ConfigService.Authentication;
using Xunit;

namespace Dbosoft.ConfigService.Tests;

public class HmacSignatureTests
{
    [Fact]
    public void ParseAuthorizationParameters_extracts_all_three_components()
    {
        var input = "Credential=admin&SignedHeaders=x-ms-date;host;x-ms-content-sha256&Signature=abc=";
        var result = HmacSignature.ParseAuthorizationParameters(input);

        result.Should().NotBeNull();
        result!.CredentialId.Should().Be("admin");
        result.SignedHeaders.Should().Be("x-ms-date;host;x-ms-content-sha256");
        result.Signature.Should().Be("abc=");
    }

    [Fact]
    public void ParseAuthorizationParameters_returns_null_when_credential_missing()
    {
        var input = "SignedHeaders=x-ms-date;host;x-ms-content-sha256&Signature=abc";
        HmacSignature.ParseAuthorizationParameters(input).Should().BeNull();
    }

    [Fact]
    public void ParseAuthorizationParameters_returns_null_when_signature_missing()
    {
        var input = "Credential=admin&SignedHeaders=x-ms-date;host;x-ms-content-sha256";
        HmacSignature.ParseAuthorizationParameters(input).Should().BeNull();
    }

    [Fact]
    public void BuildStringToSign_uses_uppercase_method_and_LF_separators()
    {
        var s = HmacSignature.BuildStringToSign(
            "get",
            "/kv?key=*&api-version=1.0",
            new[] { "Fri, 11 May 2018 18:48:36 GMT", "config.local", "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=" });

        s.Should().Be(
            "GET\n/kv?key=*&api-version=1.0\nFri, 11 May 2018 18:48:36 GMT;config.local;47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=");
    }

    [Fact]
    public void ComputeContentHash_of_empty_body_matches_well_known_constant()
    {
        // SHA-256 of the empty string, base64-encoded.
        HmacSignature.ComputeContentHash(ReadOnlySpan<byte>.Empty)
            .Should().Be("47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=");
    }

    [Fact]
    public void ComputeSignature_round_trips_against_independent_HMAC_implementation()
    {
        var secret = Convert.ToBase64String(Encoding.UTF8.GetBytes("test-secret-key-32-bytes-long!!"));
        var stringToSign = "GET\n/kv\nFri, 11 May 2018 18:48:36 GMT;config.local;47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=";

        var actual = HmacSignature.ComputeSignature(stringToSign, secret);

        // Independent computation
        using var hmac = new HMACSHA256(Convert.FromBase64String(secret));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));

        actual.Should().Be(expected);
    }

    [Fact]
    public void SignaturesEqual_is_true_for_identical_signatures()
        => HmacSignature.SignaturesEqual("abc==", "abc==").Should().BeTrue();

    [Fact]
    public void SignaturesEqual_is_false_for_different_signatures()
        => HmacSignature.SignaturesEqual("abc==", "abd==").Should().BeFalse();

    [Fact]
    public void SignaturesEqual_is_false_for_different_lengths()
        => HmacSignature.SignaturesEqual("abc", "abcdef").Should().BeFalse();
}
