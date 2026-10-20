namespace UnitToko.Api;

public static class ReleaseProfile
{
    public static readonly string[] PilotReturnPaths = ["/", "/identity/account", "/workspace", "/katalog", "/shop", "/catalog/browse",
        "/ordering/requests", "/ordering/requests/incoming", "/administration/catalog", "/operations/health"];

    public static bool IsPilot(IConfiguration configuration) => configuration["Release:Profile"] switch
    {
        null or "release1" => true,
        "full" => false,
        _ => throw new InvalidOperationException("Release:Profile must be release1 or full.")
    };
}
