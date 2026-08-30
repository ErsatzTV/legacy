namespace ErsatzTV.Core.Domain;

public class PlexMediaSource : MediaSource
{
    public string ServerName { get; set; }
    public string ProductVersion { get; set; }
    public string Platform { get; set; }
    public string PlatformVersion { get; set; }
    public string ClientIdentifier { get; set; }

    // public bool IsOwned { get; set; }
    public List<PlexConnection> Connections { get; set; }
    public List<PlexPathReplacement> PathReplacements { get; set; }
    public DateTime? LastCollectionsScan { get; set; }

    public DateTime? MissingSince { get; set; }
}
