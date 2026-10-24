using System.Xml.Linq;

namespace Moodle.Application.Tests.Infrastructure;

public sealed class PedagogicContentPublishTests
{
    private static readonly string[] ExpectedGuides =
    [
        "1º GUIA DA PRÁTICA PEDAGÓGICA DA MSEP.md",
        "2º GUIA DA PRÁTICA PEDAGÓGICA DA MSEP.md",
        "GUIA DE ELABORACAO DE ITENS.md",
        "Guia de Desenvolvimento de Situação de Aprendizagem.md",
        "Guia do Tutor - Com ISBN 1 (6).md",
        "METODOLOGIA SENAI DE EDUCACAO PROFISSIONAL.md",
        "Princípios Norteadores MSEP.md"
    ];

    [Fact]
    public void PresentationProject_DevePublicarGuiasPedagogicosNoDiretorioPublic()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectPath = Path.Combine(repositoryRoot, "src", "Moodle.Presentation", "Moodle.Presentation.csproj");
        var project = XDocument.Load(projectPath);

        var content = project.Descendants("Content")
            .SingleOrDefault(element => NormalizePath((string?)element.Attribute("Include")) == "../../public/pedagogic/**/*.md");

        Assert.NotNull(content);
        Assert.Equal("public/pedagogic/%(RecursiveDir)%(Filename)%(Extension)", NormalizePath((string?)content.Element("Link")));
        Assert.Equal("PreserveNewest", (string?)content.Element("CopyToOutputDirectory"));
        Assert.Equal("PreserveNewest", (string?)content.Element("CopyToPublishDirectory"));
    }

    [Fact]
    public void Repositorio_DeveConterOsSeteGuiasPedagogicosEsperados()
    {
        var guidesDirectory = Path.Combine(FindRepositoryRoot(), "public", "pedagogic");
        var actualGuides = Directory.GetFiles(guidesDirectory, "*.md", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedGuides.Order(StringComparer.Ordinal), actualGuides);
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
