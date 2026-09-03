using ErsatzTV.Application.Artworks;
using ErsatzTV.Application.Emby;
using ErsatzTV.Application.Images;
using ErsatzTV.Application.Jellyfin;
using ErsatzTV.Application.Plex;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Emby;
using ErsatzTV.Core.Images;
using ErsatzTV.Core.Interfaces.Images;
using ErsatzTV.Core.Jellyfin;
using ErsatzTV.Extensions;
using ErsatzTV.Filters;
using Flurl;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Controllers;

[ResponseCache(Duration = 3600)]
[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
public class ArtworkController(
    IMediator mediator,
    IHttpClientFactory httpClientFactory,
    IChannelLogoGenerator channelLogoGenerator)
    : ControllerBase
{
    [HttpHead("/artwork/{id:int}")]
    [HttpGet("/artwork/{id:int}")]
    [ServiceFilter(typeof(ConditionalUiAuthorizeFilter))]
    // This route redirect to the proper artwork from its Id
    public async Task<IActionResult> RedirectArtwork(int id, CancellationToken cancellationToken)
    {
        Either<BaseError, Artwork> artwork =
            await mediator.Send(new GetArtwork(id), cancellationToken);

        return artwork.Match<IActionResult>(
            Left: _ => new NotFoundResult(),
            Right: r =>
            {
                // only redirect local artwork
                if (!r.Path.IsHex())
                {
                    return NotFound();
                }

                return r.ArtworkKind switch
                {
                    ArtworkKind.Poster => new RedirectResult("/artwork/posters/" + r.Path),
                    ArtworkKind.Thumbnail => new RedirectResult("/artwork/thumbnails/" + r.Path),
                    ArtworkKind.Logo => new RedirectResult("/iptv/logos/" + r.Path),
                    ArtworkKind.FanArt => new RedirectResult("/artwork/fanart/" + r.Path),
                    ArtworkKind.Watermark => new RedirectResult("/artwork/watermarks/" + r.Path),
                    _ => new NotFoundResult()
                };
            });
    }

    [HttpHead("/iptv/artwork/posters/{fileName:hex}")]
    [HttpGet("/iptv/artwork/posters/{fileName:hex}")]
    [HttpHead("/iptv/artwork/posters/{fileName:hex}.jpg")]
    [HttpGet("/iptv/artwork/posters/{fileName:hex}.jpg")]
    public async Task<IActionResult> IptvGetPoster(string fileName, CancellationToken cancellationToken)
    {
        Either<BaseError, CachedImagePathViewModel> cachedImagePath =
            await mediator.Send(
                new GetCachedImagePath(fileName, ArtworkKind.Poster, string.Empty, 440),
                cancellationToken);
        return cachedImagePath.Match<IActionResult>(
            Left: _ => new NotFoundResult(),
            Right: r => new PhysicalFileResult(r.FileName, r.MimeType));
    }

    [HttpGet("/artwork/posters/{fileName:hex}")]
    [ServiceFilter(typeof(ConditionalUiAuthorizeFilter))]
    public async Task<IActionResult> GetPoster(string fileName, CancellationToken cancellationToken)
    {
        Either<BaseError, CachedImagePathViewModel> cachedImagePath =
            await mediator.Send(
                new GetCachedImagePath(fileName, ArtworkKind.Poster, string.Empty, 440),
                cancellationToken);
        return cachedImagePath.Match<IActionResult>(
            Left: _ => new NotFoundResult(),
            Right: r => new PhysicalFileResult(r.FileName, r.MimeType));
    }

    [HttpGet("/artwork/watermarks/{fileName:hex}")]
    [ServiceFilter(typeof(ConditionalUiAuthorizeFilter))]
    public async Task<IActionResult> GetWatermark(
        string fileName,
        [FromQuery]
        string contentType,
        CancellationToken cancellationToken)
    {
        Either<BaseError, CachedImagePathViewModel> cachedImagePath =
            await mediator.Send(
                new GetCachedImagePath(fileName, ArtworkKind.Watermark, contentType),
                cancellationToken);
        return cachedImagePath.Match<IActionResult>(
            Left: _ => new NotFoundResult(),
            Right: r => new PhysicalFileResult(r.FileName, r.MimeType));
    }

    [HttpGet("/artwork/fanart/{fileName:hex}")]
    [ServiceFilter(typeof(ConditionalUiAuthorizeFilter))]
    public async Task<IActionResult> GetFanArt(string fileName, CancellationToken cancellationToken)
    {
        Either<BaseError, CachedImagePathViewModel> cachedImagePath =
            await mediator.Send(new GetCachedImagePath(fileName, ArtworkKind.FanArt, string.Empty), cancellationToken);
        return cachedImagePath.Match<IActionResult>(
            Left: _ => new NotFoundResult(),
            Right: r => new PhysicalFileResult(r.FileName, r.MimeType));
    }


    [HttpHead("/iptv/artwork/posters/plex/{id:int}")]
    [HttpGet("/iptv/artwork/posters/plex/{id:int}")]
    [HttpHead("/iptv/artwork/thumbnails/plex/{id:int}")]
    [HttpGet("/iptv/artwork/thumbnails/plex/{id:int}")]
    public Task<IActionResult> IptvGetPlex(int id, CancellationToken cancellationToken) =>
        GetPlexArtwork(id, cancellationToken);

    [HttpGet("/artwork/posters/plex/{id:int}")]
    [HttpGet("/artwork/thumbnails/plex/{id:int}")]
    [HttpGet("/artwork/fanart/plex/{id:int}")]
    [ServiceFilter(typeof(ConditionalUiAuthorizeFilter))]
    public Task<IActionResult> GetPlex(int id, CancellationToken cancellationToken) =>
        GetPlexArtwork(id, cancellationToken);

    [HttpHead("/iptv/artwork/posters/jellyfin/{id:int}")]
    [HttpGet("/iptv/artwork/posters/jellyfin/{id:int}")]
    [HttpHead("/iptv/artwork/thumbnails/jellyfin/{id:int}")]
    [HttpGet("/iptv/artwork/thumbnails/jellyfin/{id:int}")]
    public Task<IActionResult> IptvGetJellyfin(int id, CancellationToken cancellationToken) =>
        GetJellyfinArtwork(id, cancellationToken);

    [HttpGet("/artwork/posters/jellyfin/{id:int}")]
    [HttpGet("/artwork/thumbnails/jellyfin/{id:int}")]
    [HttpGet("/artwork/fanart/jellyfin/{id:int}")]
    [ServiceFilter(typeof(ConditionalUiAuthorizeFilter))]
    public Task<IActionResult> GetJellyfin(int id, CancellationToken cancellationToken) =>
        GetJellyfinArtwork(id, cancellationToken);

    [HttpHead("/iptv/artwork/posters/emby/{id:int}")]
    [HttpGet("/iptv/artwork/posters/emby/{id:int}")]
    [HttpHead("/iptv/artwork/thumbnails/emby/{id:int}")]
    [HttpGet("/iptv/artwork/thumbnails/emby/{id:int}")]
    public Task<IActionResult> IptvetEmby(int id, CancellationToken cancellationToken) =>
        GetEmbyArtwork(id, cancellationToken);

    [HttpGet("/artwork/posters/emby/{id:int}")]
    [HttpGet("/artwork/thumbnails/emby/{id:int}")]
    [HttpGet("/artwork/fanart/emby/{id:int}")]
    [ServiceFilter(typeof(ConditionalUiAuthorizeFilter))]
    public Task<IActionResult> GetEmby(int id, CancellationToken cancellationToken) =>
        GetEmbyArtwork(id, cancellationToken);

    [HttpHead("/iptv/artwork/thumbnails/{fileName:hex}")]
    [HttpGet("/iptv/artwork/thumbnails/{fileName:hex}")]
    [HttpHead("/iptv/artwork/thumbnails/{fileName:hex}.jpg")]
    [HttpGet("/iptv/artwork/thumbnails/{fileName:hex}.jpg")]
    public async Task<IActionResult> IptvGetThumbnail(string fileName, CancellationToken cancellationToken)
    {
        Either<BaseError, CachedImagePathViewModel> cachedImagePath =
            await mediator.Send(
                new GetCachedImagePath(fileName, ArtworkKind.Thumbnail, string.Empty, 220),
                cancellationToken);
        return cachedImagePath.Match<IActionResult>(
            Left: _ => new NotFoundResult(),
            Right: r => new PhysicalFileResult(r.FileName, r.MimeType));
    }

    [HttpGet("/artwork/thumbnails/{fileName:hex}")]
    [ServiceFilter(typeof(ConditionalUiAuthorizeFilter))]
    public async Task<IActionResult> GetThumbnail(string fileName, CancellationToken cancellationToken)
    {
        Either<BaseError, CachedImagePathViewModel> cachedImagePath =
            await mediator.Send(
                new GetCachedImagePath(fileName, ArtworkKind.Thumbnail, string.Empty, 220),
                cancellationToken);
        return cachedImagePath.Match<IActionResult>(
            Left: _ => new NotFoundResult(),
            Right: r => new PhysicalFileResult(r.FileName, r.MimeType));
    }

    private async Task<IActionResult> GetPlexArtwork(int id, CancellationToken cancellationToken)
    {
#if DEBUG_NO_SYNC
        await Task.CompletedTask;
        return NotFound();
#else
        Either<BaseError, Artwork> artwork =
            await mediator.Send(new GetArtwork(id), cancellationToken);

        return await artwork.Match(
            Left: _ => new NotFoundResult().AsTask<IActionResult>(),
            Right: async art =>
            {
                // plex/{id}/library/metadata/x/thumb/y
                string[] split = (art.Path ?? string.Empty).Split('/');
                if (split.Length < 7 || split[0] != "plex" || !int.TryParse(split[1], out int plexMediaSourceId))
                {
                    return NotFound();
                }

                var path = string.Join('/', split[2..]);

                string transcodePath = art.ArtworkKind switch
                {
                    ArtworkKind.Poster => $"photo/:/transcode?url=/{path}&height=440&width=304&minSize=1&upscale=0",
                    ArtworkKind.Thumbnail => $"photo/:/transcode?url=/{path}&height=220&width=392&minSize=1&upscale=0",
                    ArtworkKind.FanArt => $"/{path}",
                    _ => null
                };

                if (string.IsNullOrWhiteSpace(transcodePath))
                {
                    return NotFound();
                }

                Either<BaseError, PlexConnectionParametersViewModel> connectionParameters =
                    await mediator.Send(new GetPlexConnectionParameters(plexMediaSourceId), cancellationToken);

                return await connectionParameters.Match(
                    Left: _ => new NotFoundResult().AsTask<IActionResult>(),
                    Right: async r =>
                    {
                        try
                        {
                            HttpClient client = httpClientFactory.CreateClient();
                            HttpContext.Response.RegisterForDispose(client);
                            client.DefaultRequestHeaders.Add("X-Plex-Token", r.AuthToken);

                            var fullPath = new Uri(new Uri(r.Address), transcodePath);
                            HttpResponseMessage response = await client.GetAsync(
                                fullPath,
                                HttpCompletionOption.ResponseHeadersRead,
                                cancellationToken);
                            HttpContext.Response.RegisterForDispose(response);

                            Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

                            return new FileStreamResult(
                                stream,
                                response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
                        }
                        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
                        {
                            return NotFound();
                        }
                    });
            });
#endif
    }

    private async Task<IActionResult> GetJellyfinArtwork(int id, CancellationToken cancellationToken)
    {
#if DEBUG_NO_SYNC
        await Task.CompletedTask;
        return NotFound();
#else
        Either<BaseError, JellyfinConnectionParametersViewModel> connectionParameters =
            await mediator.Send(new GetJellyfinConnectionParameters(), cancellationToken);

        return await connectionParameters.Match(
            Left: _ => new NotFoundResult().AsTask<IActionResult>(),
            Right: async vm =>
            {
                Either<BaseError, Artwork> artwork =
                    await mediator.Send(new GetArtwork(id), cancellationToken);

                return await artwork.Match(
                    Left: _ => new NotFoundResult().AsTask<IActionResult>(),
                    Right: async art =>
                    {
                        try
                        {
                            if (!(art.Path ?? string.Empty).StartsWith("jellyfin://", StringComparison.OrdinalIgnoreCase))
                            {
                                return NotFound();
                            }

                            HttpClient client = httpClientFactory.CreateClient();
                            HttpContext.Response.RegisterForDispose(client);

                            Url fullPath = JellyfinUrl.ForArtwork(vm.Address, art.Path);
                            string fillHeight = art.ArtworkKind switch
                            {
                                ArtworkKind.Poster => "440",
                                ArtworkKind.Thumbnail => "220",
                                _ => string.Empty
                            };

                            if (!string.IsNullOrWhiteSpace(fillHeight))
                            {
                                fullPath.SetQueryParam("fillHeight", fillHeight);
                            }

                            HttpResponseMessage response = await client.GetAsync(
                                fullPath,
                                HttpCompletionOption.ResponseHeadersRead,
                                cancellationToken);
                            HttpContext.Response.RegisterForDispose(response);

                            Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

                            return new FileStreamResult(
                                stream,
                                response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
                        }
                        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
                        {
                            return NotFound();
                        }
                    });
            });
#endif
    }

    private async Task<IActionResult> GetEmbyArtwork(int id, CancellationToken cancellationToken)
    {
#if DEBUG_NO_SYNC
        await Task.CompletedTask;
        return NotFound();
#else
        Either<BaseError, EmbyConnectionParametersViewModel> connectionParameters =
            await mediator.Send(new GetEmbyConnectionParameters(), cancellationToken);

        return await connectionParameters.Match(
            Left: _ => new NotFoundResult().AsTask<IActionResult>(),
            Right: async vm =>
            {
                Either<BaseError, Artwork> artwork =
                    await mediator.Send(new GetArtwork(id), cancellationToken);

                return await artwork.Match(
                    Left: _ => new NotFoundResult().AsTask<IActionResult>(),
                    Right: async art =>
                    {
                        try
                        {
                            if (!(art.Path ?? string.Empty).StartsWith("emby://", StringComparison.OrdinalIgnoreCase))
                            {
                                return NotFound();
                            }

                            HttpClient client = httpClientFactory.CreateClient();
                            HttpContext.Response.RegisterForDispose(client);

                            Url fullPath = EmbyUrl.ForArtwork(vm.Address, art.Path);
                            string maxHeight = art.ArtworkKind switch
                            {
                                ArtworkKind.Poster => "440",
                                ArtworkKind.Thumbnail => "220",
                                _ => string.Empty
                            };

                            if (!string.IsNullOrWhiteSpace(maxHeight))
                            {
                                fullPath.SetQueryParam("maxHeight", maxHeight);
                            }

                            HttpResponseMessage response = await client.GetAsync(
                                fullPath,
                                HttpCompletionOption.ResponseHeadersRead,
                                cancellationToken);
                            HttpContext.Response.RegisterForDispose(response);

                            Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);

                            return new FileStreamResult(
                                stream,
                                response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
                        }
                        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
                        {
                            return NotFound();
                        }
                    });
            });
#endif
    }

    [HttpGet(ChannelLogoGenerator.GetRoute)]
    public IActionResult GenerateChannelLogo(
        string text, // param name = ChannelLogoGenerator.GetRouteQueryParamName
        CancellationToken cancellationToken) =>
        channelLogoGenerator
            .GenerateChannelLogo(text, 100, 200, cancellationToken).Match<IActionResult>(
                Left: _ => new RedirectResult("/iptv/images/ersatztv-500.png"),
                Right: img => File(img, "image/png")
            );
}
