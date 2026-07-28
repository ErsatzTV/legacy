using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;

namespace ErsatzTV.Application.Configuration;

public class UpdateUiSettingsHandler(IConfigElementRepository configElementRepository)
    : IRequestHandler<UpdateUiSettings, Either<BaseError, Unit>>
{
    public async Task<Either<BaseError, Unit>> Handle(
        UpdateUiSettings request,
        CancellationToken cancellationToken)
    {
        return await ApplyUpdate(request.UiSettings, cancellationToken);
    }

    private async Task<Unit> ApplyUpdate(UiSettingsViewModel uiSettings, CancellationToken cancellationToken)
    {
        await configElementRepository.Upsert(
            ConfigElementKey.PagesThemeMode,
            uiSettings.ThemeMode,
            cancellationToken);

        // the legacy dark mode flag is only used to migrate the theme mode setting
        await configElementRepository.Delete(ConfigElementKey.PagesIsDarkMode, cancellationToken);

        await configElementRepository.Upsert(ConfigElementKey.PagesLanguage, uiSettings.Language, cancellationToken);

        return Unit.Default;
    }
}
