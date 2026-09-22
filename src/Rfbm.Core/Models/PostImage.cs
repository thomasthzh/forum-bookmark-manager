namespace Rfbm.Core.Models;

public sealed record PostImage(
    string SourceUrl,
    int Position,
    string? LocalPath = null,
    string? AltText = null);
