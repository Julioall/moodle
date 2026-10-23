using System.Text.Json;
using Moodle.Application.Auditing;

namespace Moodle.Application.Tests.Auditing;

public sealed class AuditPayloadSanitizerTests
{
    [Fact]
    public void SerializeSanitized_RedigeCamposSensiveisEUrlsComToken()
    {
        var json = AuditPayloadSanitizer.SerializeSanitized(new
        {
            user = "teacher",
            password = "senha-real",
            moodleToken = "token-real",
            links = new[]
            {
                "https://moodle.tests/pluginfile.php/1/mod_resource/content/1/file.pdf?forcedownload=1&wstoken=abc&sesskey=xyz"
            },
            nested = new
            {
                apiKey = "api-key-real",
                title = "Aula 1"
            }
        });

        Assert.DoesNotContain("senha-real", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token-real", json, StringComparison.Ordinal);
        Assert.DoesNotContain("api-key-real", json, StringComparison.Ordinal);
        Assert.DoesNotContain("wstoken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sesskey", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", json, StringComparison.Ordinal);
        Assert.Contains("forcedownload=1", json, StringComparison.Ordinal);
        Assert.Contains("Aula 1", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ToSanitizedElement_RetornaJsonElementSemSegredo()
    {
        var element = AuditPayloadSanitizer.ToSanitizedElement(new
        {
            accessToken = "access-token-real",
            url = "https://moodle.tests/course/view.php?id=10&token=abc"
        });

        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.Equal("[REDACTED]", element.GetProperty("accessToken").GetString());
        Assert.Equal(
            "https://moodle.tests/course/view.php?id=10",
            element.GetProperty("url").GetString());
    }

    [Fact]
    public void SerializeSanitized_RedigeSegredosAninhadosEmObjetosEArrays()
    {
        var json = AuditPayloadSanitizer.SerializeSanitized(new
        {
            token = "token-real",
            nested = new { userprivateaccesskey = "private-real" },
            array = new[] { new { password = "password-real" } }
        });

        Assert.DoesNotContain("token-real", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-real", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password-real", json, StringComparison.Ordinal);
        Assert.Equal(3, json.Split("[REDACTED]", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void SerializeSanitized_RedigePayloadDesconhecidoComCasingDiferente()
    {
        var json = AuditPayloadSanitizer.SerializeSanitized(new
        {
            TOKEN = "SECRET_A",
            nested = new { USER_PRIVATE_ACCESS_KEY = "SECRET_B" },
            items = new[] { new { PASSWORD = "SECRET_C" } }
        });

        Assert.DoesNotContain("SECRET_A", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_B", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_C", json, StringComparison.Ordinal);
        Assert.Equal(3, json.Split("[REDACTED]", StringSplitOptions.None).Length - 1);
    }
}
