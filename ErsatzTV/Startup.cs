using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Abstractions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using BlazorSortable;
using Dapper;
using ErsatzTV.Application;
using ErsatzTV.Application.Channels;
using ErsatzTV.Application.Streaming;
using ErsatzTV.Core;
using ErsatzTV.Core.Emby;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Health;
using ErsatzTV.Core.Health.Checks;
using ErsatzTV.Core.Images;
using ErsatzTV.Core.Interfaces.Database;
using ErsatzTV.Core.Interfaces.Emby;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Images;
using ErsatzTV.Core.Interfaces.Jellyfin;
using ErsatzTV.Core.Interfaces.Locking;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Plex;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Interfaces.Scripting;
using ErsatzTV.Core.Interfaces.Search;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Interfaces.Trakt;
using ErsatzTV.Core.Interfaces.Troubleshooting;
using ErsatzTV.Core.Jellyfin;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Core.Plex;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Core.Scheduling.BlockScheduling;
using ErsatzTV.Core.Scheduling.Engine;
using ErsatzTV.Core.Scheduling.ScriptedScheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling;
using ErsatzTV.Core.Search;
using ErsatzTV.Core.Trakt;
using ErsatzTV.Core.Troubleshooting;
using ErsatzTV.FFmpeg.Capabilities;
using ErsatzTV.FFmpeg.Pipeline;
using ErsatzTV.FFmpeg.Runtime;
using ErsatzTV.Filters;
using ErsatzTV.Formatters;
using ErsatzTV.Infrastructure;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Data.Repositories;
using ErsatzTV.Infrastructure.Database;
using ErsatzTV.Infrastructure.Emby;
using ErsatzTV.Infrastructure.FFmpeg;
using ErsatzTV.Infrastructure.Health;
using ErsatzTV.Infrastructure.Health.Checks;
using ErsatzTV.Infrastructure.Images;
using ErsatzTV.Infrastructure.Jellyfin;
using ErsatzTV.Infrastructure.Locking;
using ErsatzTV.Infrastructure.Metadata;
using ErsatzTV.Infrastructure.Plex;
using ErsatzTV.Infrastructure.Runtime;
using ErsatzTV.Infrastructure.Scheduling;
using ErsatzTV.Infrastructure.Scripting;
using ErsatzTV.Infrastructure.Search;
using ErsatzTV.Infrastructure.Sqlite.Data;
using ErsatzTV.Infrastructure.Streaming;
using ErsatzTV.Infrastructure.Streaming.Graphics;
using ErsatzTV.Infrastructure.Trakt;
using ErsatzTV.Serialization;
using ErsatzTV.Services;
using ErsatzTV.Services.RunOnce;
using ErsatzTV.Services.Validators;
using FluentValidation;
using FluentValidation.AspNetCore;
using Ganss.Xss;
using MediatR.Courier.DependencyInjection;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IO;
using Microsoft.OpenApi;
using MudBlazor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Refit;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;
using Testably.Abstractions;

namespace ErsatzTV;

public class Startup
{
    public Startup(IConfiguration configuration, IWebHostEnvironment env)
    {
        Configuration = configuration;
        CurrentEnvironment = env;
    }

    public IConfiguration Configuration { get; }

    private IWebHostEnvironment CurrentEnvironment { get; }

    [SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments")]
    public void ConfigureServices(IServiceCollection services)
    {
        BugsnagConfiguration bugsnagConfig = Configuration.GetSection("Bugsnag").Get<BugsnagConfiguration>();
        services.Configure<BugsnagConfiguration>(Configuration.GetSection("Bugsnag"));
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.All;
            options.ForwardLimit = 2;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(FileSystemLayout.DataProtectionFolder));

        services.AddOpenApi("v1", options => { options.ShouldInclude += a => a.GroupName == "general"; });

        services.AddOpenApi(
            "scripted-schedule-tagged",
            options => { options.ShouldInclude += a => a.GroupName == "scripted-schedule"; });

        services.AddOpenApi(
            "scripted-schedule",
            options =>
            {
                options.ShouldInclude += a => a.GroupName == "scripted-schedule";
                var tag = new OpenApiTag { Name = "ScriptedSchedule" };
                var tagReference = new OpenApiTagReference("ScriptedSchedule");
                options.AddOperationTransformer((operation, _, _) =>
                {
                    operation.Tags.Clear();
                    operation.Tags.Add(tagReference);
                    return Task.CompletedTask;
                });
                options.AddDocumentTransformer((document, _, _) =>
                {
                    document.Tags.Clear();
                    document.Tags.Add(tag);
                    return Task.CompletedTask;
                });
            });

        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);

