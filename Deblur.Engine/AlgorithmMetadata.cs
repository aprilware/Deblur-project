namespace Deblur.Engine;

public sealed record AlgorithmMetadata(
    string Id,
    string Version,
    string DisplayName,
    string DescriptionMarkdown,
    string LiteratureCitation);
