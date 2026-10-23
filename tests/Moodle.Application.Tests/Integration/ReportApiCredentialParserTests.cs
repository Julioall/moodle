using System.Text;
using Microsoft.AspNetCore.Http;
using Moodle.Presentation.Security;

namespace Moodle.Application.Tests.Integration;

public sealed class ReportApiCredentialParserTests
{
    [Fact]
    public void Parse_AcceptsBasicAuthenticationForExcelOnline()
    {
        var context = new DefaultHttpContext();
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("excel-report:connector-api-key"));
        context.Request.Headers.Authorization = $"Basic {encoded}";

        var result = ReportApiCredentialParser.Parse(context.Request);

        Assert.Equal("connector-api-key", result.ApiKey);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("outro-usuario:connector-api-key")]
    [InlineData("excel-report:")]
    public void Parse_RejectsInvalidBasicCredentials(string credentials)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization =
            $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials))}";

        var result = ReportApiCredentialParser.Parse(context.Request);

        Assert.Null(result.ApiKey);
        Assert.Equal("invalid_basic_credentials", result.Error);
    }

    [Fact]
    public void Parse_KeepsQueryStringCompatibility()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?api_key=desktop-key");

        var result = ReportApiCredentialParser.Parse(context.Request);

        Assert.Equal("desktop-key", result.ApiKey);
    }
}
