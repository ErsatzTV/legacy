namespace ErsatzTV.Core;

public static class LegacyStreamingEngine
{
    // compile-time, not a user setting: a hotfix can turn legacy back on without a revert
    // property, not const: avoids CS0162 on gated code
    // troubleshooting ignores this so users can compare engines
    public static bool Enabled => false;
}
