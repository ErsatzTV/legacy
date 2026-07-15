using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using ErsatzTV.Infrastructure.Plex.Models;
using Microsoft.IO;

namespace ErsatzTV.Infrastructure.Plex;

[SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms")]
public class PlexEtag
{
    private readonly RecyclableMemoryStreamManager _recyclableMemoryStreamManager;

    public PlexEtag(RecyclableMemoryStreamManager recyclableMemoryStreamManager) =>
        _recyclableMemoryStreamManager = recyclableMemoryStreamManager;

    public string ForMovie(PlexMetadataResponse response)
    {
        using MemoryStream ms = _recyclableMemoryStreamManager.GetStream();
        using var bw = new BinaryWriter(ms);

        // video key
        bw.Write(response.Key);

        // video added at
        bw.Write(response.AddedAt);

        // video updated at
        bw.Write(response.UpdatedAt);

        // video title
        bw.Write((byte)FieldKey.Title);
        bw.Write(response.Title ?? string.Empty);

        foreach (PlexMediaResponse<PlexPartResponse> media in response.Media)
        {
            // media id
            bw.Write((byte)FieldKey.MediaId);
            bw.Write(media.Id);

            // media part id
            foreach (PlexPartResponse part in media.Part)
            {
                bw.Write((byte)FieldKey.Key);
                bw.Write(part.Key);

                bw.Write((byte)FieldKey.PartId);
                bw.Write(part.Id);

                bw.Write((byte)FieldKey.File);
                bw.Write(part.File);

                bw.Write((byte)FieldKey.Duration);
                bw.Write(part.Duration);

                bw.Write((byte)FieldKey.Size);
                bw.Write(part.Size);

                bw.Write((byte)FieldKey.Container);
                bw.Write(part.Container ?? string.Empty);

                bw.Write((byte)FieldKey.VideoProfile);
                bw.Write(part.VideoProfile ?? string.Empty);

                bw.Write((byte)FieldKey.AudioProfile);
                bw.Write(part.AudioProfile ?? string.Empty);
            }
        }

        // genre ids
        foreach (PlexGenreResponse genre in Optional(response.Genre).Flatten())
        {
            bw.Write((byte)FieldKey.GenreTag);
            bw.Write(genre.Tag);
        }

        // label ids
        foreach (PlexLabelResponse label in Optional(response.Label).Flatten())
        {
            bw.Write((byte)FieldKey.LabelTag);
            bw.Write(label.Tag);
        }

        // director ids
        foreach (PlexDirectorResponse director in Optional(response.Director).Flatten())
        {
            bw.Write((byte)FieldKey.DirectorTag);
            bw.Write(director.Tag);
        }

        // writer ids
        foreach (PlexWriterResponse writer in Optional(response.Writer).Flatten())
        {
            bw.Write((byte)FieldKey.WriterTag);
            bw.Write(writer.Tag);
        }

        // collection ids
        foreach (PlexCollectionResponse collection in Optional(response.Collection).Flatten())
        {
            bw.Write((byte)FieldKey.CollectionTag);
            bw.Write(collection.Tag);
        }

        // role ids
        foreach (PlexRoleResponse role in Optional(response.Role).Flatten())
        {
            bw.Write((byte)FieldKey.RoleTag);
            bw.Write(role.Tag);
        }

        ms.Position = 0;
        byte[] hash = SHA1.Create().ComputeHash(ms);
        return Convert.ToHexString(hash);
    }

    public string ForShow(PlexMetadataResponse response)
    {
        using MemoryStream ms = _recyclableMemoryStreamManager.GetStream();
        using var bw = new BinaryWriter(ms);

        // video key
        bw.Write(response.Key);

        // video added at
        bw.Write(response.AddedAt);

        // video updated at
        bw.Write(response.UpdatedAt);

        // genre ids
        foreach (PlexGenreResponse genre in Optional(response.Genre).Flatten())
        {
            bw.Write((byte)FieldKey.GenreTag);
            bw.Write(genre.Tag);
        }

        // collection ids
        foreach (PlexCollectionResponse collection in Optional(response.Collection).Flatten())
        {
            bw.Write((byte)FieldKey.CollectionTag);
            bw.Write(collection.Tag);
        }

        // role ids
        foreach (PlexRoleResponse role in Optional(response.Role).Flatten())
        {
            bw.Write((byte)FieldKey.RoleTag);
            bw.Write(role.Tag);
        }

        // label ids
        foreach (PlexLabelResponse label in Optional(response.Label).Flatten())
        {
            bw.Write((byte)FieldKey.LabelTag);
            bw.Write(label.Tag);
        }

        ms.Position = 0;
        byte[] hash = SHA1.Create().ComputeHash(ms);
        return Convert.ToHexString(hash);
    }

    public string ForSeason(PlexXmlMetadataResponse response)
    {
        using MemoryStream ms = _recyclableMemoryStreamManager.GetStream();
        using var bw = new BinaryWriter(ms);

        // video key
        bw.Write(response.Key);

        // video added at
        bw.Write(response.AddedAt);

        // video updated at
        bw.Write(response.UpdatedAt);

        // collection ids
        foreach (PlexCollectionResponse collection in Optional(response.Collection).Flatten())
        {
            bw.Write((byte)FieldKey.CollectionTag);
            bw.Write(collection.Tag);
        }

        // thumb
        if (!string.IsNullOrWhiteSpace(response.Thumb))
        {
            bw.Write(response.Thumb);
        }

        // art
        if (!string.IsNullOrWhiteSpace(response.Art))
        {
            bw.Write(response.Art);
        }

        ms.Position = 0;
        byte[] hash = SHA1.Create().ComputeHash(ms);
        return Convert.ToHexString(hash);
    }

    public string ForEpisode(PlexXmlMetadataResponse response)
    {
        using MemoryStream ms = _recyclableMemoryStreamManager.GetStream();
        using var bw = new BinaryWriter(ms);

        // video key
        bw.Write(response.Key);

        // video added at
        bw.Write(response.AddedAt);

        // video updated at
        bw.Write(response.UpdatedAt);

        foreach (PlexMediaResponse<PlexXmlPartResponse> media in response.Media)
        {
            // media id
            bw.Write((byte)FieldKey.MediaId);
            bw.Write(media.Id);

            // media part id
            foreach (PlexXmlPartResponse part in media.Part)
            {
                bw.Write((byte)FieldKey.Key);
                bw.Write(part.Key);

                bw.Write((byte)FieldKey.PartId);
                bw.Write(part.Id);

                bw.Write((byte)FieldKey.File);
                bw.Write(part.File);

                bw.Write((byte)FieldKey.Duration);
                bw.Write(part.Duration);

                bw.Write((byte)FieldKey.Size);
                bw.Write(part.Size);

                bw.Write((byte)FieldKey.Container);
                bw.Write(part.Container ?? string.Empty);

                bw.Write((byte)FieldKey.VideoProfile);
                bw.Write(part.VideoProfile ?? string.Empty);

                bw.Write((byte)FieldKey.AudioProfile);
                bw.Write(part.AudioProfile ?? string.Empty);

                // stream id
                foreach (PlexStreamResponse stream in part.Stream)
                {
                    bw.Write((byte)FieldKey.StreamId);
                    bw.Write(stream.Id);
                }
            }
        }

        // genre ids
        foreach (PlexGenreResponse genre in Optional(response.Genre).Flatten())
        {
            bw.Write((byte)FieldKey.GenreTag);
            bw.Write(genre.Tag);
        }

        // director ids
        foreach (PlexDirectorResponse director in Optional(response.Director).Flatten())
        {
            bw.Write((byte)FieldKey.DirectorTag);
            bw.Write(director.Tag);
        }

        // writer ids
        foreach (PlexWriterResponse writer in Optional(response.Writer).Flatten())
        {
            bw.Write((byte)FieldKey.WriterTag);
            bw.Write(writer.Tag);
        }

        // collection ids
        foreach (PlexCollectionResponse collection in Optional(response.Collection).Flatten())
        {
            bw.Write((byte)FieldKey.CollectionTag);
            bw.Write(collection.Tag);
        }

        // role ids
        foreach (PlexRoleResponse role in Optional(response.Role).Flatten())
        {
            bw.Write((byte)FieldKey.RoleTag);
            bw.Write(role.Tag);
        }

        ms.Position = 0;
        byte[] hash = SHA1.Create().ComputeHash(ms);
        return Convert.ToHexString(hash);
    }

    public string ForCollection(PlexCollectionMetadataResponse response)
    {
        using MemoryStream ms = _recyclableMemoryStreamManager.GetStream();
        using var bw = new BinaryWriter(ms);

        // collection key
        bw.Write(response.Key);

        // collection added at
        bw.Write(response.AddedAt);

        // collection updated at
        bw.Write(response.UpdatedAt);

        // collection child count
        bw.Write(response.ChildCount);

        // collection is smart collection
        bw.Write(response.Smart ?? "0");

        ms.Position = 0;
        byte[] hash = SHA1.Create().ComputeHash(ms);
        return Convert.ToHexString(hash);
    }

    private enum FieldKey : byte
    {
        MediaId = 0,
        PartId = 1,
        StreamId = 2,

        GenreTag = 10,
        DirectorTag = 11,
        WriterTag = 12,
        CollectionTag = 13,
        RoleTag = 14,
        LabelTag = 15,

        Thumb = 20,
        Art = 21,
        Title = 22,

        File = 30,
        Key = 31,

        ChildCount = 40,
        Smart = 41, // smart collection bool

        Duration = 50,
        Size = 51,
        Container = 52,
        VideoProfile = 53,
        AudioProfile = 54
    }
}
