using Saga.Core.Domain;
using Saga.Core.Models;

namespace Saga.Tests;

/// <summary>
/// Which preset a new chat opens on. It is pure list logic, so no database is involved; what it
/// guards is that the default is never a preset the picker greys out — a chat that starts on a
/// greyed-out button looks broken and, with nothing selected, refuses the first question.
/// </summary>
public class MaterialPresetTests
{
    private static Document Document(string typeName) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"{typeName}.pdf",
        DocumentType = new DocumentType { Name = typeName },
    };

    private static Artifact Summary() => new()
    {
        Type = ArtifactType.Summary,
        Status = ArtifactStatus.Generated,
    };

    /// <summary>Client material wins even when the proposal has everything else too: a question
    /// is nearly always about what the client sent, and the wider presets are a click away.</summary>
    [Fact]
    public void Client_material_is_the_default_whenever_the_client_sent_something()
        => Assert.Equal(WorkingContextKind.ClientMaterial, MaterialSelection.DefaultPreset(
            [Document(DocumentType.ClientMaterialName)], [Summary()]));

    [Fact]
    public void Without_client_material_the_default_falls_through_to_all_materials()
        => Assert.Equal(WorkingContextKind.SourceMaterial, MaterialSelection.DefaultPreset(
            [Document(DocumentType.MannazMaterialName)], []));

    /// <summary>An artifact that was never generated is not material, so it cannot make the
    /// widest preset the default any more than it is offered by the picker.</summary>
    [Fact]
    public void An_empty_artifact_does_not_count_as_something_to_read()
        => Assert.Equal(WorkingContextKind.SourceMaterial, MaterialSelection.DefaultPreset(
            [Document(DocumentType.MannazMaterialName)],
            [new Artifact { Type = ArtifactType.Summary, Status = ArtifactStatus.Empty }]));

    /// <summary>Nothing the client sent and nothing to read but artifacts: the picker greys out
    /// both narrower presets, so the default has to be the one that is left.</summary>
    [Fact]
    public void With_only_artifacts_the_default_falls_through_to_the_widest_preset()
        => Assert.Equal(WorkingContextKind.FullProject, MaterialSelection.DefaultPreset(
            [], [Summary()]));

    /// <summary>Nothing to read at all: no preset is offered, so none is chosen — the picker
    /// shows the link to the Materials tab instead.</summary>
    [Fact]
    public void An_empty_proposal_has_no_preset_to_default_to()
        => Assert.Equal(WorkingContextKind.Custom, MaterialSelection.DefaultPreset([], []));

    [Fact]
    public void The_default_is_never_a_preset_the_picker_disables()
    {
        Document[][] materials =
        [
            [],
            [Document(DocumentType.ClientMaterialName)],
            [Document(DocumentType.MannazMaterialName)],
            [Document(DocumentType.ClientMaterialName), Document(DocumentType.MannazMaterialName)],
        ];
        Artifact[][] built = [[], [Summary()]];

        foreach (var documents in materials)
        foreach (var artifacts in built)
        {
            var preset = MaterialSelection.DefaultPreset(documents, artifacts);
            Assert.True(MaterialSelection.IsPresetAvailable(preset, documents, artifacts),
                $"{preset} is disabled for {documents.Length} documents and {artifacts.Length} artifacts");
            Assert.Equal(documents.Length + artifacts.Length == 0, preset == WorkingContextKind.Custom);
        }
    }
}
