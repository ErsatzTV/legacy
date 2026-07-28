using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;

namespace ErsatzTV.Application.Configuration;

public class GetUiSettingsHandler(IConfigElementRepository configElementRepository)
    : IRequestHandler<GetUiSettings, UiSettingsViewModel>
{
    public async Task<UiSettingsViewModel> Handle(GetUiSettings request, CancellationToken cancellationToken)
    {
        Option<ThemeMode> pagesThemeMode = await configElementRepository.GetValue<ThemeMode>(
            ConfigElementKey.PagesThemeMode,
            cancellationToken);

        // fall back to the legacy dark mode flag for installs that predate the theme mode setting
        Option<bool> pagesIsDarkMode = await configElementRepository.GetValue<bool>(
            ConfigElementKey.PagesIsDarkMode,
            cancellationToken);

        Option<string> pagesLanguage = await configElementRepository.GetValue<string>(
            ConfigElementKey.PagesLanguage,
            cancellationToken);

        return new UiSettingsViewModel
        {
            ThemeMode = pagesThemeMode.IfNone(
                () => pagesIsDarkMode.Match(
                    isDarkMode => isDarkMode ? ThemeMode.Dark : ThemeMode.Light,
                    () => ThemeMode.Dark)),
            Language = await pagesLanguage.IfNoneAsync("en")
        };
    }
}
