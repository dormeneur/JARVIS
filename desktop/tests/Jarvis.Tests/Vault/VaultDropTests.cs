using Jarvis.Core.Vault;

namespace Jarvis.Tests.Vault;

public sealed class VaultDropTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vaultdrop").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Touch(string relative, int bytes = 3)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Theory]
    [InlineData("Sam_CV_2026.pdf", true)]
    [InlineData("resume-final.docx", true)]
    [InlineData("Résumé.pdf", true)]
    [InlineData("cvs-receipt.pdf", false)]
    [InlineData("presumed.txt", false)]
    [InlineData("notes.md", false)]
    public void Resumes_AreRecognisedByAWordInTheName(string name, bool resume)
        => Assert.Equal(resume, VaultDrop.LooksLikeResume(name));

    [Fact]
    public void Files_GoToResumeOrDocuments_UnreadableOnesAreSkipped()
    {
        var plan = VaultDrop.For([Touch("My Resume.pdf"), Touch("notes.md"), Touch("photo.png"), "relative.txt"]);

        Assert.Equal(
            [(Path.Combine(_root, "My Resume.pdf"), "Profile/resume/My Resume.pdf"), (Path.Combine(_root, "notes.md"), "Profile/documents/notes.md")],
            plan.Uploads);
        Assert.Equal(["photo.png"], plan.Skipped);
    }

    [Fact]
    public void AFolder_KeepsItsLayout_WithoutHiddenOrSecretFiles()
    {
        Touch("Uni/sem1/grades.csv");
        Touch("Uni/.DS_Store");
        Touch("Uni/Secrets/passwords.txt");
        Touch("Uni/.git/config.txt");

        var plan = VaultDrop.For([Path.Combine(_root, "Uni")]);

        Assert.Equal([(Path.Combine(_root, "Uni", "sem1", "grades.csv"), "Profile/documents/Uni/sem1/grades.csv")], plan.Uploads);
        Assert.Empty(plan.Skipped);
    }

    [Fact]
    public void ASecretsFolder_IsRefusedWhole_AndHugeFilesAreSkipped()
    {
        Touch("Secrets/keys.txt");
        var huge = Path.Combine(_root, "big.pdf");
        using (var stream = File.Create(huge))
        {
            stream.SetLength(VaultDrop.MaxFileBytes + 1);
        }

        var plan = VaultDrop.For([Path.Combine(_root, "Secrets"), huge]);

        Assert.Empty(plan.Uploads);
        Assert.Equal(["Secrets", "big.pdf"], plan.Skipped);
    }
}
