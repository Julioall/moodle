using System.Xml.Linq;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class PedagogicContentPublishTests
{
    [Fact]
    public void PresentationProject_DeveImpedirEmpacotamentoDeGuiasSemLicencaVerificada()
    {
        var root = FindRepositoryRoot();
        var project = XDocument.Load(Path.Combine(root, "src", "Moodle.Presentation", "Moodle.Presentation.csproj"));
        var guides = project.Descendants("Content")
            .Where(element => NormalizePath((string?)element.Attribute("Include"))
                .Contains("public/pedagogic/", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(guides);
    }

    [Fact]
    public void Repositorio_NaoDeveExporGuiasInstitucionaisSemLicenca()
    {
        var dir = Path.Combine(FindRepositoryRoot(), "public", "pedagogic");
        if (!Directory.Exists(dir)) return;
        Assert.Empty(Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Pipeline_DeveExecutarCiEDispararDeployAposAlteracoesEmPublic()
    {
        var workflowsDirectory = Path.Combine(FindRepositoryRoot(), ".github", "workflows");
        var ciLines = await File.ReadAllLinesAsync(Path.Combine(workflowsDirectory, "ci.yml"));
        var deployLines = await File.ReadAllLinesAsync(Path.Combine(workflowsDirectory, "deploy-vps.yml"));

        Assert.Contains(ciLines, line => line.Trim() is "- public/**" or "- public/pedagogic/**");
        Assert.Contains(deployLines, line => line.Trim() == "workflow_run:");
        Assert.Contains(deployLines, line => line.Trim() == "- CI - Build, Testes e Qualidade");
    }

    private static string NormalizePath(string? path) => (path ?? string.Empty).Replace('\\', '/');

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Moodle.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Raiz do repositorio nao encontrada.");
    }
}
