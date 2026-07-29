namespace Scrye.Core.Mip;

/// <summary>A single decoded MIP frame: the client id it was tagged with, the
/// 3-char tag (FFF, BBE, BAB, ...) and the raw data payload.</summary>
public readonly record struct MipMessage(string Id, string Tag, string Data);