        OidcHelper.Init(Configuration);
        JwtHelper.Init(Configuration);
        SearchHelper.Init(Configuration);

        if (OidcHelper.IsEnabled)
        {
            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = "cookie";
                    options.DefaultChallengeScheme = "oidc";
                })
                .AddCookie(
                    "cookie",
                    options =>
                    {
                        options.CookieManager = new ChunkingCookieManager();

                        options.Cookie.HttpOnly = true;
                        options.Cookie.SameSite = SameSiteMode.None;
                        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    })
                .AddOpenIdConnect(
                    "oidc",
                    options =>
                    {
                        options.Authority = OidcHelper.Authority;
                        options.ClientId = OidcHelper.ClientId;
                        options.ClientSecret = OidcHelper.ClientSecret;

                        options.ResponseType = OpenIdConnectResponseType.Code;
                        options.UsePkce = true;
                        options.ResponseMode = OpenIdConnectResponseMode.Query;

                        options.Scope.Clear();
                        options.Scope.Add("openid");

                        options.CallbackPath = new PathString("/callback");

                        options.SaveTokens = true;

                        options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
                        options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;

                        if (!string.IsNullOrWhiteSpace(OidcHelper.LogoutUri))
                        {
                            options.Events = new OpenIdConnectEvents
                            {
                                OnRedirectToIdentityProviderForSignOut = context =>
                                {
                                    context.Response.Redirect(OidcHelper.LogoutUri);
                                    context.HandleResponse();

                                    return Task.CompletedTask;
                                }
                            };
                        }
                    });
        }

        if (JwtHelper.IsEnabled)
        {
            services.AddAuthentication().AddJwtBearer(
                "jwt",
                options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = JwtHelper.IssuerSigningKey,
                        ValidateLifetime = true
                    };
                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = static context =>
                        {
                            StringValues token = context.Request.Query["access_token"];
                            if (!string.IsNullOrWhiteSpace(token))
                            {
                                context.Token = token;
                            }

                            return Task.CompletedTask;
                        }
                    };
                });
        }

        if (OidcHelper.IsEnabled || JwtHelper.IsEnabled)
        {
            services.AddAuthorization(options =>
                {
                    if (OidcHelper.IsEnabled)
                    {
                        var defaultAuthorizationPolicyBuilder = new AuthorizationPolicyBuilder(
                            "cookie",
                            "oidc");

                        defaultAuthorizationPolicyBuilder =
                            defaultAuthorizationPolicyBuilder.RequireAuthenticatedUser();

                        options.DefaultPolicy = defaultAuthorizationPolicyBuilder.Build();
                    }

                    if (JwtHelper.IsEnabled)
                    {
                        var onlyJwtSchemePolicyBuilder = new AuthorizationPolicyBuilder("jwt");
                        options.AddPolicy(
                            "JwtOnlyScheme",
                            onlyJwtSchemePolicyBuilder
                                .RequireAuthenticatedUser()
                                .Build());
                    }
                }
            );
        }

        services.AddCors(o => o.AddPolicy(
            "AllowAll",
            builder =>
            {
                builder.AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader();
            }));

        services.AddLocalization();

        services.AddControllers(options =>
            {
                options.OutputFormatters.Insert(0, new ConcatPlaylistOutputFormatter());
                options.OutputFormatters.Insert(0, new ChannelPlaylistOutputFormatter());
                options.OutputFormatters.Insert(0, new ChannelGuideOutputFormatter());
                options.OutputFormatters.Insert(0, new DeviceXmlOutputFormatter());
                options.OutputFormatters.Insert(0, new HdhrJsonOutputFormatter());
            })
            .AddNewtonsoftJson(opt =>
            {
                opt.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
                opt.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
                opt.SerializerSettings.ContractResolver = new CustomContractResolver();
                opt.SerializerSettings.Converters.Add(new StringEnumConverter());
            });

        services.AddScoped(_ => new ConditionalIptvAuthorizeFilter("JwtOnlyScheme"));

        services.AddFluentValidationAutoValidation();
        services.AddValidatorsFromAssemblyContaining<Startup>();

        services.AddMemoryCache();

        services.AddRazorPages(options =>
        {
            if (OidcHelper.IsEnabled)
            {
                options.Conventions.AuthorizeFolder("/");
            }
        });

        services.AddServerSideBlazor()
            .AddHubOptions(hubOptions => hubOptions.MaximumReceiveMessageSize = 1024 * 1024);

        services.AddMudServices();

        services.AddSortable();

        var coreAssembly = Assembly.GetAssembly(typeof(LibraryScanProgress));
        if (coreAssembly != null)
        {
            services.AddCourier(coreAssembly);
        }

        Console.OutputEncoding = Encoding.UTF8;

        string etvVersion = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

        Log.Logger.Information("ErsatzTV Legacy version {Version}", etvVersion);

        CopyMacOsConfigFolderIfNeeded();

        List<string> directoriesToCreate =
        [
            FileSystemLayout.AppDataFolder,
            FileSystemLayout.TranscodeFolder,
            FileSystemLayout.TempFilePoolFolder,
            FileSystemLayout.FontsCacheFolder,
            FileSystemLayout.TemplatesFolder,
            FileSystemLayout.MusicVideoCreditsTemplatesFolder,
            FileSystemLayout.ChannelStreamSelectorsFolder,
            FileSystemLayout.ChannelGuideTemplatesFolder,
            FileSystemLayout.GraphicsElementsTemplatesFolder,
            FileSystemLayout.GraphicsElementsTextTemplatesFolder,
            FileSystemLayout.GraphicsElementsImageTemplatesFolder,
            FileSystemLayout.GraphicsElementsScriptTemplatesFolder,
            FileSystemLayout.GraphicsElementsSubtitleTemplatesFolder,
            FileSystemLayout.GraphicsElementsMotionTemplatesFolder,
            FileSystemLayout.ScriptsFolder,
            FileSystemLayout.MultiEpisodeShuffleTemplatesFolder,
            FileSystemLayout.AudioStreamSelectorScriptsFolder,
            FileSystemLayout.MpegTsScriptsFolder,
            FileSystemLayout.DefaultMpegTsScriptFolder,
            FileSystemLayout.NextChannelConfigOverlaysFolder,
            FileSystemLayout.NextPlayoutsFolder,
        ];

        foreach (string directory in directoriesToCreate)
        {
            if (directory is not null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        // until we add a setting for a file-specific scheme://host:port to access
        // stream urls contained in this file, it doesn't make sense to do
        // for now, continue to use scheme and host from incoming requests
        // string xmltvPath = Path.Combine(appDataFolder, "xmltv.xml");
        // Log.Logger.Information("XMLTV is at {XmltvPath}", xmltvPath);

        string databaseProvider = Configuration.GetValue("provider", Provider.Sqlite.Name);
        var sqliteConnectionString = $"Data Source={FileSystemLayout.DatabasePath};foreign keys=true;";
        string mySqlConnectionString = Configuration.GetValue<string>("MySql:ConnectionString");

        services.AddDbContext<TvContext>(
            options =>
            {
                if (databaseProvider == Provider.Sqlite.Name)
                {
                    TvContext.IsSqlite = true;

                    options.UseSqlite(
                        sqliteConnectionString,
                        o =>
                        {
                            o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                            o.MigrationsAssembly("ErsatzTV.Infrastructure.Sqlite");
                        });
                }

                if (databaseProvider == Provider.MySql.Name)
                {
                    TvContext.IsSqlite = false;

                    options.UseMySql(
                        mySqlConnectionString,
                        ServerVersion.AutoDetect(mySqlConnectionString),
                        o =>
                        {
                            o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                            o.MigrationsAssembly("ErsatzTV.Infrastructure.MySql");
                        }
                    );
                }
            },
            ServiceLifetime.Scoped,
            ServiceLifetime.Singleton);

        services.AddDbContextFactory<TvContext>(options =>
        {
            if (databaseProvider == Provider.Sqlite.Name)
            {
                options.UseSqlite(
                    sqliteConnectionString,
                    o =>
                    {
                        o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                        o.MigrationsAssembly("ErsatzTV.Infrastructure.Sqlite");
                    });
            }

            if (databaseProvider == Provider.MySql.Name)
            {
                options.UseMySql(
                    mySqlConnectionString,
                    ServerVersion.AutoDetect(mySqlConnectionString),
                    o =>
                    {
                        o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                        o.MigrationsAssembly("ErsatzTV.Infrastructure.MySql");
                    }
                );
            }
        });

        if (databaseProvider == Provider.Sqlite.Name)
        {
            Log.Logger.Information("Database is at {DatabasePath}", FileSystemLayout.DatabasePath);

            TvContext.LastInsertedRowId = "last_insert_rowid()";
            TvContext.CaseInsensitiveCollation = "NOCASE";

            SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
            SqlMapper.AddTypeHandler(new GuidHandler());
            SqlMapper.AddTypeHandler(new TimeSpanHandler());
        }

        if (databaseProvider == Provider.MySql.Name)
        {
            TvContext.LastInsertedRowId = "last_insert_id()";
            TvContext.CaseInsensitiveCollation = "utf8mb4_general_ci";
        }

        Log.Logger.Information("Transcode folder is {Folder}", FileSystemLayout.TranscodeFolder);

        services.AddMediatR(config => config.RegisterServicesFromAssemblyContaining<GetAllChannels>());

        services.AddRefitClient<IPlexTvApi>()
            .ConfigureHttpClient(c => c.BaseAddress = new Uri("https://plex.tv/api/v2"));

        services.AddRefitClient<ITraktApi>(
                new RefitSettings
                {
                    ContentSerializer = new NewtonsoftJsonContentSerializer(
                        new JsonSerializerSettings
                        {
                            ContractResolver = new SnakeCasePropertyNamesContractResolver()
                        })
                })
            .ConfigureHttpClient(c =>
            {
                c.BaseAddress = new Uri("https://api.trakt.tv");
                c.DefaultRequestHeaders.Add("User-Agent", $"ErsatzTV/{etvVersion}");
            });

        services.AddHttpClient("RefitCustomClient").AddHttpMessageHandler<SlowApiHandler>();

        services.Configure<TraktConfiguration>(Configuration.GetSection("Trakt"));

        services.AddResponseCompression(options => { options.EnableForHttps = true; });

        CustomServices(services);
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        string baseUrl = SystemEnvironment.BaseUrl;
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            try
            {
                app.UsePathBase(baseUrl);

                // for testing - make path base required
                // app.Use(async (context, next) =>
                // {
                //     if (context.Request.PathBase != baseUrl)
                //     {
                //         context.Response.StatusCode = 404;
                //         return;
                //     }
                //
                //     await next(context);
                // });
            }
            catch (Exception ex)
            {
                Log.Error(
                    ex,
                    "Failed to configure ETV_BASE_URL; please check syntax and include leading slash e.g. `/etv`: {BaseUrl}",
                    baseUrl);
            }
        }

        app.UseCors("AllowAll");
        app.UseForwardedHeaders();

        //app.UseHttpLogging();
        app.UseSerilogRequestLogging(options =>
        {
            options.IncludeQueryInRequestPath = true;

            // Emit debug-level events instead of the defaults
            options.GetLevel = (httpContext, elapsed, ex) =>
            {
                if (ex is not null)
                {
                    return LogEventLevel.Error;
                }

                if (httpContext.Response.StatusCode > 499)
                {
                    return LogEventLevel.Error;
                }

                if (httpContext.Request.Path.ToUriComponent().StartsWith(
                        "/iptv",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return LogEventLevel.Debug;
                }

                if (httpContext.Request.Path.ToUriComponent().StartsWith(
                        "/api",
                        StringComparison.OrdinalIgnoreCase) &&
                    !httpContext.Request.Path.ToUriComponent().StartsWith(
                        "/api/scan",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return LogEventLevel.Debug;
                }

                return LogEventLevel.Verbose;
            };

            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("RemoteIP", httpContext.Connection.RemoteIpAddress);
                diagnosticContext.Set("UserAgent", httpContext.Request.Headers["User-Agent"]);
            };

            options.MessageTemplate =
                "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.00} ms from {UserAgent} at {RemoteIP}";
        });

        app.UseRequestLocalization(options =>
        {
            CultureInfo[] cinfo = CultureInfo.GetCultures(CultureTypes.AllCultures & ~CultureTypes.NeutralCultures);
            string[] supportedCultures = cinfo.Select(t => t.Name).Distinct().ToArray();
            options.AddSupportedCultures(supportedCultures)
                .AddSupportedUICultures(Localization.UiCultures)
                .SetDefaultCulture(Localization.DefaultCulture);
            options.AddInitialRequestCultureProvider(
                new DatabaseRequestCultureProvider(
                    new AcceptLanguageHeaderRequestCultureProvider()));
        });

        app.UseStaticFiles();

        var extensionProvider = new FileExtensionContentTypeProvider();

        // fix static file M3U8 mime type
        extensionProvider.Mappings.Add(".m3u8", "application/vnd.apple.mpegurl");

        // fix static file TS mime type
        extensionProvider.Mappings.Remove(".ts");
        extensionProvider.Mappings.Add(".ts", "video/mp2t");

        app.UseStaticFiles(
            new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(FileSystemLayout.TranscodeFolder),
                RequestPath = "/iptv/session",
                ContentTypeProvider = extensionProvider,
                OnPrepareResponse = ctx =>
                {
                    // Log.Logger.Information("Transcode access: {Test}", ctx.File.PhysicalPath);
                    ChannelWriter<IFFmpegWorkerRequest> writer = app.ApplicationServices
                        .GetRequiredService<ChannelWriter<IFFmpegWorkerRequest>>();
                    writer.TryWrite(new TouchFFmpegSession(ctx.File.PhysicalPath));
                },
                // to serve m4s
                ServeUnknownFileTypes = true
            });

        app.UseResponseCompression();

        app.Use(async (context, next) =>
        {
            if (!context.Request.Host.Value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) &&
                !IsIptvPath(context.Request.Path) &&
                context.Connection.LocalPort != Settings.UiPort)
            {
                context.Response.StatusCode = 404;
                return;
            }

            await next(context);
        });

        app.MapWhen(
            ctx => !IsIptvPath(ctx.Request.Path),
            blazor =>
            {
                blazor.UseRouting();

                if (OidcHelper.IsEnabled)
                {
                    blazor.UseAuthentication();
#pragma warning disable ASP0001
                    blazor.UseAuthorization();
#pragma warning restore ASP0001
                }

                blazor.UseEndpoints(endpoints =>
                {
                    endpoints.MapControllers();
                    endpoints.MapBlazorHub();
                    endpoints.MapFallbackToPage("/_Host");

                    if (CurrentEnvironment.IsDevelopment())
                    {
                        endpoints.MapOpenApi();
                    }

                    endpoints.MapScalarApiReference("/docs", options =>
                    {
                        options.AddDocument(
                            "scripted-schedule",
                            "Scripted Schedule",
                            "openapi/scripted-schedule-tagged.json");
                        options.AddDocument("v1", "General", "openapi/v1.json");
                        options.HideClientButton = true;
                        options.DocumentDownloadType = DocumentDownloadType.None;
                        options.Title = "ErsatzTV API Reference";
                    });
                });
            });

        app.MapWhen(
            ctx => IsIptvPath(ctx.Request.Path),
            iptv =>
            {
                iptv.UseRouting();
                iptv.UseEndpoints(endpoints => endpoints.MapControllers());
            });
        return;

        bool IsIptvPath(PathString path)
        {
            return path.StartsWithSegments("/iptv") ||
                   path.StartsWithSegments("/discover.json") ||
                   path.StartsWithSegments("/device.xml") ||
                   path.StartsWithSegments("/lineup.json") ||
                   path.StartsWithSegments("/lineup_status.json");
        }
    }

    private static void CustomServices(IServiceCollection services)
    {
        services.AddSingleton<IEnvironmentValidator, EnvironmentValidator>();

        services.AddSingleton<IFileSystem, RealFileSystem>();

        services.AddSingleton<IDatabaseMigrations, DatabaseMigrations>();
        services.AddSingleton<IPlexSecretStore, PlexSecretStore>();
        services.AddSingleton<IPlexTvApiClient, PlexTvApiClient>(); // TODO: does this need to be singleton?
        services.AddSingleton<ITraktApiClient, TraktApiClient>();
        services.AddSingleton<IEntityLocker, EntityLocker>();
        services.AddSingleton<ISearchTargets, SearchTargets>();
        services.AddSingleton<ISmartCollectionCache, SmartCollectionCache>();
        services.AddSingleton<SearchQueryParser>();
        services.AddSingleton<ITroubleshootingNotifier, TroubleshootingNotifier>();
        services.AddSingleton<CustomFontMapper>();
        services.AddSingleton<GraphicsEngineFonts>();
        services.AddSingleton(Program.InMemoryLogService);

        if (SearchHelper.IsElasticSearchEnabled)
        {
            Log.Logger.Information("Using Elasticsearch (external) search index backend");

            ElasticSearchIndex.Uri = new Uri(SearchHelper.ElasticSearchUri);
            ElasticSearchIndex.IndexName = SearchHelper.ElasticSearchIndexName;
            services.AddSingleton<ISearchIndex, ElasticSearchIndex>();
        }
        else
        {
            Log.Logger.Information("Using Lucene (embedded) search index backend");

            services.AddSingleton<ISearchIndex, LuceneSearchIndex>();
        }
        services.AddSingleton<IScannerProxyService, ScannerProxyService>();
        services.AddSingleton<IScriptedPlayoutBuilderService, ScriptedPlayoutBuilderService>();
        services.AddSingleton<IFFmpegSegmenterService, FFmpegSegmenterService>();
        services.AddSingleton<ITempFilePool, TempFilePool>();
        services.AddSingleton<IHlsPlaylistFilter, HlsPlaylistFilter>();
        services.AddSingleton<RecyclableMemoryStreamManager>();
        services.AddSingleton<SystemStartup>();
        services.AddSingleton<ILanguageCodeCache, LanguageCodeCache>();
        AddChannel<IBackgroundServiceRequest>(services);
        AddChannel<IPlexBackgroundServiceRequest>(services);
        AddChannel<IJellyfinBackgroundServiceRequest>(services);
        AddChannel<IEmbyBackgroundServiceRequest>(services);
        AddChannel<IFFmpegWorkerRequest>(services);
        AddChannel<ISearchIndexBackgroundServiceRequest>(services);
        AddChannel<IScannerBackgroundServiceRequest>(services);

        services.AddScoped<IMacOsConfigFolderHealthCheck, MacOsConfigFolderHealthCheck>();
        services.AddScoped<IFFmpegVersionHealthCheck, FFmpegVersionHealthCheck>();
        services.AddScoped<IFFmpegCapabilitiesHealthCheck, FFmpegCapabilitiesHealthCheck>();
        services.AddScoped<IFFmpegReportsHealthCheck, FFmpegReportsHealthCheck>();
        services.AddScoped<IHardwareAccelerationHealthCheck, HardwareAccelerationHealthCheck>();
        services.AddScoped<IMovieMetadataHealthCheck, MovieMetadataHealthCheck>();
        services.AddScoped<IEpisodeMetadataHealthCheck, EpisodeMetadataHealthCheck>();
        services.AddScoped<IZeroDurationHealthCheck, ZeroDurationHealthCheck>();
        services.AddScoped<IFileNotFoundHealthCheck, FileNotFoundHealthCheck>();
        services.AddScoped<IUnavailableHealthCheck, UnavailableHealthCheck>();
        services.AddScoped<IVaapiDriverHealthCheck, VaapiDriverHealthCheck>();
        services.AddScoped<IUnifiedDockerHealthCheck, UnifiedDockerHealthCheck>();
        services.AddScoped<IDowngradeHealthCheck, DowngradeHealthCheck>();
        services.AddScoped<IEmptyScheduleHealthCheck, EmptyScheduleHealthCheck>();
        services.AddScoped<IHealthCheckService, HealthCheckService>();

        services.AddScoped<IChannelRepository, ChannelRepository>();
        services.AddScoped<IFFmpegProfileRepository, FFmpegProfileRepository>();
        services.AddScoped<IMediaSourceRepository, MediaSourceRepository>();
        services.AddScoped<IMediaItemRepository, MediaItemRepository>();
        services.AddScoped<IMediaCollectionRepository, MediaCollectionRepository>();
        services.AddScoped<IConfigElementRepository, ConfigElementRepository>();
        services.AddScoped<ITelevisionRepository, TelevisionRepository>();
        services.AddScoped<ISearchRepository, SearchRepository>();
        services.AddScoped<IMovieRepository, MovieRepository>();
        services.AddScoped<IArtistRepository, ArtistRepository>();
        services.AddScoped<IMusicVideoRepository, MusicVideoRepository>();
        services.AddScoped<IOtherVideoRepository, OtherVideoRepository>();
        services.AddScoped<ISongRepository, SongRepository>();
        services.AddScoped<IImageRepository, ImageRepository>();
        services.AddScoped<IRemoteStreamRepository, RemoteStreamRepository>();
        services.AddScoped<ILibraryRepository, LibraryRepository>();
        services.AddScoped<IMetadataRepository, MetadataRepository>();
        services.AddScoped<IArtworkRepository, ArtworkRepository>();
        services.AddScoped<ICollectionEtag, CollectionEtag>();
        services.AddScoped<IFFmpegLocator, FFmpegLocator>();
        services.AddScoped<IFallbackMetadataProvider, FallbackMetadataProvider>();
        services.AddScoped<ILocalStatisticsProvider, LocalStatisticsProvider>();
        services.AddScoped<IExternalJsonPlayoutItemProvider, ExternalJsonPlayoutItemProvider>();
        services.AddScoped<IPlayoutBuilder, PlayoutBuilder>();
        services.AddScoped<IBlockPlayoutBuilder, BlockPlayoutBuilder>();
        services.AddScoped<IBlockPlayoutPreviewBuilder, BlockPlayoutPreviewBuilder>();
        services.AddScoped<IBlockPlayoutFillerBuilder, BlockPlayoutFillerBuilder>();
        services.AddScoped<ISequentialPlayoutBuilder, SequentialPlayoutBuilder>();
        services.AddScoped<IScriptedPlayoutBuilder, ScriptedPlayoutBuilder>();
        services.AddScoped<ISchedulingEngine, SchedulingEngine>();
        services.AddScoped<IExternalJsonPlayoutBuilder, ExternalJsonPlayoutBuilder>();
        services.AddScoped<IPlayoutTimeShifter, PlayoutTimeShifter>();
        services.AddScoped<IImageCache, ImageCache>();
        services.AddScoped<ILocalFileSystem, LocalFileSystem>();
        services.AddScoped<IPlexServerApiClient, PlexServerApiClient>();
        services.AddScoped<IPlexMovieRepository, PlexMovieRepository>();
        services.AddScoped<IPlexTelevisionRepository, PlexTelevisionRepository>();
        services.AddScoped<IPlexCollectionRepository, PlexCollectionRepository>();
        services.AddScoped<IPlexMetadataRepository, PlexMetadataRepository>();
        services.AddScoped<IJellyfinApiClient, JellyfinApiClient>();
        services.AddScoped<IJellyfinPathReplacementService, JellyfinPathReplacementService>();
        services.AddScoped<IJellyfinTelevisionRepository, JellyfinTelevisionRepository>();
        services.AddScoped<IJellyfinCollectionRepository, JellyfinCollectionRepository>();
        services.AddScoped<IJellyfinMovieRepository, JellyfinMovieRepository>();
        services.AddScoped<IEmbyApiClient, EmbyApiClient>();
        services.AddScoped<IEmbyPathReplacementService, EmbyPathReplacementService>();
        services.AddScoped<IEmbyTelevisionRepository, EmbyTelevisionRepository>();
        services.AddScoped<IEmbyCollectionRepository, EmbyCollectionRepository>();
        services.AddScoped<IEmbyMovieRepository, EmbyMovieRepository>();
        services.AddScoped<IRuntimeInfo, RuntimeInfo>();
        services.AddScoped<IPlexPathReplacementService, PlexPathReplacementService>();
        services.AddScoped<ICustomStreamSelector, CustomStreamSelector>();
        services.AddScoped<IFFmpegStreamSelector, FFmpegStreamSelector>();
        services.AddScoped<IStreamSelectorRepository, StreamSelectorRepository>();
        services.AddScoped<IHardwareCapabilitiesFactory, HardwareCapabilitiesFactory>();
        services.AddScoped<IMultiEpisodeShuffleCollectionEnumeratorFactory,
            MultiEpisodeShuffleCollectionEnumeratorFactory>();
        services.AddScoped<IRerunHelper, RerunHelper>();
        services.AddScoped<IChannelLogoGenerator, ChannelLogoGenerator>();
        services.AddScoped<IGraphicsEngine, GraphicsEngine>();
        services.AddScoped<IGraphicsElementRepository, GraphicsElementRepository>();
        services.AddScoped<ITemplateDataRepository, TemplateDataRepository>();
        services.AddScoped<IGraphicsElementLoader, GraphicsElementLoader>();
        services.AddScoped<TemplateFunctions>();
        services.AddScoped<IDecoSelector, DecoSelector>();
        services.AddScoped<IWatermarkSelector, WatermarkSelector>();
        services.AddScoped<IGraphicsElementSelector, GraphicsElementSelector>();
        services.AddScoped<IHlsInitSegmentCache, HlsInitSegmentCache>();
        services.AddScoped<IMpegTsScriptService, MpegTsScriptService>();
        services.AddScoped<ILanguageCodeService, LanguageCodeService>();
        services.AddScoped<IPlayoutItemConverter, PlayoutItemConverter>();
        services.AddScoped<IDynamicPlayoutItemService, DynamicPlayoutItemService>();

        services.AddScoped<IFFmpegProcessService, FFmpegLibraryProcessService>();
        services.AddScoped<IPipelineBuilderFactory, PipelineBuilderFactory>();
        services.AddScoped<FFmpegProcessService>();

        services.AddScoped<ISongVideoGenerator, SongVideoGenerator>();
        services.AddScoped<IMusicVideoCreditsGenerator, MusicVideoCreditsGenerator>();
        services.AddScoped<IHtmlSanitizer, HtmlSanitizer>(_ =>
        {
            var sanitizer = new HtmlSanitizer();
            sanitizer.AllowedAttributes.Add("class");
            return sanitizer;
        });
        services.AddScoped<IJellyfinSecretStore, JellyfinSecretStore>();
        services.AddScoped<IEmbySecretStore, EmbySecretStore>();
        services.AddScoped<IScriptEngine, ScriptEngine>();
        services.AddScoped<ISequentialScheduleValidator, SequentialScheduleValidator>();

        services.AddScoped<PlexEtag>();

        // services.AddTransient(typeof(IRequestHandler<,>), typeof(GetRecentLogEntriesHandler<>));

        services.AddTransient<SlowApiHandler>();
        services.AddTransient<SlowQueryInterceptor>();

        // run-once/blocking startup services
        services.AddHostedService<EndpointValidatorService>();
        services.AddHostedService<DatabaseMigratorService>();
        services.AddHostedService<DatabaseCleanerService>();
        services.AddHostedService<LoadLoggingLevelService>();
        services.AddHostedService<CacheCleanerService>();
        services.AddHostedService<ResourceExtractorService>();
        services.AddHostedService<PlatformSettingsService>();
        services.AddHostedService<RebuildSearchIndexService>();
        services.AddHostedService<RunHealthChecksService>();

        // background services
#if !DEBUG_NO_SYNC
        services.AddHostedService<EmbyService>();
        services.AddHostedService<JellyfinService>();
        services.AddHostedService<PlexService>();
        services.AddHostedService<ScannerService>();
#endif
        services.AddHostedService<FFmpegLocatorService>();
        services.AddHostedService<WorkerService>();
        services.AddHostedService<SchedulerService>();
        services.AddHostedService<FFmpegWorkerService>();
        services.AddHostedService<SearchIndexService>();
    }

    private static void AddChannel<TMessageType>(IServiceCollection services)
    {
        services.AddSingleton(
            Channel.CreateUnbounded<TMessageType>(new UnboundedChannelOptions { SingleReader = true }));
        services.AddSingleton(provider => provider.GetRequiredService<Channel<TMessageType>>().Reader);
        services.AddSingleton(provider => provider.GetRequiredService<Channel<TMessageType>>().Writer);
    }

    private static void CopyMacOsConfigFolderIfNeeded()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return;
        }

        bool newDbExists = File.Exists(FileSystemLayout.DatabasePath);
        if (newDbExists)
        {
            return;
        }

        bool oldDbExists = File.Exists(FileSystemLayout.MacOsOldDatabasePath);
        if (!oldDbExists)
        {
            return;
        }

        // safe to move here since
        //   - old db exists
        //   - new db does not exist

        Log.Logger.Information(
            "Migrating config data from {OldFolder} to {NewFolder}",
            FileSystemLayout.MacOsOldAppDataFolder,
            FileSystemLayout.AppDataFolder);

        try
        {
            // delete new config folder
            if (Directory.Exists(FileSystemLayout.AppDataFolder))
            {
                Directory.Delete(FileSystemLayout.AppDataFolder, true);
            }

            // move old config folder to new config folder
            Directory.Move(FileSystemLayout.MacOsOldAppDataFolder, FileSystemLayout.AppDataFolder);
        }
        catch (Exception ex)
        {
            Log.Logger.Warning(ex, "Failed to migrate config data");
        }
    }
}
