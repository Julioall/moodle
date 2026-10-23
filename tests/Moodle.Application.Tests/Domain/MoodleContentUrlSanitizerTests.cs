using Moodle.Domain;

namespace Moodle.Application.Tests.Domain;

public class MoodleContentUrlSanitizerTests
{
    [Fact]
    public void Deve_remover_parametros_sensiveis_de_urls_de_conteudo()
    {
        var sanitized = MoodleContentUrlSanitizer.Sanitize(
            "https://moodle.example/pluginfile.php/1/mod_resource/content/0/aula.pdf?forcedownload=1&token=secret&wstoken=service-token&sesskey=session");

        Assert.Equal(
            "https://moodle.example/pluginfile.php/1/mod_resource/content/0/aula.pdf?forcedownload=1",
            sanitized);
    }

    [Fact]
    public void Deve_preservar_url_relativa_sem_tentar_parsear()
    {
        var sanitized = MoodleContentUrlSanitizer.Sanitize("/mod/page/view.php?id=10&token=abc");

        Assert.Equal("/mod/page/view.php?id=10&token=abc", sanitized);
    }
}
