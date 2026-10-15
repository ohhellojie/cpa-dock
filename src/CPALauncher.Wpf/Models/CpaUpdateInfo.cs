namespace CPALauncher.Models;

public sealed class CpaUpdateInfo
{
    public required string TagName { get; init; }
    public required Version NewVersion { get; init; }
    public required string AssetDownloadUrl { get; init; }
    public required long AssetSize { get; init; }
    public string? ExpectedSha256 { get; init; }
    public required string ReleaseUrl { get; init; }
}
