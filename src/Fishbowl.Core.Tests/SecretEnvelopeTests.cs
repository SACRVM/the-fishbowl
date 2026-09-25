using System.Text;
using System.Text.Json;
using Fishbowl.Core.Util;
using Xunit;

namespace Fishbowl.Core.Tests;

// Secret vault v2 (docs/superpowers/specs/2026-09-25-secret-vault-design.md):
// the server can't decrypt content_secret, but it can and must refuse
// anything that isn't the v2 envelope shape the client-side vault writes.
// Every refusal is a ResourceValidationKind.Invalid (400), never SizeLimit.
public class SecretEnvelopeTests
{
    // AES-GCM: 12-byte IV + 16-byte tag + >=1 byte ciphertext = 29 bytes min.
    private const int MinBlockBytes = 29;

    private static string ValidBlockB64(int byteLength = MinBlockBytes)
        => Convert.ToBase64String(new byte[byteLength]);

    private static byte[] Envelope(object body)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));

    [Fact]
    public void Validate_Null_ReturnsNull()
    {
        Assert.Null(SecretEnvelope.Validate(null));
    }

    [Fact]
    public void Validate_ValidV2Envelope_ReturnsNull()
    {
        var blob = Envelope(new { v = 2, blocks = new[] { ValidBlockB64() } });
        Assert.Null(SecretEnvelope.Validate(blob));
    }

    [Fact]
    public void Validate_ValidV2Envelope_MultipleBlocks_ReturnsNull()
    {
        var blob = Envelope(new { v = 2, blocks = new[] { ValidBlockB64(), ValidBlockB64(50) } });
        Assert.Null(SecretEnvelope.Validate(blob));
    }

    [Fact]
    public void Validate_ExactlyMaxBlocks_ReturnsNull()
    {
        var blocks = Enumerable.Range(0, SecretEnvelope.MaxBlocks).Select(_ => ValidBlockB64()).ToArray();
        var blob = Envelope(new { v = 2, blocks });
        Assert.Null(SecretEnvelope.Validate(blob));
    }

    [Fact]
    public void Validate_V1Envelope_Refused()
    {
        var blob = Envelope(new { v = 1, blocks = new[] { ValidBlockB64() } });
        var err = SecretEnvelope.Validate(blob);
        Assert.NotNull(err);
        Assert.Equal(ResourceValidationKind.Invalid, err!.Kind);
        Assert.Equal("contentSecret", err.Field);
        Assert.Equal("note", err.Resource);
    }

    [Fact]
    public void Validate_MissingVersion_Refused()
    {
        var blob = Envelope(new { blocks = new[] { ValidBlockB64() } });
        var err = SecretEnvelope.Validate(blob);
        Assert.NotNull(err);
        Assert.Equal(ResourceValidationKind.Invalid, err!.Kind);
    }

    [Fact]
    public void Validate_NotJson_Refused()
    {
        var blob = Encoding.UTF8.GetBytes("not json at all {{{");
        var err = SecretEnvelope.Validate(blob);
        Assert.NotNull(err);
        Assert.Equal(ResourceValidationKind.Invalid, err!.Kind);
    }

    [Fact]
    public void Validate_JsonArray_NotObject_Refused()
    {
        var blob = Encoding.UTF8.GetBytes("[1, 2, 3]");
        var err = SecretEnvelope.Validate(blob);
        Assert.NotNull(err);
        Assert.Equal(ResourceValidationKind.Invalid, err!.Kind);
    }

    [Fact]
    public void Validate_JsonString_NotObject_Refused()
    {
        var blob = Encoding.UTF8.GetBytes("\"just a string\"");
        var err = SecretEnvelope.Validate(blob);
        Assert.NotNull(err);
        Assert.Equal(ResourceValidationKind.Invalid, err!.Kind);
    }

    [Fact]
    public void Validate_MissingBlocksArray_Refused()
    {
        var blob = Envelope(new { v = 2 });
        var err = SecretEnvelope.Validate(blob);
        Assert.NotNull(err);
        Assert.Equal(ResourceValidationKind.Invalid, err!.Kind);
    }

    [Fact]
    public void Validate_BlocksNotAnArray_Refused()
    {
        var blob = Envelope(new { v = 2, blocks = "not-an-array" });
        var err = SecretEnvelope.Validate(blob);
        Assert.NotNull(err);
        Assert.Equal(ResourceValidationKind.Invalid, err!.Kind);
    }

    [Fact]
    public void Validate_EmptyBlocksArray_Refused()
    {
        var blob = Envelope(new { v = 2, blocks = Array.Empty<string>() });
        var err = SecretEnvelope.Validate(blob);
        Assert.NotNull(err);
        Assert.Equal(ResourceValidationKind.Invalid, err!.Kind);
    }

    [Fact]
    public void Validate_TooManyBlocks_Refused()
    {
        var blocks = Enumerable.Range(0, SecretEnvelope.MaxBlocks + 1).Select(_ => ValidBlockB64()).ToArray();
        var blob = Envelope(new { v = 2, blocks });
        var err = SecretEnvelope.Validate(blob);
        Assert.NotNull(err);
        Assert.Equal(ResourceValidationKind.Invalid, err!.Kind);
    }

    [Fact]
    public void Validate_NonStringBlock_Refused()
    {
        var blob = Envelope(new { v = 2, blocks = new object[] { 123 } });
        var err = SecretEnvelope.Validate(blob);
        Assert.NotNull(err);
        Assert.Equal(ResourceValidationKind.Invalid, err!.Kind);
    }

    [Fact]
    public void Validate_NonBase64Block_Refused()
    {
        var blob = Envelope(new { v = 2, blocks = new[] { "not-valid-base64!!!" } });
        var err = SecretEnvelope.Validate(blob);
        Assert.NotNull(err);
        Assert.Equal(ResourceValidationKind.Invalid, err!.Kind);
    }

    [Fact]
    public void Validate_TooShortBlock_Refused()
    {
        // 28 bytes — one short of the 29-byte iv+tag+1 floor.
        var blob = Envelope(new { v = 2, blocks = new[] { ValidBlockB64(MinBlockBytes - 1) } });
        var err = SecretEnvelope.Validate(blob);
        Assert.NotNull(err);
        Assert.Equal(ResourceValidationKind.Invalid, err!.Kind);
    }

    [Fact]
    public void Validate_OneGoodOneTooShortBlock_Refused()
    {
        // The whole envelope is refused if any single block fails — not a
        // partial accept.
        var blob = Envelope(new { v = 2, blocks = new[] { ValidBlockB64(), ValidBlockB64(10) } });
        var err = SecretEnvelope.Validate(blob);
        Assert.NotNull(err);
        Assert.Equal(ResourceValidationKind.Invalid, err!.Kind);
    }
}
