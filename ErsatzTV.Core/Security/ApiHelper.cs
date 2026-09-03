namespace ErsatzTV.Core.Security;

public static class ApiHelper
{
    public const string HeaderName = "X-Etv-Api-Key";
    public const string EnvironmentVariableName = "ETV_API_KEY";

    public static string ApiKey { get; set; }
}
