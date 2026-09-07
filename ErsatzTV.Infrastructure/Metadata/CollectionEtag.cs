using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Metadata;
using Microsoft.IO;

namespace ErsatzTV.Infrastructure.Metadata;

[SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms")]
public class CollectionEtag(RecyclableMemoryStreamManager recyclableMemoryStreamManager)
    : ICollectionEtag
{
    public string ForCollectionItems(List<MediaItem> items)
    {
        using MemoryStream ms = recyclableMemoryStreamManager.GetStream();
        using var bw = new BinaryWriter(ms);

        foreach (MediaItem item in items.OrderBy(i => i.Id))
        {
            bw.Write(item.Id);
        }

        ms.Position = 0;
        byte[] hash = SHA1.HashData(ms);
        return Convert.ToHexString(hash);
    }
}
