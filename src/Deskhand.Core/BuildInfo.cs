namespace Deskhand.Core;

/// <summary>The single source of truth for the Deskhand version. Bump this on release; it feeds /health,
/// the OpenAPI doc, and the self-update check (compared against the latest GitHub release tag).</summary>
public static class BuildInfo
{
    public const string Version = "0.2.3";

    /// <summary>owner/repo the self-update check queries on GitHub for the latest release.</summary>
    public const string Repository = "guscatalano/Deskhand";
}
