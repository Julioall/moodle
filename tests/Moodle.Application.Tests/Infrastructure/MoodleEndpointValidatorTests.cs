using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moodle.Application.MoodleApi;
using Moodle.Infrastructure;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class MoodleEndpointValidatorTests
{
    [Theory]
    [InlineData("https://localhost")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://10.0.0.5")]
    [InlineData("https://169.254.169.254")]
    [InlineData("https://192.168.1.10")]
    [InlineData("https://192.0.2.10")]
    [InlineData("https://198.51.100.10")]
    [InlineData("https://203.0.113.10")]
    [InlineData("https://[::1]")]
    [InlineData("https://[fc00::1]")]
    [InlineData("https://[64:ff9b::7f00:1]")]
    [InlineData("https://[2001:db8::1]")]
    [InlineData("https://[2002:7f00:1::]")]
    public async Task ValidateAsync_BloqueiaDestinosLocaisOuPrivados(string url)
    {
        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            CreateSut().ValidateAsync(url, CancellationToken.None));

        Assert.Equal(MoodleErrorContract.NetworkError, error.ErrorCode);
        Assert.Equal(MoodleIntegrationStage.UrlValidation, error.Stage);
    }

    [Theory]
    [InlineData("http://ead.fieg.com.br")]
    [InlineData("ftp://ead.fieg.com.br")]
    [InlineData("https://user:password@ead.fieg.com.br")]
    public async Task ValidateAsync_ExigeHttpsSemUserInfo(string url)
    {
        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            CreateSut().ValidateAsync(url, CancellationToken.None));

        Assert.Equal(MoodleErrorContract.NetworkError, error.ErrorCode);
    }

    [Theory]
    [InlineData("https://moodle.local")]
    [InlineData("https://moodle.internal")]
    [InlineData("https://moodle.home.arpa")]
    [InlineData("https://moodle.lan")]
    public async Task ValidateAsync_BloqueiaNomesDeUsoLocalSemConsultarDns(string url)
    {
        var dnsCalls = 0;
        var sut = CreateSut((_, _) =>
        {
            dnsCalls++;
            return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
        });

        await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.ValidateAsync(url, CancellationToken.None));

        Assert.Equal(0, dnsCalls);
    }

    [Fact]
    public async Task ValidateAsync_BloqueiaHostComRespostaDnsMista()
    {
        var sut = CreateSut((_, _) => Task.FromResult(new[]
        {
            IPAddress.Parse("8.8.8.8"),
            IPAddress.Parse("10.0.0.5")
        }));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.ValidateAsync("https://moodle.example.org", CancellationToken.None));

        Assert.Equal(MoodleIntegrationStage.UrlValidation, error.Stage);
    }

    [Fact]
    public async Task ValidateAsync_AceitaHostCorporativoExplicitamenteConfiavelComDnsPrivado()
    {
        var sut = CreateSut(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.5.109") }),
            ["ead.fieg.com.br"]);

        var result = await sut.ValidateAsync(
            "https://ead.fieg.com.br/moodle?token=nao-deve-permanecer",
            CancellationToken.None);

        Assert.Equal("ead.fieg.com.br", result.Host);
        Assert.Empty(result.Query);
    }

    [Fact]
    public async Task ValidateAsync_NaoLiberaOutroHostComDnsPrivado()
    {
        var sut = CreateSut(
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.5.109") }),
            ["ead.fieg.com.br"]);

        await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.ValidateAsync("https://outro-moodle.example.org", CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_ResolveNovamenteERecusaMudancaParaEnderecoPrivado()
    {
        var dnsCalls = 0;
        var sut = CreateSut((_, _) =>
        {
            dnsCalls++;
            return Task.FromResult(new[]
            {
                IPAddress.Parse(dnsCalls == 1 ? "8.8.8.8" : "10.0.0.5")
            });
        });

        _ = await sut.ValidateAsync("https://moodle.example.org", CancellationToken.None);
        await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.ValidateAsync("https://moodle.example.org", CancellationToken.None));

        Assert.Equal(2, dnsCalls);
    }

    [Fact]
    public async Task ValidateAsync_AceitaEnderecoPublicoERemoveQueryEFragmento()
    {
        var result = await CreateSut().ValidateAsync(
            "https://8.8.8.8/moodle/?token=nao-deve-permanecer#fragmento",
            CancellationToken.None);

        Assert.Equal(Uri.UriSchemeHttps, result.Scheme);
        Assert.Equal("8.8.8.8", result.Host);
        Assert.Equal("/moodle/", result.AbsolutePath);
        Assert.Empty(result.Query);
        Assert.Empty(result.Fragment);
        Assert.Empty(result.UserInfo);
    }

    [Fact]
    public async Task ValidateAsync_ClassificaFalhaDeDnsSemExporHostComoErroInesperado()
    {
        var sut = CreateSut((_, _) =>
            Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)));

        var error = await Assert.ThrowsAsync<MoodleApiException>(() =>
            sut.ValidateAsync("https://moodle.example.org", CancellationToken.None));

        Assert.Equal(MoodleErrorContract.NetworkError, error.ErrorCode);
        Assert.Equal(MoodleIntegrationStage.UrlValidation, error.Stage);
    }

    private static MoodleEndpointValidator CreateSut(
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null,
        IEnumerable<string>? trustedPrivateEndpointHosts = null) =>
        resolver is null
            ? new MoodleEndpointValidator(NullLogger<MoodleEndpointValidator>.Instance)
            : new MoodleEndpointValidator(
                NullLogger<MoodleEndpointValidator>.Instance,
                resolver,
                trustedPrivateEndpointHosts);
}
